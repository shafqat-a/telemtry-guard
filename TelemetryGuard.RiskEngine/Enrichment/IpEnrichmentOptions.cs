namespace TelemetryGuard.RiskEngine.Enrichment;

/// <summary>Bound from the "IpEnrichment" configuration section (see AddIpEnrichment).
/// All database files are optional at runtime — missing files degrade to null
/// enrichment fields, never a startup failure.</summary>
public sealed class IpEnrichmentOptions
{
    public const string SectionName = "IpEnrichment";

    /// <summary>Which IP-intelligence dataset answers lookups (D24), case-insensitive:
    /// <list type="bullet">
    /// <item><c>Iplegence</c> (default) — one merged <see cref="IplegenceDb"/> file.</item>
    /// <item><c>MaxMind</c> — GeoLite2 City + ASN + IP2Proxy LITE PX, the RSK-02 original.</item>
    /// </list>
    /// Any other value aborts startup rather than silently enriching nothing — the same
    /// rule the analytics provider switch follows (D7).</summary>
    public string Provider { get; set; } = IpIntelligenceProviders.Iplegence;

    public string DataDir { get; set; } = "./data/geo";

    /// <summary>Iplegence provider: the merged MaxMind-compatible database, refreshed by
    /// scripts/update-iplegence.sh.</summary>
    public string IplegenceDb { get; set; } = "Superior-IP.mmdb";

    // MaxMind provider files, refreshed by scripts/update-geoip.sh.
    public string CityDb { get; set; } = "GeoLite2-City.mmdb";
    public string AsnDb { get; set; } = "GeoLite2-ASN.mmdb";
    public string ProxyDb { get; set; } = "IP2PROXY-LITE-PX11.BIN";

    /// <summary>Apple iCloud Private Relay egress ranges. Used by every provider: an
    /// optional refreshed copy, falling back to the seed embedded in the assembly.</summary>
    public string PrivateRelayCsv { get; set; } = "apple-private-relay.csv";

    public int RefreshCheckHours { get; set; } = 6;   // how often the refresh service polls file mtimes
}

/// <summary>Accepted <see cref="IpEnrichmentOptions.Provider"/> values.</summary>
public static class IpIntelligenceProviders
{
    public const string Iplegence = "Iplegence";
    public const string MaxMind = "MaxMind";
}
