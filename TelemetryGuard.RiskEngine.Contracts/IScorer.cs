namespace TelemetryGuard.RiskEngine.Contracts;

/// <summary>Result of scoring one session. RuleHits are the T1 rule names that fired
/// (attached by the scoring pipeline, RSK-07 — IScorer implementations themselves
/// return an empty array). FeatureSetVersion echoes FraudFeatureVector.FeatureSetVersion.</summary>
public sealed record ScoreResult(
    int Score,                        // 0..100 inclusive
    IReadOnlyList<string> RuleHits,   // e.g. ["honeypot_touched"]; empty when none
    string ScorerVersion,             // e.g. "heuristic-1", "lgbm-20260901-a1b2c3d4"
    int FeatureSetVersion);           // always FraudFeatureVector.FeatureSetVersion today

/// <summary>The pluggable scorer seam (D18): heuristic at MVP, ML.NET LightGBM later,
/// swapped by config only. Implementations MUST be pure, thread-safe, allocation-light,
/// and synchronous — they run in-process inside the &lt; 50 ms scoring budget (D3).
/// Implementations return their model/heuristic score with EMPTY RuleHits; the pipeline
/// (RSK-07) applies the T1 rule floor via Max(score, floor) and attaches hits.</summary>
public interface IScorer
{
    /// <summary>Stable identifier stamped on every verdict (D18).</summary>
    string ScorerVersion { get; }

    ScoreResult Score(in FraudFeatureVector vector);
}
