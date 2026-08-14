using TelemetryGuard.Data.Models;

namespace TelemetryGuard.Data.Repositories;

/// <summary>
/// Writes/reads the P2-01 aggregate tables dbo.PublisherDailySummaries and
/// dbo.SiteDailySummaries. Upserts are idempotent ABSOLUTE-VALUE MERGEs — never
/// increments — so ANA-07 rollup replays converge instead of double-counting
/// (same contract as DAT-06's IVerdictSummaryRepository). There is deliberately
/// no live-path increment counterpart: nothing on the request path writes here.
/// </summary>
public interface IPublisherSummaryRepository
{
    /// <summary>Idempotent absolute-value MERGE. Row.TenantId must equal the ambient tenant.</summary>
    Task UpsertPlacementDailyAsync(PublisherDailySummaryRow row, CancellationToken ct);

    /// <summary>Idempotent absolute-value MERGE. Row.TenantId must equal the ambient tenant.</summary>
    Task UpsertSiteDailyAsync(SiteDailySummaryRow row, CancellationToken ct);

    /// <summary>Top placements over an INCLUSIVE date range, totals summed per
    /// placement, ordered by Blocked desc then (Challenged + Blocked) desc.
    /// limit must be 1..1000.</summary>
    Task<IReadOnlyList<PlacementRangeTotalsRow>> GetTopPlacementsAsync(
        DateOnly from, DateOnly to, int limit, CancellationToken ct);

    /// <summary>Per-site daily rows over an INCLUSIVE date range, ordered by date then site key.</summary>
    Task<IReadOnlyList<SiteDailySummaryRow>> GetSiteDailyAsync(
        DateOnly from, DateOnly to, CancellationToken ct);
}
