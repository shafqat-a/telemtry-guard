using Microsoft.Extensions.ML;
using Microsoft.ML.Data;
using TelemetryGuard.RiskEngine.Contracts;

namespace TelemetryGuard.RiskEngine.Scoring;

/// <summary>ML.NET's raw binary-classification output for one <see cref="MlFeatureRow"/>.
/// PredictedLabel/Score are the trainer's calibrated defaults; only Probability (the
/// calibrated 0..1 fraud probability) feeds <see cref="MlNetScorer"/>.</summary>
public sealed class MlPrediction
{
    [ColumnName("PredictedLabel")]
    public bool PredictedLabel;

    public float Probability;

    public float Score;
}

/// <summary>
/// RSK-08 (D18): the trained LightGBM scorer behind the <see cref="IScorer"/> seam.
/// Serving-only inference via a pooled, thread-safe <see cref="PredictionEnginePool{TData,TPrediction}"/>
/// — kept in-process, sub-millisecond, well inside the &lt; 50 ms scoring budget (D3);
/// no model-server sidecar, no HTTP inference hop. <see cref="ScorerVersion"/> is
/// stamped from metadata.json at DI registration time (never hard-coded) so
/// heuristic-era ("heuristic-1") and model-era ("lgbm-yyyyMMdd-hash8") rows are never
/// confused (D18 version stamping).
///
/// Pure from the caller's perspective, thread-safe (the pool serializes access to
/// pooled PredictionEngine instances internally), synchronous. Always returns EMPTY
/// RuleHits — the pipeline (RSK-07) attaches T1 hits and applies the floor exactly
/// once, identically whether this scorer enforces or only shadows (§6.3).
/// </summary>
public sealed class MlNetScorer(PredictionEnginePool<MlFeatureRow, MlPrediction> pool, string scorerVersion)
    : IScorer
{
    public string ScorerVersion => scorerVersion;

    public ScoreResult Score(in FraudFeatureVector v)
    {
        var row = MlFeatureMapper.ToRow(v);
        var prediction = pool.Predict(row);
        var score = (int)Math.Clamp(Math.Round(prediction.Probability * 100), 0, 100);
        return new ScoreResult(score, Array.Empty<string>(), scorerVersion, FraudFeatureVector.FeatureSetVersion);
    }
}
