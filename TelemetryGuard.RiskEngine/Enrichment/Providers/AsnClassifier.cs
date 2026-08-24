using System.Collections.Frozen;
using TelemetryGuard.RiskEngine.Contracts;

namespace TelemetryGuard.RiskEngine.Enrichment.Providers;

/// <summary>Shared <see cref="AsnType"/> classification for every provider. The embedded
/// datacenter-ASN seed is the common fallback; each dataset feeds its own native signal
/// into it (IP2Proxy usage_type for MaxMind, traits flags for iplegence).</summary>
internal static class AsnClassifier
{
    internal const string DatacenterAsnResourceName =
        "TelemetryGuard.RiskEngine.Enrichment.Data.datacenter-asns.txt";

    /// <summary>Shared consumer-egress / CDN edge ASNs (Cloudflare WARP, Private Relay
    /// partners, Google One VPN). Seeded as <see cref="AsnType.Cdn"/>, never Datacenter,
    /// so real visitors behind them do not hit the ip_datacenter_paid T1 rule.</summary>
    internal const string CdnAsnResourceName =
        "TelemetryGuard.RiskEngine.Enrichment.Data.cdn-asns.txt";

    /// <summary>Maps IP2Proxy usage_type to AsnType, falling back to the embedded CDN seed,
    /// then the datacenter ASN seed, then Unknown. Precedence per RSK-02 step 5.</summary>
    internal static AsnType FromUsageType(string? usageType, long? asnNumber, FrozenSet<long> datacenterAsns,
        FrozenSet<long>? cdnAsns = null)
    {
        if (usageType is not null)
        {
            switch (usageType)
            {
                case "ISP": return AsnType.Residential;
                case "MOB": return AsnType.Mobile;
                case "COM":
                case "ORG": return AsnType.Business;
                case "DCH": return AsnType.Datacenter;
                case "EDU":
                case "LIB": return AsnType.Education;
                case "GOV":
                case "MIL": return AsnType.Government;
                case "CDN": return AsnType.Cdn;
                // anything else ("SES", "RSV", "-", "") falls through to the seed list
            }
        }

        return FromSeed(asnNumber, datacenterAsns, cdnAsns);
    }

    /// <summary>Maps iplegence traits to AsnType.
    ///
    /// <paramref name="usageType"/> is the dataset's inferred coarse type — one of
    /// residential / mobile / business / education / government / hosting, or empty when
    /// nothing could be inferred (~54% of the routable IPv4 space, which stays Unknown
    /// rather than being guessed: missing signal ≠ zero). It is trusted first because the
    /// producer already resolved precedence when deriving it — education/government from
    /// PeeringDB or the ASN name outrank prefix-level hosting flags, and it only reports
    /// "hosting" after those checks. The flags and the embedded seed then cover the rows
    /// it could not type.
    ///
    /// CDN outranks Datacenter for the same address because most CDN ASNs are also
    /// hosting ASNs and the edge classification is the useful one.</summary>
    internal static AsnType FromTraits(
        string? usageType, bool isCdn, bool isHostingProvider, long? asnNumber, FrozenSet<long> datacenterAsns,
        FrozenSet<long>? cdnAsns = null)
    {
        switch (usageType)
        {
            case "residential": return AsnType.Residential;
            case "mobile": return AsnType.Mobile;
            case "business": return AsnType.Business;
            case "education": return AsnType.Education;
            case "government": return AsnType.Government;
            case "hosting": return isCdn ? AsnType.Cdn : AsnType.Datacenter;
            // "" / null / an unrecognized value falls through to the flags and the seed
        }

        if (isCdn)
            return AsnType.Cdn;
        if (isHostingProvider)
            return AsnType.Datacenter;

        return FromSeed(asnNumber, datacenterAsns, cdnAsns);
    }

    /// <summary>The weakest signal we hold: CDN seed first (consumer egress), then the
    /// datacenter seed, else Unknown — never guessed.</summary>
    private static AsnType FromSeed(long? asnNumber, FrozenSet<long> datacenterAsns, FrozenSet<long>? cdnAsns)
    {
        if (asnNumber is not { } number) return AsnType.Unknown;
        if (cdnAsns is not null && cdnAsns.Contains(number)) return AsnType.Cdn;
        return datacenterAsns.Contains(number) ? AsnType.Datacenter : AsnType.Unknown;
    }

    /// <summary>Reads the embedded seed list of well-known datacenter/cloud ASNs.</summary>
    internal static FrozenSet<long> LoadDatacenterAsnSeed() => LoadSeed(DatacenterAsnResourceName);

    /// <summary>Reads the embedded seed list of shared consumer-egress / CDN ASNs.</summary>
    internal static FrozenSet<long> LoadCdnAsnSeed() => LoadSeed(CdnAsnResourceName);

    private static FrozenSet<long> LoadSeed(string resourceName)
    {
        using var stream = typeof(AsnClassifier).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' is missing from the assembly.");
        using var reader = new StreamReader(stream);

        var asns = new HashSet<long>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var hash = line.IndexOf('#');
            var span = (hash >= 0 ? line.AsSpan(0, hash) : line.AsSpan()).Trim();
            if (span.Length == 0)
                continue;
            if (long.TryParse(span, out var asn))
                asns.Add(asn);
        }
        return asns.ToFrozenSet();
    }
}
