using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using TelemetryGuard.Data;
using TelemetryGuard.Data.Repositories;

namespace TelemetryGuard.Tests.Integration.Sql;

/// <summary>
/// The §9/D11 RLS proofs — raw connections from the fixture so no repository code can
/// mask database behavior. Semantics under test, verbatim from the spec:
/// (a) cross-tenant READ is EMPTY, not an error (FILTER silently filters — asserting a
///     throw here would mask a broken FILTER predicate);
/// (b) mismatched WRITE on a stamped session THROWS (BLOCK predicate — a silent no-op
///     would mask a broken BLOCK predicate);
/// (c) an unstamped session sees ZERO rows in every RLS-protected table;
/// (d) the SYSTEM sentinel session sees ALL tenants;
/// (e) the resolution tables dbo.ApiKeys / dbo.Sites have no FILTER predicate and are
///     readable unstamped (their BLOCK predicate — 0013 — is proven in RlsPrincipalTests,
///     together with the principal-bound sentinel and the structural coverage check).
/// </summary>
[Collection("sqlserver")]
[Trait("Category", "Integration")]
public sealed class RlsProofTests(SqlServerFixture fx)
{
    [Fact] // (a) cross-tenant read is EMPTY, not an error
    public async Task Read_as_B_of_A_rows_returns_empty()
    {
        await using var asB = await fx.OpenAsync(SqlServerFixture.TenantB);
        var rows = await asB.QueryAsync(
            "SELECT * FROM dbo.Campaigns WHERE TenantId = @tid",
            new { tid = SqlServerFixture.TenantA });
        Assert.Empty(rows); // seeded campaign exists for A; B sees nothing, no exception
    }

    [Fact] // (b) BLOCK predicate: mismatched INSERT under a stamped context throws
    public async Task Insert_with_mismatched_TenantId_throws()
    {
        await using var asA = await fx.OpenAsync(SqlServerFixture.TenantA);
        await Assert.ThrowsAsync<SqlException>(() => asA.ExecuteAsync(
            """
            INSERT INTO dbo.Campaigns (TenantId, CampaignId, Platform, LandingUrl)
            VALUES (@tid, NEWID(), 'google', N'https://b.example.com/x')
            """,
            new { tid = SqlServerFixture.TenantB }));
    }

    [Fact] // (c) unstamped connection sees zero rows in every RLS table
    public async Task Unstamped_connection_sees_zero_rows()
    {
        await using var raw = await fx.OpenAsync(null);
        foreach (var table in new[] { "dbo.Tenants", "dbo.Campaigns",
            "dbo.VerdictDailySummaries", "dbo.FlaggedSourcesDaily",
            "dbo.ExclusionQueue", "dbo.RollupWatermarks", "dbo.WhitelistEntries",
            "dbo.PublisherDailySummaries", "dbo.SiteDailySummaries",
            "dbo.EnforcementAudit", "dbo.GoogleAdsPushedExclusions",
            "dbo.TenantPolicyAudit", "dbo.LabelSubmissions", "dbo.WebhookOutbox" })
        {
            var n = await raw.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {table}");
            Assert.Equal(0, n);
        }
    }

    [Fact] // P2-01: the same RLS pattern (a + b) proven for the new aggregate tables
    public async Task PublisherAndSite_summaries_are_RLS_protected()
    {
        var placement = $"rls-test-{Guid.NewGuid():N}.example";
        var siteKey = $"rls-{Guid.NewGuid():N}"[..20];

        // Seed directly (not through the repository) so this exercises the RLS
        // predicates themselves, not the repository's own ambient-tenant guard.
        await using (var asA = await fx.OpenAsync(SqlServerFixture.TenantA))
        {
            await asA.ExecuteAsync(
                "INSERT INTO dbo.PublisherDailySummaries (TenantId, [Date], Placement) " +
                "VALUES (@TenantId, CAST(SYSUTCDATETIME() AS date), @Placement)",
                new { TenantId = SqlServerFixture.TenantA, Placement = placement });
            await asA.ExecuteAsync(
                "INSERT INTO dbo.SiteDailySummaries (TenantId, [Date], SiteKey) " +
                "VALUES (@TenantId, CAST(SYSUTCDATETIME() AS date), @SiteKey)",
                new { TenantId = SqlServerFixture.TenantA, SiteKey = siteKey });
        }

        // (a) cross-tenant read is EMPTY, not an error
        await using var asB = await fx.OpenAsync(SqlServerFixture.TenantB);
        var pubRows = await asB.QueryAsync(
            "SELECT * FROM dbo.PublisherDailySummaries WHERE TenantId = @tid",
            new { tid = SqlServerFixture.TenantA });
        Assert.Empty(pubRows);
        var siteRows = await asB.QueryAsync(
            "SELECT * FROM dbo.SiteDailySummaries WHERE TenantId = @tid",
            new { tid = SqlServerFixture.TenantA });
        Assert.Empty(siteRows);

        // (b) BLOCK predicate: mismatched INSERT under a stamped session throws
        await Assert.ThrowsAsync<SqlException>(() => asB.ExecuteAsync(
            "INSERT INTO dbo.PublisherDailySummaries (TenantId, [Date], Placement) " +
            "VALUES (@tid, CAST(SYSUTCDATETIME() AS date), 'mismatched.example')",
            new { tid = SqlServerFixture.TenantA }));
        await Assert.ThrowsAsync<SqlException>(() => asB.ExecuteAsync(
            "INSERT INTO dbo.SiteDailySummaries (TenantId, [Date], SiteKey) " +
            "VALUES (@tid, CAST(SYSUTCDATETIME() AS date), 'mismatched-site')",
            new { tid = SqlServerFixture.TenantA }));
    }

    [Fact] // (d) SYSTEM sentinel sees all tenants
    public async Task System_sentinel_sees_all_rows()
    {
        await using var sys = await fx.OpenAsync(WellKnownTenants.System);
        var n = await sys.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Tenants");
        Assert.True(n >= 2); // both seeded tenants visible
    }

    [Fact] // (e) resolution tables are exempt: readable unstamped
    public async Task Resolution_tables_readable_unstamped()
    {
        await using var raw = await fx.OpenAsync(null);
        Assert.True(await raw.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Sites") >= 1);
        Assert.True(await raw.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.ApiKeys") >= 1);
    }

    [Fact] // D10 journal: a second migration run against the migrated DB is a no-op
    public void Second_migration_run_is_a_noop()
    {
        Assert.Equal(0, TelemetryGuard.MigrationRunner.Migrations.RunSqlServer(fx.ConnectionString));
    }

    [Fact] // companion: isolation holds through the full factory + repository path
    public async Task Repository_path_as_B_returns_null_for_A_campaign()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantB);
        using var scope = provider.CreateScope();
        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignRepository>();

        // Empty/null, not an error — RLS FILTER + the explicit @TenantId predicate agree.
        Assert.Null(await campaigns.GetAsync(SqlServerFixture.CampaignA1, CancellationToken.None));
        Assert.Null(await campaigns.GetRedirectAsync(SqlServerFixture.CampaignA1, CancellationToken.None));
    }
}
