namespace TelemetryGuard.RiskEngine.Features;

/// <summary>Config gates for feature extraction, bound from the "FeatureExtraction"
/// section via IOptionsMonitor (tunable without redeploy).</summary>
public sealed class FeatureExtractionOptions
{
    /// <summary>OFF until Cloudflare fronting (INT-05) supplies JA3/JA4 headers.</summary>
    public bool TlsUaMismatchEnabled { get; set; } = false;
}
