namespace TelemetryGuard.Analytics.Abstractions;

/// <summary>
/// One day's counts inside a <see cref="CampaignFraudReport"/>.
/// </summary>
public sealed record CampaignDailyCounts(
    DateOnly Day,
    long TotalEvents,          // all kinds
    long ScoredEvents,         // Kind == verdict
    long Allowed,              // verdicts with band 'allow'
    long Challenged,
    long Blocked,
    long ScoreSum,             // sum of scores over scored events; 0 when none (mergeable — feeds DAT-06 ScoreSum)
    double AvgScore,           // NaN when ScoredEvents == 0 (missing != zero)
    long NoJsBeaconCount)      // verdicts with has_js_beacon = false
{
    public ScoreDistribution ScoreDistribution { get; init; } = ScoreDistribution.Empty;
}

/// <summary>
/// Per-campaign fraud aggregate over a date range, as answered by
/// <see cref="IAnalyticsQueries.GetCampaignReportAsync"/>.
/// </summary>
public sealed record CampaignFraudReport(
    string CampaignId,
    DateRange Range,
    long TotalEvents,
    long ScoredEvents,
    long Allowed,
    long Challenged,
    long Blocked,
    double AvgScore,           // NaN when ScoredEvents == 0
    long NoJsBeaconCount,
    IReadOnlyList<CampaignDailyCounts> Days)   // ordered ascending by Day
{
    public ScoreDistribution ScoreDistribution { get; init; } = ScoreDistribution.Empty;
}
