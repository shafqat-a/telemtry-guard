using Microsoft.Extensions.DependencyInjection;
using TelemetryGuard.Data.Models;
using TelemetryGuard.Data.Repositories;

namespace TelemetryGuard.Tests.Integration.Sql;

/// <summary>
/// P2-01 IPublisherSummaryRepository against real migrated SQL Server. Modeled on
/// VerdictSummaryRepositoryTests.cs (DAT-06): the double-upsert cases prove
/// ABSOLUTE-VALUE MERGE semantics (never a sum), the ambient-tenant guard throws
/// before any SQL runs, and cross-tenant reads come back empty (the RLS proof
/// this suite exists for). Each test uses fresh dates / placement / site-key
/// values so tests can run in any order.
/// </summary>
[Collection("sqlserver")]
[Trait("Category", "Integration")]
public sealed class PublisherSummaryRepositoryTests(SqlServerFixture fx)
{
    [Fact]
    public async Task UpsertPlacementDailyAsync_inserts_then_updates_idempotently()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPublisherSummaryRepository>();

        var date = new DateOnly(2024, 3, 10);
        var placement = $"pub-{Guid.NewGuid():N}.example";
        var row = new PublisherDailySummaryRow(
            SqlServerFixture.TenantA, date, placement,
            Events: 17, Allowed: 10, Challenged: 5, Blocked: 2, ScoreSum: 850, NoJsBeaconCount: 3);

        await repo.UpsertPlacementDailyAsync(row, CancellationToken.None);
        await repo.UpsertPlacementDailyAsync(row, CancellationToken.None); // identical re-run

