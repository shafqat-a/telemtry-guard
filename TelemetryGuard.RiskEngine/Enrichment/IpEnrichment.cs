using TelemetryGuard.RiskEngine.Contracts;

namespace TelemetryGuard.RiskEngine.Enrichment;

/// <summary>Everything we know about an IP. Null members = the backing database was
/// unavailable or had no row — downstream this becomes NaN/null features, never 0/false.</summary>
public sealed record IpEnrichment
{
    public string? CountryCode { get; init; }      // ISO 3166-1 alpha-2, e.g. "DE"
    public string? City { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? TimeZone { get; init; }         // IANA, e.g. "Europe/Berlin"
    public long? AsnNumber { get; init; }
    public string? AsnOrganization { get; init; }
    public AsnType AsnType { get; init; } = AsnType.Unknown;
    public bool? IsProxyOrVpn { get; init; }       // null = PX db missing; RAW value — Private Relay carve-out applied in RSK-04
    public bool? IsTor { get; init; }              // null = PX db missing
    public bool? IsDatacenter { get; init; }       // null = both PX db and ASN unavailable
    public bool IsPrivateRelay { get; init; }      // embedded egress list — always known

    public static IpEnrichment Empty { get; } = new();
}

public interface IIpEnrichmentService
{
    /// <summary>Synchronous by design: memory-mapped in-process lookups (D3).
    /// Never throws for unparseable IPs — returns IpEnrichment.Empty.</summary>
    IpEnrichment Enrich(string ip);
}
