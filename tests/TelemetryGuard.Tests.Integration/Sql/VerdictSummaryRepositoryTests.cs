using Microsoft.Extensions.DependencyInjection;
using TelemetryGuard.Data.Models;
using TelemetryGuard.Data.Repositories;

namespace TelemetryGuard.Tests.Integration.Sql;

/// <summary>
/// DAT-06 IVerdictSummaryRepository against real migrated SQL Server. The double-upsert
/// cases prove ABSOLUTE-VALUE MERGE semantics: calling twice with the identical row
/// leaves exactly one row holding those values — no doubling — which is what makes
/// ANA-07 rollup re-runs (crash recovery, watermark replay) converge.
/// Each test uses fresh campaign ids / values and its own date window so tests can
/// run in any order.
/// </summary>
[Collection("sqlserver")]
[Trait("Category", "Integration")]
public sealed class VerdictSummaryRepositoryTests(SqlServerFixture fx)
{
    [Fact]
    public async Task UpsertDailySummaryAsync_inserts_then_updates_idempotently()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IVerdictSummaryRepository>();

        var campaignId = Guid.NewGuid();
        var date = new DateOnly(2024, 3, 10);
        var row = new VerdictDailySummaryRow(
            SqlServerFixture.TenantA, campaignId, date,
            Allowed: 10, Challenged: 5, Blocked: 2, ScoreSum: 850, Events: 17);

        await repo.UpsertDailySummaryAsync(row, CancellationToken.None);
        await repo.UpsertDailySummaryAsync(row, CancellationToken.None); // identical re-run

        var stored = await repo.GetDailySummariesAsync(campaignId, date, date, CancellationToken.None);
        var single = Assert.Single(stored);   // one row — MERGE matched, no duplicate
        Assert.Equal(row, single);            // absolute values — no doubling
    }

    [Fact]
    public async Task UpsertDailySummaryAsync_rejects_foreign_tenant_row()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IVerdictSummaryRepository>();

        var foreign = new VerdictDailySummaryRow(
            SqlServerFixture.TenantB, Guid.NewGuid(), new DateOnly(2024, 3, 11),
            Allowed: 1, Challenged: 0, Blocked: 0, ScoreSum: 5, Events: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.UpsertDailySummaryAsync(foreign, CancellationToken.None));
    }

    [Fact]
    public async Task UpsertFlaggedSourceAsync_upserts_idempotently()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IVerdictSummaryRepository>();

        var date = new DateOnly(2024, 4, 15);
        var value = $"ip-{Guid.NewGuid():N}";
        var row = new FlaggedSourceDailyRow(
            SqlServerFixture.TenantA, date, "ip", value,
            FlaggedCount: 5, BlockedCount: 3, ScoreSum: 400);

        await repo.UpsertFlaggedSourceAsync(row, CancellationToken.None);
        await repo.UpsertFlaggedSourceAsync(row, CancellationToken.None); // identical re-run

        var stored = (await repo.GetTopFlaggedSourcesAsync(date, date, 1000, CancellationToken.None))
            .Where(r => r.Value == value).ToList();
        var single = Assert.Single(stored); // one row, absolute values
        Assert.Equal(row, single);

        // Foreign-tenant row is refused before SQL, same as daily summaries.
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.UpsertFlaggedSourceAsync(
            row with { TenantId = SqlServerFixture.TenantB, Value = $"ip-{Guid.NewGuid():N}" },
            CancellationToken.None));
    }

    [Fact]
    public async Task GetDailySummariesAsync_filters_campaign_and_range_ordered_by_date()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IVerdictSummaryRepository>();

        var campaignX = Guid.NewGuid();
        var campaignY = Guid.NewGuid();
        var d1 = new DateOnly(2024, 6, 1);
        var d2 = new DateOnly(2024, 6, 2);
        var d3 = new DateOnly(2024, 6, 3);

        // Insert deliberately out of date order to prove ORDER BY [Date].
        foreach (var (campaign, date) in new[] { (campaignX, d3), (campaignX, d1), (campaignY, d2), (campaignX, d2) })
        {
            await repo.UpsertDailySummaryAsync(new VerdictDailySummaryRow(
                    SqlServerFixture.TenantA, campaign, date, 1, 1, 1, 100, 3),
                CancellationToken.None);
        }

        var rows = await repo.GetDailySummariesAsync(campaignX, d1, d2, CancellationToken.None);

        Assert.Equal(2, rows.Count);                       // d3 outside range; campaignY filtered out
        Assert.Equal(new[] { d1, d2 }, rows.Select(r => r.Date).ToArray()); // ascending by date
        Assert.All(rows, r => Assert.Equal(campaignX, r.CampaignId));
    }

    [Fact]
    public async Task GetTopFlaggedSourcesAsync_orders_by_blocked_then_flagged_and_limits()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IVerdictSummaryRepository>();

        // Date window unique to this test so the ordering assertions see only its rows.
        var date = new DateOnly(2024, 7, 1);
        var v1 = $"src-{Guid.NewGuid():N}"; // Blocked 9,  Flagged 20 -> 3rd
        var v2 = $"src-{Guid.NewGuid():N}"; // Blocked 9,  Flagged 30 -> 2nd (flagged tiebreak)
        var v3 = $"src-{Guid.NewGuid():N}"; // Blocked 50, Flagged 1  -> 1st (blocked dominates)

        await repo.UpsertFlaggedSourceAsync(new FlaggedSourceDailyRow(
            SqlServerFixture.TenantA, date, "placement", v1, 20, 9, 100), CancellationToken.None);
        await repo.UpsertFlaggedSourceAsync(new FlaggedSourceDailyRow(
            SqlServerFixture.TenantA, date, "placement", v2, 30, 9, 100), CancellationToken.None);
        await repo.UpsertFlaggedSourceAsync(new FlaggedSourceDailyRow(
            SqlServerFixture.TenantA, date, "placement", v3, 1, 50, 100), CancellationToken.None);

        var ordered = await repo.GetTopFlaggedSourcesAsync(date, date, 1000, CancellationToken.None);
        Assert.Equal(new[] { v3, v2, v1 }, ordered.Select(r => r.Value).ToArray());

        var limited = await repo.GetTopFlaggedSourcesAsync(date, date, 2, CancellationToken.None);
        Assert.Equal(new[] { v3, v2 }, limited.Select(r => r.Value).ToArray()); // at most limit rows
    }
}