        var stored = await repo.GetTopPlacementsAsync(date, date, 1000, CancellationToken.None);
        var single = Assert.Single(stored, r => r.Placement == placement); // one row — no doubling
        Assert.Equal(17, single.Events);
        Assert.Equal(10, single.Allowed);
        Assert.Equal(5, single.Challenged);
        Assert.Equal(2, single.Blocked);
        Assert.Equal(850, single.ScoreSum);
        Assert.Equal(3, single.NoJsBeaconCount);
        Assert.Equal(date, single.FirstDay);
        Assert.Equal(date, single.LastDay);
    }

    [Fact]
    public async Task UpsertPlacementDailyAsync_second_call_with_different_values_overwrites_never_sums()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPublisherSummaryRepository>();

        var date = new DateOnly(2024, 3, 12);
        var placement = $"pub-{Guid.NewGuid():N}.example";

        await repo.UpsertPlacementDailyAsync(new PublisherDailySummaryRow(
            SqlServerFixture.TenantA, date, placement, 10, 5, 3, 2, 400, 1), CancellationToken.None);
        await repo.UpsertPlacementDailyAsync(new PublisherDailySummaryRow(
            SqlServerFixture.TenantA, date, placement, 99, 1, 1, 97, 9000, 0), CancellationToken.None);

        var stored = await repo.GetTopPlacementsAsync(date, date, 1000, CancellationToken.None);
        var single = Assert.Single(stored, r => r.Placement == placement);
        Assert.Equal(99, single.Events);     // the SECOND value, never 10 + 99
        Assert.Equal(9000, single.ScoreSum); // never 400 + 9000
    }

    [Fact]
    public async Task UpsertPlacementDailyAsync_rejects_foreign_tenant_row()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPublisherSummaryRepository>();

        var foreign = new PublisherDailySummaryRow(
            SqlServerFixture.TenantB, new DateOnly(2024, 3, 13), $"pub-{Guid.NewGuid():N}.example",
            1, 1, 0, 0, 5, 0);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.UpsertPlacementDailyAsync(foreign, CancellationToken.None));
    }

    [Fact]
    public async Task UpsertSiteDailyAsync_inserts_then_updates_idempotently()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPublisherSummaryRepository>();

        var date = new DateOnly(2024, 4, 15);
        var siteKey = $"site-{Guid.NewGuid():N}"[..20];
        var row = new SiteDailySummaryRow(
            SqlServerFixture.TenantA, date, siteKey,
            TotalEvents: 40, Events: 17, Allowed: 10, Challenged: 5, Blocked: 2,
            ScoreSum: 850, NoJsBeaconCount: 3);

        await repo.UpsertSiteDailyAsync(row, CancellationToken.None);
        await repo.UpsertSiteDailyAsync(row, CancellationToken.None); // identical re-run

        var stored = await repo.GetSiteDailyAsync(date, date, CancellationToken.None);
        var single = Assert.Single(stored, r => r.SiteKey == siteKey);
        Assert.Equal(row, single);
    }

    [Fact]
    public async Task UpsertSiteDailyAsync_rejects_foreign_tenant_row()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPublisherSummaryRepository>();

        var foreign = new SiteDailySummaryRow(
            SqlServerFixture.TenantB, new DateOnly(2024, 4, 16), $"site-{Guid.NewGuid():N}"[..20],
            1, 1, 1, 0, 0, 5, 0);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.UpsertSiteDailyAsync(foreign, CancellationToken.None));
    }

    [Fact]
    public async Task GetTopPlacementsAsync_sums_across_days_orders_by_blocked_then_flagged_and_limits()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPublisherSummaryRepository>();

        var d1 = new DateOnly(2024, 5, 1);
        var d2 = new DateOnly(2024, 5, 2);
        var p1 = $"pub-{Guid.NewGuid():N}.example"; // Blocked 9,  Flagged 20 -> 3rd
        var p2 = $"pub-{Guid.NewGuid():N}.example"; // Blocked 9,  Flagged 30 -> 2nd (flagged tiebreak)
        var p3 = $"pub-{Guid.NewGuid():N}.example"; // Blocked 50, Flagged 1  -> 1st (blocked dominates)

        // p1: summed across two days -> Blocked 9 (4+5), Challenged 20 (11+9)
        await repo.UpsertPlacementDailyAsync(new PublisherDailySummaryRow(
            SqlServerFixture.TenantA, d1, p1, 50, 10, 11, 4, 100, 0), CancellationToken.None);
        await repo.UpsertPlacementDailyAsync(new PublisherDailySummaryRow(
            SqlServerFixture.TenantA, d2, p1, 50, 10, 9, 5, 100, 0), CancellationToken.None);
        await repo.UpsertPlacementDailyAsync(new PublisherDailySummaryRow(
            SqlServerFixture.TenantA, d1, p2, 50, 5, 30, 9, 100, 0), CancellationToken.None);
        await repo.UpsertPlacementDailyAsync(new PublisherDailySummaryRow(
            SqlServerFixture.TenantA, d1, p3, 51, 0, 1, 50, 100, 0), CancellationToken.None);

        var ordered = await repo.GetTopPlacementsAsync(d1, d2, 1000, CancellationToken.None);
        var subset = ordered.Where(r => r.Placement == p1 || r.Placement == p2 || r.Placement == p3).ToList();
        Assert.Equal(new[] { p3, p2, p1 }, subset.Select(r => r.Placement).ToArray());
        Assert.Equal(new DateOnly(2024, 5, 1), subset.First(r => r.Placement == p1).FirstDay);
        Assert.Equal(new DateOnly(2024, 5, 2), subset.First(r => r.Placement == p1).LastDay);

        var limited = await repo.GetTopPlacementsAsync(d1, d2, 2, CancellationToken.None);
        // The fixture's own concurrent seed rows (other test methods) may also fall in
        // this window, so only assert the TOP result and the exact count requested.
        Assert.Equal(2, limited.Count);
        Assert.Equal(p3, limited[0].Placement);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task GetTopPlacementsAsync_rejects_out_of_range_limit(int limit)
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPublisherSummaryRepository>();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repo.GetTopPlacementsAsync(new DateOnly(2024, 5, 1), new DateOnly(2024, 5, 1), limit, CancellationToken.None));
    }

    [Fact]
    public async Task GetSiteDailyAsync_filters_range_ordered_by_date_then_site_key()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPublisherSummaryRepository>();

        var siteA = $"site-{Guid.NewGuid():N}"[..20];
        var siteB = $"site-{Guid.NewGuid():N}"[..20];
        var d1 = new DateOnly(2024, 6, 1);
        var d2 = new DateOnly(2024, 6, 2);
        var d3 = new DateOnly(2024, 6, 3);

        // Insert deliberately out of date order to prove ORDER BY [Date], SiteKey.
        foreach (var (site, date) in new[] { (siteA, d3), (siteB, d1), (siteA, d1), (siteA, d2) })
        {
            await repo.UpsertSiteDailyAsync(new SiteDailySummaryRow(
                    SqlServerFixture.TenantA, date, site, 10, 5, 3, 1, 1, 100, 0),
                CancellationToken.None);
        }

        var rows = await repo.GetSiteDailyAsync(d1, d2, CancellationToken.None);

        Assert.Equal(3, rows.Count); // (siteA,d1) + (siteB,d1) + (siteA,d2); d3 outside range
        // Order by [Date] is deterministic; order between siteA/siteB on the SAME date
        // is not (both are random GUID-suffixed keys), so assert the d1 pair as a set.
        Assert.Equal(new[] { d1, d1, d2 }, rows.Select(r => r.Date).ToArray());
        Assert.Equal(new[] { siteA, siteB }.OrderBy(s => s, StringComparer.Ordinal),
            rows.Where(r => r.Date == d1).Select(r => r.SiteKey).OrderBy(s => s, StringComparer.Ordinal));
        Assert.Contains(rows, r => r.Date == d2 && r.SiteKey == siteA);
    }

    [Fact]
    public async Task Cross_tenant_read_returns_empty()
    {
        await using var providerA = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scopeA = providerA.CreateScope();
        var repoA = scopeA.ServiceProvider.GetRequiredService<IPublisherSummaryRepository>();

        var date = new DateOnly(2024, 7, 1);
        var placement = $"pub-{Guid.NewGuid():N}.example";
        var siteKey = $"site-{Guid.NewGuid():N}"[..20];
        await repoA.UpsertPlacementDailyAsync(new PublisherDailySummaryRow(
            SqlServerFixture.TenantA, date, placement, 1, 1, 0, 0, 10, 0), CancellationToken.None);
        await repoA.UpsertSiteDailyAsync(new SiteDailySummaryRow(
            SqlServerFixture.TenantA, date, siteKey, 1, 1, 1, 0, 0, 10, 0), CancellationToken.None);

        await using var providerB = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantB);
        using var scopeB = providerB.CreateScope();
        var repoB = scopeB.ServiceProvider.GetRequiredService<IPublisherSummaryRepository>();

        var placementsAsB = (await repoB.GetTopPlacementsAsync(date, date, 1000, CancellationToken.None))
            .Where(r => r.Placement == placement).ToList();
        Assert.Empty(placementsAsB); // RLS FILTER hides tenant A's row, no exception

        var sitesAsB = (await repoB.GetSiteDailyAsync(date, date, CancellationToken.None))
            .Where(r => r.SiteKey == siteKey).ToList();
        Assert.Empty(sitesAsB);
    }
}
