using MaxMind.Db;

namespace TelemetryGuard.RiskEngine.Enrichment.Providers;

// Decode DTOs for the iplegence "Superior-IP.mmdb" record layout (database_type
// "iplegence-Superior-IP"). Mirrors iplegence's internal/schema/record.go ToMMDBType:
// nested country / continent / city / location / subdivisions / postal / asn / traits,
// English names only, with empty fields and false traits OMITTED from the record.
//
// Consequences of that omission, which the mapping in IplegenceIpIntelligenceProvider
// depends on:
//   * a whole sub-map is absent → the property here is null (nothing known),
//   * a trait key is absent → false (the row exists and no source asserted the flag).
//
// MaxMind.Db populates these through the [Constructor]/[Parameter] attributes: every
// parameter needs a default so absent keys decode rather than throw, and keys present in
// the database but missing here (continent, subdivisions, postal, geoname_id,
// accuracy_radius) are skipped silently — verified against a fixture and a real build.

internal sealed class IplegenceRecord
{
    [Constructor]
    public IplegenceRecord(
        [Parameter("country")] IplegenceCountry? country = null,
        [Parameter("city")] IplegenceCity? city = null,
        [Parameter("location")] IplegenceLocation? location = null,
        [Parameter("asn")] IplegenceAsn? asn = null,
        [Parameter("traits")] IplegenceTraits? traits = null)
    {
        Country = country;
        City = city;
        Location = location;
        Asn = asn;
        Traits = traits;
    }

    public IplegenceCountry? Country { get; }
    public IplegenceCity? City { get; }
    public IplegenceLocation? Location { get; }
    public IplegenceAsn? Asn { get; }
    public IplegenceTraits? Traits { get; }
}

internal sealed class IplegenceCountry
{
    [Constructor]
    public IplegenceCountry([Parameter("iso_code")] string? isoCode = null)
    {
        IsoCode = isoCode;
    }

    public string? IsoCode { get; }
}

internal sealed class IplegenceCity
{
    [Constructor]
    public IplegenceCity([Parameter("names")] IDictionary<string, string>? names = null)
    {
        Names = names;
    }

    /// <summary>iplegence writes English names only (locked decision "Names: names.en").</summary>
    public IDictionary<string, string>? Names { get; }

    public string? EnglishName =>
        Names is not null && Names.TryGetValue("en", out var name) && name.Length > 0 ? name : null;
}

internal sealed class IplegenceLocation
{
    [Constructor]
    public IplegenceLocation(
        [Parameter("latitude")] double? latitude = null,
        [Parameter("longitude")] double? longitude = null,
        [Parameter("time_zone")] string? timeZone = null)
    {
        Latitude = latitude;
        Longitude = longitude;
        TimeZone = timeZone;
    }

    public double? Latitude { get; }
    public double? Longitude { get; }
    public string? TimeZone { get; }
}

internal sealed class IplegenceAsn
{
    [Constructor]
    public IplegenceAsn(
        [Parameter("autonomous_system_number")] long? number = null,
        [Parameter("autonomous_system_organization")] string? organization = null,
        [Parameter("as_domain")] string? domain = null)
    {
        Number = number;
        Organization = organization;
        Domain = domain;
    }

    public long? Number { get; }
    public string? Organization { get; }

    /// <summary>Decoded but unused today — <see cref="IpEnrichment"/> has no field for it.
    /// Kept because it is part of the record contract and costs one string reference.</summary>
    public string? Domain { get; }
}

internal sealed class IplegenceTraits
{
    /// <summary>All-false instance used when a found row carries no traits map at all.</summary>
    public static IplegenceTraits None { get; } = new();

    [Constructor]
    public IplegenceTraits(
        [Parameter("is_anonymous")] bool isAnonymous = false,
        [Parameter("is_anonymous_vpn")] bool isAnonymousVpn = false,
        [Parameter("is_hosting_provider")] bool isHostingProvider = false,
        [Parameter("is_public_proxy")] bool isPublicProxy = false,
        [Parameter("is_tor_exit_node")] bool isTorExitNode = false,
        [Parameter("is_cdn")] bool isCdn = false,
        [Parameter("is_relay")] bool isRelay = false,
        [Parameter("usage_type")] string? usageType = null,
        [Parameter("usage_type_source")] string? usageTypeSource = null)
    {
        IsAnonymous = isAnonymous;
        IsAnonymousVpn = isAnonymousVpn;
        IsHostingProvider = isHostingProvider;
        IsPublicProxy = isPublicProxy;
        IsTorExitNode = isTorExitNode;
        IsCdn = isCdn;
        IsRelay = isRelay;
        UsageType = usageType;
        UsageTypeSource = usageTypeSource;
    }

    public bool IsAnonymous { get; }
    public bool IsAnonymousVpn { get; }
    public bool IsHostingProvider { get; }
    public bool IsPublicProxy { get; }
    public bool IsTorExitNode { get; }
    public bool IsCdn { get; }
    public bool IsRelay { get; }

    /// <summary>Coarse inferred type: residential / mobile / business / education /
    /// government / hosting, or null/empty when nothing could be inferred. INFERRED from
    /// PeeringDB network types, ASN-name keywords and prefix flags — self-declared and
    /// incomplete, not a commercial usage_type feed. Absent for roughly half the routable
    /// IPv4 space, which is why <see cref="AsnType.Unknown"/> stays a normal outcome.</summary>
    public string? UsageType { get; }

    /// <summary>Which rule produced <see cref="UsageType"/>: "peeringdb" (self-declared
    /// network type), "asn_name" (keyword match on the ASN organization/domain) or
    /// "prefix_flag" (derived from is_hosting_provider/is_cdn). Decoded for diagnostics
    /// and for a future confidence weighting; nothing scores on it today.</summary>
    public string? UsageTypeSource { get; }
}
