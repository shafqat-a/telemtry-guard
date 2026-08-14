namespace TelemetryGuard.RiskEngine.Scoring;

/// <summary>Which scorer implementation ENFORCES (drives band/challenge/exclusion sync).
/// Independent of whether a model is loaded at all — see <see cref="ScoringOptions"/>.</summary>
public enum ScoringMode
{
    /// <summary>The <see cref="ScoringOptions.Scorer"/> selection enforces directly.</summary>
    Enforce,

    /// <summary>D18 listen-only: <see cref="HeuristicScorer"/> ALWAYS enforces regardless
    /// of <see cref="ScoringOptions.Scorer"/>; if a model is configured
    /// (<see cref="ScoringOptions.ModelPath"/> set) it runs only as a non-enforcing
    /// <see cref="IShadowScorer"/> — logged to shadow_score/shadow_scorer_version,
    /// never consulted for the verdict.</summary>
    ListenOnly,
}

/// <summary>
/// RSK-08 (D18): the heuristic-to-model swap is a config change only. Bound from the
/// "Scoring" config section (alongside the pre-existing "Scoring:Bands" ->
/// ScoringBandOptions and "Scoring:Heuristic" -> HeuristicWeights sections, which this
/// type does NOT relocate or duplicate).
///
/// Two config snippets cover the whole rollout:
/// <code>
/// // Listen-only pilot: heuristic still enforces; the model is loaded and scored
/// // for every non-whitelisted session, but only logged (shadow_score/shadow_scorer_version).
/// "Scoring": { "Scorer": "Heuristic", "Mode": "ListenOnly", "ModelPath": "artifacts/models/lgbm-20260915-a1b2c3d4" }
///
/// // Promotion (human decision, never automatic): the model now enforces directly.
/// "Scoring": { "Scorer": "MlNet", "Mode": "Enforce", "ModelPath": "artifacts/models/lgbm-20260915-a1b2c3d4" }
/// </code>
/// T1 rule floors keep applying via Math.Max in BOTH modes (§6.3: rules only raise).
/// </summary>
public sealed class ScoringOptions
{
    public const string SectionName = "Scoring";

    /// <summary>"Heuristic" (default) | "MlNet". Which scorer implementation ENFORCES
    /// when <see cref="Mode"/> == Enforce. Ignored for the enforcing choice under
    /// ListenOnly (heuristic always enforces there) — but a non-empty
    /// <see cref="ModelPath"/> still loads and shadow-scores regardless of this value.</summary>
    public string Scorer { get; set; } = "Heuristic";

    public ScoringMode Mode { get; set; } = ScoringMode.Enforce;

    /// <summary>Directory containing model.zip + metadata.json (Trainer's export shape,
    /// RSK-08 step 4). Null/empty = no model loaded at all (pure heuristic, MVP
    /// default). Required when Scorer == "MlNet" or Mode == ListenOnly with a model
    /// configured.</summary>
    public string? ModelPath { get; set; }
}
