using System.Diagnostics.Metrics;

namespace TelemetryGuard.RiskEngine.Pipeline;

/// <summary>
/// OpenTelemetry-compatible metrics for the risk engine. The meter name is
/// "TelemetryGuard.RiskEngine" — API-01's OpenTelemetry setup subscribes to this meter
/// BY NAME (AddMeter(RiskMetrics.MeterName)); keep it stable.
/// tg.scoring.duration_ms is recorded exactly once per ScoreSessionAsync call with tags
/// band (allow|challenge|block|not_found) and whitelisted (bool).
/// </summary>
public static class RiskMetrics
{
    public const string MeterName = "TelemetryGuard.RiskEngine";

    public static readonly Meter Meter = new(MeterName);

    public static readonly Histogram<double> ScoringDuration =
        Meter.CreateHistogram<double>(
            "tg.scoring.duration_ms",
            unit: "ms",
            description: "End-to-end ScoreSessionAsync duration (D3 budget: p99 < 50 ms).");

    /// <summary>Single recording point so the band/whitelisted tag names stay consistent.</summary>
    public static void RecordScoringDuration(double durationMs, string band, bool whitelisted)
        => ScoringDuration.Record(
            durationMs,
            new KeyValuePair<string, object?>("band", band),
            new KeyValuePair<string, object?>("whitelisted", whitelisted));
}
