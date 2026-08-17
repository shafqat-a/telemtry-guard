using TelemetryGuard.Data.Models;

namespace TelemetryGuard.Data.Repositories;

/// <summary>
/// Writes/reads the D23 aggregate tables dbo.VerdictDailySummaries and
/// dbo.FlaggedSourcesDaily. Upserts are idempotent ABSOLUTE-VALUE MERGEs — never
/// increments — so rollup re-runs (crash recovery, watermark replay) converge
/// instead of double-counting.
///
/// Background-job usage (ANA-07 rollup): the job runs per tenant — it enumerates
/// tenants via <see cref="ISystemConnectionFactory.OpenSystemAsync"/>, then for
/// each tenant either (a) creates a DI scope, sets the scoped TenantContext
/// (FND-04) to that tenant, and resolves this repository inside the scope
/// (preferred — reuses this code path unchanged), or (b) uses
/// <see cref="ISystemConnectionFactory.OpenForTenantAsync"/> directly for raw SQL.
/// This repository itself needs no changes for job use.
/// </summary>
public interface IVerdictSummaryRepository
{
    /// <summary>Idempotent absolute-value MERGE upsert (rollup re-runs must converge,
    /// never double-count). Row.TenantId must equal the ambient tenant.</summary>
    Task UpsertDailySummaryAsync(VerdictDailySummaryRow row, CancellationToken ct);

    /// <summary>Idempotent absolute-value MERGE upsert.</summary>
    Task UpsertFlaggedSourceAsync(FlaggedSourceDailyRow row, CancellationToken ct);

    /// <summary>Live-path per-verdict increment (API-06). The ANA-07 rollup's absolute
    /// upsert (<see cref="UpsertDailySummaryAsync"/>) later overwrites these rows from
    /// ClickHouse — the rollup stays authoritative; this increment only keeps the
    /// live/API-07-visible row roughly current between rollup runs, and must never be
    /// used by the rollup itself (increments there would double-count on replay).
    /// Sessions with no campaign (pixel-only/organic) use Guid.Empty as the
    /// CampaignId sentinel. Row.TenantId must equal the ambient tenant.</summary>
    Task IncrementDailySummaryAsync(VerdictDailySummaryRow delta, CancellationToken ct);

    /// <summary>Read for API-07/dashboards: inclusive date range, one campaign.</summary>
    Task<IReadOnlyList<VerdictDailySummaryRow>> GetDailySummariesAsync(
        Guid campaignId, DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>REQ-03: tenant-wide daily summary — every campaign plus
    /// campaign-less traffic, summed per date (the CampaignId dimension is
    /// collapsed away). One row per date that has any data; CampaignId on the
    /// returned rows is a Guid.Empty sentinel (unused by callers — API-07 sets
    /// the response's top-level campaignId to null for this path instead).
    /// Backs GET /admin/reports/summary when campaignId is omitted.</summary>
    Task<IReadOnlyList<VerdictDailySummaryRow>> GetTenantDailySummariesAsync(
        DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>Top flagged sources over an inclusive date range, ordered by
    /// BlockedCount desc then FlaggedCount desc.</summary>
    Task<IReadOnlyList<FlaggedSourceDailyRow>> GetTopFlaggedSourcesAsync(
        DateOnly from, DateOnly to, int limit, CancellationToken ct);
}

public interface IRollupWatermarkRepository
{
    /// <summary>Null when the named rollup has never run for this tenant.</summary>
    Task<DateTime?> GetAsync(string rollupName, CancellationToken ct);
    Task SetAsync(string rollupName, DateTime watermarkUtc, CancellationToken ct);
}
