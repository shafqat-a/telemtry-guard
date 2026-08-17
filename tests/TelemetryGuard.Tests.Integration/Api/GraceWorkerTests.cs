using Dapper;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Api.Options;
using TelemetryGuard.Api.Services;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Core.Time;
using TelemetryGuard.Data;
using TelemetryGuard.RiskEngine.Contracts;
using TelemetryGuard.RiskEngine.Pipeline;
using TelemetryGuard.Tests.Integration.Sql;

namespace TelemetryGuard.Tests.Integration.Api;

/// <summary>
/// API-06: the grace-period worker end to end, against REAL Redis + SQL Server
/// (DAT-08's SqlServerFixture runs both containers and applies every migration).
/// IScoringPipeline and IEventSink/ILabelSink are fakes — scoring and ClickHouse
/// writes belong to other tasks — but everything downstream of the pipeline call
/// (the t:{tid}:fin:{sid} SETNX claim, the exclusion-queue insert, the SQL
/// daily-summary MERGE, and RLS isolation) runs through the real VerdictFinalizer
/// against the real stores.
/// </summary>
[Collection("sqlserver")]
[Trait("Category", "Integration")]
public sealed class GraceWorkerTests(SqlServerFixture fx)
{
    private sealed class FakePipeline : IScoringPipeline
    {
        public readonly Dictionary<string, ScoringOutcome?> Responses = new();

        public Task<ScoringOutcome?> ScoreSessionAsync(
            string sessionId, ChallengeOutcome outcome = ChallengeOutcome.NotChallenged, CancellationToken ct = default)
            => Task.FromResult(Responses.GetValueOrDefault(sessionId));
    }

    private sealed class CapturingSink : IEventSink
    {
        public readonly List<ClickEvent> Events = [];

        public ValueTask WriteBatchAsync(ReadOnlyMemory<ClickEvent> events, CancellationToken ct)
        {
            lock (Events) Events.AddRange(events.ToArray());
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoOpLabelSink : ILabelSink
    {
        public ValueTask WriteAsync(LabelEvent label, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private static ScoringOutcome Outcome(int score, string[]? ruleHits = null)
        => new(new ScoreResult(score, ruleHits ?? Array.Empty<string>(), "test-scorer", 1),
               BandMapper.ToBand(score), Whitelisted: false, 1.0);

    /// <summary>Builds the REAL production object graph for the finalizer + worker:
    /// scoped TenantContext (mirroring Program.cs's own registration exactly, unlike
    /// RepositoryFactory's single fixed tenant — the grace worker resolves a fresh
    /// scope per claimed session), AddTelemetryGuardData()'s repositories against the
    /// fixture's SQL Server, and the SAME Redis multiplexer the test seeds through.</summary>
    private (ServiceProvider Provider, FakePipeline Pipeline, CapturingSink Sink) BuildProvider()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Main"] = fx.ConnectionString,
            ["ConnectionStrings:Redis"] = fx.RedisConnectionString,
        }).Build();

        var pipeline = new FakePipeline();
        var sink = new CapturingSink();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(cfg);
        // Registered BEFORE AddTelemetryGuardData so its TryAddSingleton keeps this
        // instance — the worker and the test's own seeding calls must share one
        // connection to the same real Redis container.
        services.AddSingleton<IConnectionMultiplexer>(fx.Redis);
        services.AddTelemetryGuardData();

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddSingleton<IScoringPipeline>(pipeline);
        services.AddSingleton<IEventSink>(sink);
        services.AddSingleton<ILabelSink, NoOpLabelSink>();
        services.AddSingleton<IOptions<ScoringBandOptions>>(Options.Create(new ScoringBandOptions()));
        services.AddSingleton<IOptions<RetentionOptions>>(Options.Create(new RetentionOptions()));
        services.AddScoped<IVerdictFinalizer, VerdictFinalizer>();

        return (services.BuildServiceProvider(), pipeline, sink);
    }

    private static async Task<T> PollAsync<T>(Func<Task<T>> probe, Func<T, bool> ready, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var last = await probe();
        while (!ready(last) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            last = await probe();
        }
        return last;
    }

    /// <summary>Seeds a fresh, test-private campaign for TenantA rather than reusing the
    /// fixture's shared CampaignA1 — the daily-summary row is keyed on (tenant, campaign,
    /// day), so sharing a campaign across tests in this class would let one test's
    /// increment bleed into another's Allowed/Challenged/Blocked assertions.</summary>
    private async Task<Guid> CreateCampaignAsync(string platform = "google")
    {
        var campaignId = Guid.NewGuid();
        await using var sys = await fx.OpenAsync(SqlServerFixture.TenantA);
        await sys.ExecuteAsync(
            "INSERT INTO dbo.Campaigns (TenantId, CampaignId, Platform, LandingUrl) " +
            "VALUES (@TenantId, @CampaignId, @Platform, N'https://a.example.com/lp')",
            new { TenantId = SqlServerFixture.TenantA, CampaignId = campaignId, Platform = platform });
        return campaignId;
    }

