using TelemetryGuard.RiskEngine.Contracts;

namespace TelemetryGuard.RiskEngine.Scoring;

/// <summary>
/// RSK-08 (D18 listen-only): marker interface for an OPTIONAL, secondary scorer run
/// purely for shadow logging. Deliberately separate from <see cref="IScorer"/> (same
/// shape) so DI can hold both an enforcing <see cref="IScorer"/> registration and a
/// non-enforcing shadow registration simultaneously without a keyed-service collision.
/// The scoring pipeline (RSK-07/RSK-08) runs this AFTER the enforcing scorer, in a
/// try/catch that can never affect the verdict — a throw here is logged and the shadow
/// fields stay null. Never consulted for the band, T1 floor, or any enforcement
/// decision (D18: "listen-only means listen-only").
/// </summary>
public interface IShadowScorer
{
    ScoreResult Score(in FraudFeatureVector vector);
}

/// <summary>Adapts any <see cref="IScorer"/> (in practice, an <see cref="MlNetScorer"/>)
/// to run as a non-enforcing shadow scorer without changing its own registration as
/// the (possibly still-heuristic) enforcing <see cref="IScorer"/>.</summary>
public sealed class ShadowScorerAdapter(IScorer inner) : IShadowScorer
{
    public ScoreResult Score(in FraudFeatureVector vector) => inner.Score(in vector);
}
