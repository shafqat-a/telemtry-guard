namespace TelemetryGuard.RiskEngine.Enrichment;

/// <summary>Bound from the "IpEnrichment" configuration section (see AddIpEnrichment).
/// All database files are optional at runtime — missing files degrade to null
/// enrichment fields, never a startup failure.</summary>
public sealed class IpEnrichmentOptions
{
    public string DataDir { get; set; } = "./data/geo";
    public string CityDb { get; set; } = "GeoLite2-City.mmdb";
    public string AsnDb { get; set; } = "GeoLite2-ASN.mmdb";
    public string ProxyDb { get; set; } = "IP2PROXY-LITE-PX11.BIN";
    public string PrivateRelayCsv { get; set; } = "apple-private-relay.csv"; // optional refreshed copy
    public int RefreshCheckHours { get; set; } = 6;   // how often the refresh service polls file mtimes
}
