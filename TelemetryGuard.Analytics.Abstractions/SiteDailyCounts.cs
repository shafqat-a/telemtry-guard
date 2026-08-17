namespace TelemetryGuard.Analytics.Abstractions;

/// <summary>
/// One (day, site_key) bucket, as answered by
/// <see cref="IAnalyticsQueries.GetSiteDailyCountsAsync"/>. This is the
/// NON-CAMPAIGN view that dbo.VerdictDailySummaries (keyed on CampaignId)
/// cannot express: pixel-mode and organic sessions carry a site key but no
/// campaign. TotalEvents counts EVERY kind, so it is also the honest
/// denominator for "how much of this site's traffic has no resolvable
/// placement" (site TotalEvents minus the placement rows' ScoredEvents).
/// </summary>
public sealed record SiteDailyCounts(
    DateOnly Day,
    string SiteKey,
    long TotalEvents,          // all kinds: tracker + pixel + beacon + verdict
    long ScoredEvents,         // kind = 'verdict'
    long Allowed,
    long Challenged,
    long Blocked,
    long ScoreSum,             // 0 when none (mergeable)
    double AvgScore,           // NaN when ScoredEvents == 0
    long NoJsBeaconCount)
{
    public ScoreDistribution ScoreDistribution { get; init; } = ScoreDistribution.Empty;
}
