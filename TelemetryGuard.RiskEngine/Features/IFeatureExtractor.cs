using TelemetryGuard.RiskEngine.Contracts;

namespace TelemetryGuard.RiskEngine.Features;

public interface IFeatureExtractor
{
    /// <summary>Pure function: all I/O pre-fetched into RawSessionData. Deterministic,
    /// thread-safe, synchronous (50 ms budget, D3).</summary>
    FraudFeatureVector Extract(RawSessionData raw);
}