    [Fact]
    public async Task GraceExpiredSession_IsFinalizedByTheWorker_WithinAFewSeconds()
    {
        var (provider, pipeline, sink) = BuildProvider();
        await using var _ = provider;

        var tid = SqlServerFixture.TenantA.ToString("D");
        var sid = $"s{Guid.NewGuid():N}";
        var db = fx.Redis.GetDatabase();
        var campaignId = await CreateCampaignAsync();
        pipeline.Responses[sid] = Outcome(20); // allow band

        // Seed a past-due grace entry + the tracker's click-context hash, exactly as
        // API-02's /c leaves them (kind, ip, site_key, campaign_id — API-02 step 3.7).
        // No t:{tid}:sess:{sid} hash is written, so this is the has_js_beacon=0 path.
        var pastDeadline = DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeSeconds();
        await db.SortedSetAddAsync($"t:{tid}:grace", sid, pastDeadline);
        await db.SetAddAsync("grace:tenants", tid);
        await db.HashSetAsync($"t:{tid}:click:{sid}", new HashEntry[]
        {
            new("kind", "tracker"),
            new("ip", "203.0.113.5"),
            new("site_key", SqlServerFixture.SiteKeyA),
            new("campaign_id", campaignId.ToString("D")),
        });

        var worker = new VerdictFinalizerService(
            fx.Redis, provider.GetRequiredService<IServiceScopeFactory>(),
            SystemClock.Instance, NullLogger<VerdictFinalizerService>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            // Poll on the ACTUAL terminal side effect (the SQL summary row), not the
            // t:{tid}:fin:{sid} claim key — that key is SET as FinalizeAsync's very
            // FIRST step (the idempotency claim), long before the summary/exclusion
            // writes near the end of the method, so polling on it races the very
            // effects this test asserts on.
            await using var conn = await fx.OpenAsync(SqlServerFixture.TenantA);
            var row = await PollAsync(
                () => conn.QuerySingleOrDefaultAsync(
                    """
                    SELECT Allowed, Challenged, Blocked, ScoreSum, Events
                    FROM dbo.VerdictDailySummaries
                    WHERE TenantId = @TenantId AND CampaignId = @CampaignId AND [Date] = CAST(SYSUTCDATETIME() AS date)
                    """,
                    new { TenantId = SqlServerFixture.TenantA, CampaignId = campaignId }),
                r => r is not null,
                TimeSpan.FromSeconds(8));

            Assert.NotNull(row);
            Assert.Equal(1, (int)row!.Allowed);
            Assert.Equal(0, (int)row.Challenged);
            Assert.Equal(0, (int)row.Blocked);
            Assert.Equal(20L, (long)row.ScoreSum);
            Assert.Equal(1, (int)row.Events);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        // fin-key set (the idempotency claim survives finalization).
        Assert.True(await db.KeyExistsAsync($"t:{tid}:fin:{sid}"));

        // Grace ZSET no longer contains the sid; the tenant is pruned once its set empties.
        Assert.Null(await db.SortedSetScoreAsync($"t:{tid}:grace", sid));
        Assert.False(await db.SetContainsAsync("grace:tenants", tid));

        // Verdict event captured (has_js_beacon=0 — no sess hash was ever written).
        var evt = Assert.Single(sink.Events);
        Assert.Equal(EventKind.Verdict, evt.Kind);
        Assert.Equal(sid, evt.SessionId);
        Assert.Equal(20, evt.Score);
        Assert.False(evt.HasJsBeacon);
        Assert.False(string.IsNullOrEmpty(evt.ScorerVersion));
        Assert.Equal(1, evt.FeatureSetVersion);
        Assert.InRange(evt.RetentionDays, (ushort)30, (ushort)180);
    }

    [Fact]
    public async Task BeaconOnlySession_GraceWorker_EmitsVerdictWithBeaconAndEnrichmentFeatures()
    {
        var (provider, pipeline, sink) = BuildProvider();
        await using var _ = provider;

        var tid = SqlServerFixture.TenantA.ToString("D");
        var sid = $"s{Guid.NewGuid():N}";
        var db = fx.Redis.GetDatabase();
        var ip = "203.0.113.77";
        var features = new FraudFeatureVector
        {
            HasJsBeacon = true,
            IpProxyOrVpn = true,
            IpDatacenterAsn = true,
        };
        pipeline.Responses[sid] = new ScoringOutcome(
            new ScoreResult(45, [], "test-scorer", FraudFeatureVector.FeatureSetVersion),
            VerdictBand.Challenge, Whitelisted: false, DurationMs: 1, Features: features);

        // The exact state produced by a first organic POST /i: aggregate + HTTP
        // context, then a quiet-period deadline discovered by the worker.
        await db.HashSetAsync($"t:{tid}:sess:{sid}",
        [
            new("has_beacon", "1"),
            new("n_beacons", "1"),
            new("n_pv", "1"),
        ]);
        await db.HashSetAsync($"t:{tid}:click:{sid}",
        [
            new("kind", "beacon"),
            new("ip", ip),
            new("ua", "IntegrationBrowser/1.0"),
            new("site_key", SqlServerFixture.SiteKeyA),
        ]);
        await db.SortedSetAddAsync(
            $"t:{tid}:grace", sid, DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds());
        await db.SetAddAsync("grace:tenants", tid);

        var worker = new VerdictFinalizerService(
            fx.Redis, provider.GetRequiredService<IServiceScopeFactory>(),
            SystemClock.Instance, NullLogger<VerdictFinalizerService>.Instance);
        await worker.StartAsync(CancellationToken.None);
        ClickEvent? verdict;
        try
        {
            verdict = await PollAsync(
                () => Task.FromResult(sink.Events.SingleOrDefault(e => e.SessionId == sid)),
                e => e is not null,
                TimeSpan.FromSeconds(8));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.NotNull(verdict);
        Assert.Equal(EventKind.Verdict, verdict!.Kind);
        Assert.True(verdict.HasJsBeacon);
        Assert.Equal(ip, verdict.Ip);
        Assert.Equal(SqlServerFixture.SiteKeyA, verdict.SiteKey);
        Assert.Equal("challenge", verdict.Band);
        var persistedFeatures = JsonSerializer.Deserialize<FraudFeatureVector>(
            verdict.Features, FraudFeatureVectorJson.Options);
        Assert.NotNull(persistedFeatures);
        Assert.True(persistedFeatures!.IpProxyOrVpn);
        Assert.True(persistedFeatures.IpDatacenterAsn);
        Assert.Null(await db.SortedSetScoreAsync($"t:{tid}:grace", sid));
    }

    [Fact]
    public async Task BlockBandExclusionRow_IsInvisibleOnATenantBStampedConnection()
    {
        var (provider, pipeline, _) = BuildProvider();
        await using var _disp = provider;

        var tid = SqlServerFixture.TenantA.ToString("D");
        var sid = $"s{Guid.NewGuid():N}";
        var db = fx.Redis.GetDatabase();
        var campaignId = await CreateCampaignAsync("google");
        var ip = $"198.51.100.{Random.Shared.Next(1, 254)}-{Guid.NewGuid():N}"[..30]; // unique per run

        pipeline.Responses[sid] = Outcome(90, ["ip_datacenter_asn"]); // block band

        var pastDeadline = DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeSeconds();
        await db.SortedSetAddAsync($"t:{tid}:grace", sid, pastDeadline);
        await db.SetAddAsync("grace:tenants", tid);
        await db.HashSetAsync($"t:{tid}:click:{sid}", new HashEntry[]
        {
            new("kind", "tracker"),
            new("ip", ip),
            new("site_key", SqlServerFixture.SiteKeyA),
            new("campaign_id", campaignId.ToString("D")),
        });

        var worker = new VerdictFinalizerService(
            fx.Redis, provider.GetRequiredService<IServiceScopeFactory>(),
            SystemClock.Instance, NullLogger<VerdictFinalizerService>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            // Poll on the exclusion row itself — the actual terminal side effect —
            // rather than the fin-key (see the comment in the test above).
            await using var connA = await fx.OpenAsync(SqlServerFixture.TenantA);
            var rowA = await PollAsync(
                () => connA.QuerySingleOrDefaultAsync(
                    "SELECT Platform, SourceType, Value, Status FROM dbo.ExclusionQueue " +
                    "WHERE TenantId = @TenantId AND Value = @Value",
                    new { TenantId = SqlServerFixture.TenantA, Value = ip }),
                r => r is not null,
                TimeSpan.FromSeconds(8));

            // Tenant A, correctly stamped: the exclusion row IS visible (D21 AutoEnforce ->
            // 'approved'; the campaign above is seeded with Platform 'google').
            Assert.NotNull(rowA);
            Assert.Equal("google", (string)rowA!.Platform);
            Assert.Equal("ip", (string)rowA.SourceType);
            Assert.Equal("approved", (string)rowA.Status);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        // Tenant B, stamped: the RLS FILTER predicate makes tenant A's row invisible —
        // an empty result, not an error (extends DAT-08's cross-tenant proof pattern).
        await using var asB = await fx.OpenAsync(SqlServerFixture.TenantB);
        var rowsB = await asB.QueryAsync(
            "SELECT * FROM dbo.ExclusionQueue WHERE Value = @Value", new { Value = ip });
        Assert.Empty(rowsB);
    }
}
