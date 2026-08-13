using Dapper;
using Microsoft.Extensions.DependencyInjection;
using TelemetryGuard.Data.Repositories;

namespace TelemetryGuard.Tests.Integration.Sql;

/// <summary>
/// API-06 IExclusionQueueRepository against real migrated SQL Server. DAT-06 ships
/// dbo.ExclusionQueue and its CHECK constraints only ("API-06 (writer) owns its
/// access path") — this suite proves the writer's C# guards, a basic insert +
/// read-back, and the RLS cross-tenant proof the D21 exclusion-sync flow depends on.
/// </summary>
[Collection("sqlserver")]
[Trait("Category", "Integration")]
public sealed class ExclusionQueueRepositoryTests(SqlServerFixture fx)
{
    [Fact]
    public async Task EnqueueAsync_insert_is_readable_on_the_same_tenant_connection()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IExclusionQueueRepository>();

        var ip = $"203.0.113.{Random.Shared.Next(1, 254)}-{Guid.NewGuid():N}";
        await repo.EnqueueAsync(new ExclusionQueueInsert(
            "google", "ip", ip, "score=90 rules=honeypot_touched", "approved",
            SqlServerFixture.CampaignA1), CancellationToken.None);

        await using var conn = await fx.OpenAsync(SqlServerFixture.TenantA);
        var row = await conn.QuerySingleOrDefaultAsync(
            "SELECT Platform, SourceType, Value, Reason, Status, CampaignScope " +
            "FROM dbo.ExclusionQueue WHERE TenantId = @TenantId AND Value = @Value",
            new { TenantId = SqlServerFixture.TenantA, Value = ip });

        Assert.NotNull(row);
        Assert.Equal("google", (string)row!.Platform);
        Assert.Equal("ip", (string)row.SourceType);
        Assert.Equal("approved", (string)row.Status);
        Assert.Equal(SqlServerFixture.CampaignA1, (Guid)row.CampaignScope);
        Assert.Contains("honeypot_touched", (string)row.Reason);
    }

    [Fact]
    public async Task EnqueueAsync_nullCampaignScope_meansTenantWide()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IExclusionQueueRepository>();

        var ip = $"203.0.113.{Random.Shared.Next(1, 254)}-{Guid.NewGuid():N}";
        await repo.EnqueueAsync(new ExclusionQueueInsert(
            "other", "ip", ip, "score=95 rules=honeypot_touched", "pending", null), CancellationToken.None);

        await using var conn = await fx.OpenAsync(SqlServerFixture.TenantA);
        var scopeValue = await conn.ExecuteScalarAsync<Guid?>(
            "SELECT CampaignScope FROM dbo.ExclusionQueue WHERE TenantId = @TenantId AND Value = @Value",
            new { TenantId = SqlServerFixture.TenantA, Value = ip });

        Assert.Null(scopeValue);
    }

    [Theory]
    [InlineData("bogus-platform", "ip", "pending")]
    [InlineData("google", "bogus-source", "pending")]
    [InlineData("google", "ip", "pushed")] // writer may only insert pending/approved
    public async Task EnqueueAsync_rejectsInvalidEnumValues_beforeSql(
        string platform, string sourceType, string status)
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IExclusionQueueRepository>();

        await Assert.ThrowsAsync<ArgumentException>(() => repo.EnqueueAsync(
            new ExclusionQueueInsert(platform, sourceType, "203.0.113.1", "reason", status, null),
            CancellationToken.None));
    }

    [Fact] // RLS proof, extends DAT-08's harness (BLOCK predicate)
    public async Task RawInsert_withMismatchedTenantId_onAStampedConnection_throws()
    {
        await using var asA = await fx.OpenAsync(SqlServerFixture.TenantA);
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => asA.ExecuteAsync(
            """
            INSERT INTO dbo.ExclusionQueue (TenantId, Platform, SourceType, Value, Reason, Status)
            VALUES (@tid, 'google', 'ip', '203.0.113.99', N'reason', 'approved')
            """,
            new { tid = SqlServerFixture.TenantB }));
    }

    [Fact] // RLS proof, extends DAT-08's harness (FILTER predicate: empty, not an error)
    public async Task Row_writtenForTenantA_isInvisibleOnATenantBStampedConnection()
    {
        await using var providerA = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scopeA = providerA.CreateScope();
        var repoA = scopeA.ServiceProvider.GetRequiredService<IExclusionQueueRepository>();

        var ip = $"203.0.113.{Random.Shared.Next(1, 254)}-{Guid.NewGuid():N}";
        await repoA.EnqueueAsync(new ExclusionQueueInsert(
            "google", "ip", ip, "score=90 rules=honeypot_touched", "approved", null), CancellationToken.None);

        await using var asB = await fx.OpenAsync(SqlServerFixture.TenantB);
        var rowsB = await asB.QueryAsync(
            "SELECT * FROM dbo.ExclusionQueue WHERE Value = @Value", new { Value = ip });
        Assert.Empty(rowsB);
    }
}
