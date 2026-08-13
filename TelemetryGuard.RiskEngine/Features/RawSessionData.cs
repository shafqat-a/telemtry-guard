using TelemetryGuard.RiskEngine.Contracts;
using TelemetryGuard.RiskEngine.Enrichment;
using TelemetryGuard.RiskEngine.Velocity;

namespace TelemetryGuard.RiskEngine.Features;

/// <summary>Everything the extractor needs, pre-fetched by the scoring pipeline (RSK-07).
/// Extraction is pure: no I/O may hide behind these members.</summary>
public sealed record RawSessionData
{
    public required string SessionId { get; init; }
    public required string Ip { get; init; }
    public string? UserAgent { get; init; }

    /// <summary>Selected request headers, case-insensitive keys. Relevant:
    /// "Accept-Language", "Referer", "Sec-CH-UA-Platform", "Sec-CH-UA-Mobile".</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>JA3/JA4 fingerprint from the Cloudflare header; null when not fronted (D13).</summary>
    public string? TlsFingerprint { get; init; }

    public bool IsPaidClick { get; init; }
    public string? ClickId { get; init; }          // gclid/fbclid as received

    /// <summary>From IVelocityStore.RecordClickAsync at capture time:
    /// true = fresh, false = replayed, null = no click id was present.</summary>
    public bool? ClickIdFresh { get; init; }

    /// <summary>null = no beacon arrived within the grace period (non-JS bot or pixel mode).</summary>
    public BeaconData? Beacon { get; init; }

    public required IpEnrichment Enrichment { get; init; }
    public required VelocitySnapshot Velocity { get; init; }
    public CampaignContext? Campaign { get; init; }
    public ChallengeOutcome ChallengeOutcome { get; init; } = ChallengeOutcome.NotChallenged;
}

/// <summary>Campaign config subset needed for extraction. The pipeline maps this from the
/// campaign entity exposed by the DAT-05 config repositories.</summary>
public sealed record CampaignContext
{
    /// <summary>ISO 3166-1 alpha-2 codes the campaign targets; empty = no geo targeting.</summary>
    public IReadOnlyList<string> GeoTargets { get; init; } = Array.Empty<string>();
}
