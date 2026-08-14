namespace TelemetryGuard.Analytics.Abstractions;

/// <summary>
/// One (day, publisher placement) bucket of VERDICT rows, as answered by
/// <see cref="IAnalyticsQueries.GetTopPlacementsDailyAsync"/>.
///
/// PLACEMENT IDENTITY (P2-01): tg_events has no placement column. A verdict is
/// attributed to the normalized publisher host of its OWN session's earliest
/// capture row (kind 'tracker' or 'pixel' — the only kinds that carry a
/// referrer). Sessions with no capture row, or no referrer on it, produce NO
/// bucket at all (§7: missing != zero — never a '' bucket, never a 0 row).
/// </summary>
public sealed record PlacementDailyCounts(
    DateOnly Day,
    string Placement,          // lowercase host, leading "www." stripped, <= 253 chars
    long ScoredEvents,         // verdict rows attributed to this placement on this day
    long Allowed,
    long Challenged,
    long Blocked,
    long ScoreSum,             // sum of scores; 0 when none (mergeable — never an average)
    double AvgScore,           // NaN when ScoredEvents == 0 (missing != zero)
    long NoJsBeaconCount);     // verdicts with has_js_beacon = 0
