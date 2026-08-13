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
/// (e) the resolution tables dbo.ApiKeys / dbo.Sites are RLS-exempt and readable unstamped.
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
            "dbo.ExclusionQueue", "dbo.RollupWatermarks", "dbo.WhitelistEntries" })
        {
            var n = await raw.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {table}");
            Assert.Equal(0, n);
        }
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
