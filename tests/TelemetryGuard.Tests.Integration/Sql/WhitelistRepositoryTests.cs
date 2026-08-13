using Dapper;
using Microsoft.Extensions.DependencyInjection;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Data.Models;
using TelemetryGuard.Data.Repositories;

namespace TelemetryGuard.Tests.Integration.Sql;

/// <summary>
/// DAT-07 IWhitelistRepository against real migrated SQL Server + real Redis:
/// the Redis cache-set contract t:{tenantId}:wl:{sourceType} (SISMEMBER + 1 h TTL),
/// MERGE idempotency on the natural key, expiry semantics, the D19 negative-label
/// emission for review-screen adds, and Redis-outage resilience (SQL is the source
/// of truth; cache failures never fail the write).
/// </summary>
[Collection("sqlserver")]
[Trait("Category", "Integration")]
public sealed class WhitelistRepositoryTests(SqlServerFixture fx)
{
    private static string FreshValue(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static NewWhitelistEntry Manual(string sourceType, string value, DateTime? expiresUtc = null,
        string? reason = null)
        => new(sourceType, value, reason, "manual", "tester", expiresUtc);

    private sealed class FakeLabelSink : ILabelSink
    {
        public List<LabelEvent> Events { get; } = [];

        public ValueTask WriteAsync(LabelEvent label, CancellationToken ct)
        {
            Events.Add(label);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task AddAsync_inserts_and_populates_redis_set()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();

        var value = FreshValue("ip");
        var id = await repo.AddAsync(Manual("ip", value, reason: "real customer"), CancellationToken.None);
        Assert.True(id > 0);

        var db = fx.Redis.GetDatabase();
        var key = SqlServerFixture.WhitelistKey(SqlServerFixture.TenantA, "ip");
        Assert.True(await db.SetContainsAsync(key, value)); // SISMEMBER = 1

        var ttl = await db.KeyTimeToLiveAsync(key);
        Assert.NotNull(ttl);
        Assert.True(ttl > TimeSpan.Zero && ttl <= TimeSpan.FromHours(1),
            $"whitelist key TTL must be in (0, 1h], was {ttl}");
    }

    [Fact]
    public async Task AddAsync_is_idempotent_on_natural_key()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();

        var value = FreshValue("ip");
        var expires = DateTime.UtcNow.AddDays(7);
        var id1 = await repo.AddAsync(Manual("ip", value, reason: "first"), CancellationToken.None);
        var id2 = await repo.AddAsync(Manual("ip", value, expiresUtc: expires, reason: "second"),
            CancellationToken.None);

        Assert.Equal(id1, id2); // same Id returned twice — MERGE, not a second insert

        await using var conn = await fx.OpenAsync(SqlServerFixture.TenantA);
        var rows = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM dbo.WhitelistEntries
            WHERE TenantId = @TenantId AND SourceType = 'ip' AND Value = @Value
            """,
            new { TenantId = SqlServerFixture.TenantA, Value = value });
        Assert.Equal(1, rows);

        // The second call updated Reason/ExpiresUtc on the existing row.
        var entry = await repo.GetByIdAsync(id1, CancellationToken.None);
        Assert.Equal("second", entry!.Reason);
        Assert.NotNull(entry.ExpiresUtc);
    }

    [Fact]
    public async Task RemoveAsync_deletes_and_removes_from_redis()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();

        var value = FreshValue("ip");
        await repo.AddAsync(Manual("ip", value), CancellationToken.None);

        var db = fx.Redis.GetDatabase();
        var key = SqlServerFixture.WhitelistKey(SqlServerFixture.TenantA, "ip");
        Assert.True(await db.SetContainsAsync(key, value));

        Assert.True(await repo.RemoveAsync("ip", value, CancellationToken.None));
        Assert.False(await db.SetContainsAsync(key, value)); // member gone after rebuild
        Assert.False(await repo.RemoveAsync("ip", value, CancellationToken.None)); // already gone
    }

    [Fact]
    public async Task GetByIdAsync_returns_entry_and_null_for_unknown_id()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();

        var value = FreshValue("fingerprint");
        var id = await repo.AddAsync(Manual("fingerprint", value), CancellationToken.None);

        var entry = await repo.GetByIdAsync(id, CancellationToken.None);
        Assert.NotNull(entry);
        Assert.Equal(id, entry.Id);
        Assert.Equal(SqlServerFixture.TenantA, entry.TenantId);
        Assert.Equal("fingerprint", entry.SourceType);
        Assert.Equal(value, entry.Value);
        Assert.Equal("manual", entry.Source);
        Assert.Equal("tester", entry.CreatedBy);

        Assert.Null(await repo.GetByIdAsync(long.MaxValue - 7, CancellationToken.None));
    }

    [Fact]
    public async Task RemoveByIdAsync_deletes_and_rebuilds_cache_then_returns_false()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();

        var value = FreshValue("device_id");
        var id = await repo.AddAsync(Manual("device_id", value), CancellationToken.None);

        var db = fx.Redis.GetDatabase();
        var key = SqlServerFixture.WhitelistKey(SqlServerFixture.TenantA, "device_id");
        Assert.True(await db.SetContainsAsync(key, value));

        Assert.True(await repo.RemoveByIdAsync(id, CancellationToken.None));
        Assert.False(await db.SetContainsAsync(key, value)); // the right sourceType set was rebuilt
        Assert.Null(await repo.GetByIdAsync(id, CancellationToken.None));
        Assert.False(await repo.RemoveByIdAsync(id, CancellationToken.None)); // nonexistent id
    }

    [Fact]
    public async Task ListAsync_filters_by_sourceType()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();

        var deviceValue = FreshValue("dev");
        var fingerprintValue = FreshValue("fp");
        await repo.AddAsync(Manual("device_id", deviceValue), CancellationToken.None);
        await repo.AddAsync(Manual("fingerprint", fingerprintValue), CancellationToken.None);

        var deviceRows = await repo.ListAsync("device_id", 0, 200, CancellationToken.None);
        Assert.Contains(deviceRows, e => e.Value == deviceValue);
        Assert.DoesNotContain(deviceRows, e => e.Value == fingerprintValue);
        Assert.All(deviceRows, e => Assert.Equal("device_id", e.SourceType));

        var allRows = await repo.ListAsync(null, 0, 200, CancellationToken.None);
        Assert.Contains(allRows, e => e.Value == deviceValue);
        Assert.Contains(allRows, e => e.Value == fingerprintValue);
    }

    [Fact]
    public async Task AreWhitelistedAsync_batch_reports_membership_and_expired_as_false()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();

        var live = FreshValue("ip");
        var expired = FreshValue("ip");
        var unknown = FreshValue("ip");
        await repo.AddAsync(Manual("ip", live), CancellationToken.None);
        await repo.AddAsync(Manual("ip", expired, expiresUtc: DateTime.UtcNow.AddHours(-1)),
            CancellationToken.None);

        var result = await repo.AreWhitelistedAsync(
            "ip", new[] { live, expired, unknown }, CancellationToken.None);

        Assert.Equal(3, result.Count); // every requested value gets an answer
        Assert.True(result[live]);
        Assert.False(result[expired]); // expired counts as false
        Assert.False(result[unknown]);
    }

    [Fact]
    public async Task RebuildCacheAsync_omits_expired_entries()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();

        var live = FreshValue("ip");
        var expired = FreshValue("ip");
        await repo.AddAsync(Manual("ip", live), CancellationToken.None);
        await repo.AddAsync(Manual("ip", expired, expiresUtc: DateTime.UtcNow.AddMinutes(-5)),
            CancellationToken.None);

        await repo.RebuildCacheAsync("ip", CancellationToken.None);

        var db = fx.Redis.GetDatabase();
        var key = SqlServerFixture.WhitelistKey(SqlServerFixture.TenantA, "ip");
        Assert.True(await db.SetContainsAsync(key, live));
        Assert.False(await db.SetContainsAsync(key, expired)); // non-expired values only
    }

    [Fact]
    public async Task AddAsync_review_screen_emits_negative_label()
    {
        var sink = new FakeLabelSink();
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA, sink);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();

        var value = FreshValue("ip");
        var sessionId = $"sess-{Guid.NewGuid():N}";
        var id = await repo.AddAsync(
            new NewWhitelistEntry("ip", value, "marked real customer", "review_screen",
                "reviewer@example.com", null, sessionId),
            CancellationToken.None);
        Assert.True(id > 0);

        var label = Assert.Single(sink.Events); // exactly one LabelEvent
        Assert.Equal(LabelValues.Legit, label.Label);
        Assert.Equal(LabelSources.ReviewScreen, label.LabelSource);
        Assert.Equal(sessionId, label.SessionId);
        Assert.Equal(new TenantId(SqlServerFixture.TenantA), label.TenantId);
    }

    [Fact]
    public async Task AddAsync_manual_emits_no_label_and_review_screen_without_session_emits_none()
    {
        var sink = new FakeLabelSink();
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA, sink);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();

        // Manual add — even with a SessionId — emits nothing.
        var manualId = await repo.AddAsync(
            new NewWhitelistEntry("ip", FreshValue("ip"), null, "manual", null, null,
                SessionId: $"sess-{Guid.NewGuid():N}"),
            CancellationToken.None);
        Assert.True(manualId > 0);
        Assert.Empty(sink.Events);

        // review_screen WITHOUT a SessionId: no label (nothing to join on) but the add succeeds.
        var noSessionId = await repo.AddAsync(
            new NewWhitelistEntry("ip", FreshValue("ip"), null, "review_screen", null, null),
            CancellationToken.None);
        Assert.True(noSessionId > 0);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task AddAsync_review_screen_without_registered_sink_still_succeeds()
    {
        // No ILabelSink in the container (sink registration arrives with ANA-05):
        // the optional constructor dependency stays null and the add must not throw.
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();

        var id = await repo.AddAsync(
            new NewWhitelistEntry("ip", FreshValue("ip"), null, "review_screen", null, null,
                SessionId: $"sess-{Guid.NewGuid():N}"),
            CancellationToken.None);
        Assert.True(id > 0);
    }

    [Fact]
    public async Task AddAsync_survives_redis_outage()
    {
        // Closed port + abortConnect=false: the multiplexer constructs but every
        // command fails. The SQL write must still succeed (cache is best-effort).
        await using var provider = RepositoryFactory.BuildServices(
            fx, SqlServerFixture.TenantA,
            redisConnectionString: "127.0.0.1:1,abortConnect=false,connectTimeout=250,connectRetry=0");
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();

        var value = FreshValue("ip");
        var id = await repo.AddAsync(Manual("ip", value), CancellationToken.None); // no throw
        Assert.True(id > 0);

        await using var conn = await fx.OpenAsync(SqlServerFixture.TenantA);
        var rows = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM dbo.WhitelistEntries
            WHERE TenantId = @TenantId AND SourceType = 'ip' AND Value = @Value
            """,
            new { TenantId = SqlServerFixture.TenantA, Value = value });
        Assert.Equal(1, rows); // SQL row exists — source of truth unharmed
    }
}
