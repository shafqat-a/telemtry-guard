namespace TelemetryGuard.Analytics.Abstractions;

/// <summary>
/// A traffic source ranked by flagged activity, as answered by
/// <see cref="IAnalyticsQueries.GetTopFlaggedSourcesAsync"/>. The field split
/// matches the DAT-06 <c>FlaggedSourceDailyRow</c> consumer
/// (FlaggedCount/BlockedCount/ScoreSum).
/// </summary>
public sealed record FlaggedSource(
    string SourceType,         // "ip" for MVP; DAT-06 also allows "placement"|"device_id"|"fingerprint" later
    string SourceValue,        // e.g. "203.0.113.7"
    long FlaggedEvents,        // verdicts in challenge OR block band
    long BlockedEvents,        // verdicts in block band only (score 71–100)
    long TotalEvents,
    long ScoreSum,             // sum of scores over scored events; 0 when none
    double AvgScore,           // NaN when no scored events
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc);
