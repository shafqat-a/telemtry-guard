namespace TelemetryGuard.Analytics.Abstractions;

/// <summary>Strict analytics-store projection used by the MarketIQ evidence APIs.</summary>
public sealed record VerdictEvidence(
    DateTime TimestampUtc,
    string SessionId,
    int Score,
    string Band,
    string Action,
    IReadOnlyList<string> RuleHits,
    string ScorerVersion,
    int FeatureSetVersion,
    int? ShadowScore,
    string? ShadowScorerVersion,
    string FeaturesJson);

public sealed record VerdictCursor(DateTime TimestampUtc, string SessionId);

public sealed record VerdictEvidencePage(IReadOnlyList<VerdictEvidence> Items, bool HasMore);
