namespace TelemetryGuard.Analytics.Abstractions;

/// <summary>
/// Per-IP velocity aggregate over a trailing window, as answered by
/// <see cref="IAnalyticsQueries.GetIpVelocityAsync"/>.
/// </summary>
public sealed record IpVelocityStats(
    string Ip,
    TimeSpan Window,
    DateTime WindowEndUtc,
    long ClickCount,            // all tracker/pixel/beacon events from this IP in window
    long DistinctSessions,
    long DistinctUserAgents,
    long DistinctFingerprints,
    long FlaggedCount);         // events with Score >= 71 in window
