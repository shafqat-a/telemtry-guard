namespace TelemetryGuard.Api.Edge;

/// <summary>INT-05: config gate for Cloudflare edge signal intake. Bound from the
/// "Edge" appsettings section (API-01's aggregation point).</summary>
public sealed class EdgeOptions
{
    public const string SectionName = "Edge";

    /// <summary>"None" (default) or "Cloudflare". Gates ALL X-TG-* header intake and
    /// the merging of embedded Cloudflare CIDRs into the trusted-proxy set. Local dev
    /// stays "None"; the deployed, Cloudflare-fronted environment sets "Cloudflare"
    /// (doc/runbooks/cloudflare-fronting.md step 7).</summary>
    public string Provider { get; set; } = "None";

    public bool IsCloudflare => string.Equals(Provider, "Cloudflare", StringComparison.OrdinalIgnoreCase);
}
