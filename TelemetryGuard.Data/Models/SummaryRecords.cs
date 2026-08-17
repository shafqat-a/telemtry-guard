using TelemetryGuard.Analytics.Abstractions;

namespace TelemetryGuard.Data.Models;

public sealed record VerdictDailySummaryRow(
    Guid TenantId, Guid CampaignId, DateOnly Date,
    int Allowed, int Challenged, int Blocked, long ScoreSum, int Events)
{
    public ScoreDistribution ScoreDistribution { get; init; } = ScoreDistribution.Empty;
}

public sealed record FlaggedSourceDailyRow(
    Guid TenantId, DateOnly Date, string SourceType, string Value,
    int FlaggedCount, int BlockedCount, long ScoreSum)
{
    public ScoreDistribution ScoreDistribution { get; init; } = ScoreDistribution.Empty;
}

/// <summary>One dbo.PublisherDailySummaries row. Every numeric field is summable
/// across rows (absolute values, never averages) so rollup replays converge.</summary>
public sealed record PublisherDailySummaryRow(
    Guid TenantId, DateOnly Date, string Placement,
    int Events, int Allowed, int Challenged, int Blocked, long ScoreSum, int NoJsBeaconCount)
{
    public ScoreDistribution ScoreDistribution { get; init; } = ScoreDistribution.Empty;
}

/// <summary>One dbo.SiteDailySummaries row. TotalEvents = all kinds; Events = verdicts.</summary>
public sealed record SiteDailySummaryRow(
    Guid TenantId, DateOnly Date, string SiteKey,
    int TotalEvents, int Events, int Allowed, int Challenged, int Blocked,
    long ScoreSum, int NoJsBeaconCount)
{
    public ScoreDistribution ScoreDistribution { get; init; } = ScoreDistribution.Empty;
}

/// <summary>Read model for GET /admin/reports/publishers: one placement's totals
/// summed over an inclusive date range. FirstDay/LastDay bound the observed
/// activity so a reader can tell a 1-day spike from steady traffic.</summary>
public sealed record PlacementRangeTotalsRow(
    string Placement, int Events, int Allowed, int Challenged, int Blocked,
    long ScoreSum, int NoJsBeaconCount, DateOnly FirstDay, DateOnly LastDay)
{
    public ScoreDistribution ScoreDistribution { get; init; } = ScoreDistribution.Empty;
}
