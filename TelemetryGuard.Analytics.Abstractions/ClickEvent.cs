using TelemetryGuard.Core.Tenancy;

namespace TelemetryGuard.Analytics.Abstractions;

/// <summary>
/// RAW capture + verdict storage record. NOT the model input —
/// FraudFeatureVector (RSK-01) is derived from this; never merge the two.
/// Null semantics (spec §7): SDK float = NaN when absent; SDK bool? = null when
/// absent; velocity ints are 0 when legitimately cold (never NaN).
/// </summary>
public sealed record ClickEvent
{
    // ---- identity ----
    public required TenantId TenantId { get; init; }
    public required string SiteKey { get; init; }
    public required string SessionId { get; init; }
    public required EventKind Kind { get; init; }

    // ---- click ids (one field per supported ad platform — API-02 extracts all four) ----
    public string CampaignId { get; init; } = "";
    public string Gclid { get; init; } = "";
    public string Fbclid { get; init; } = "";
    public string Msclkid { get; init; } = "";         // Microsoft Ads
    public string Ttclid { get; init; } = "";          // TikTok Ads
    public bool? ClickIdInvalid { get; init; }        // null = not applicable (organic)

    // ---- HTTP layer ----
    public required string Ip { get; init; }           // textual IPv4 or IPv6
    public IReadOnlyList<string> HeaderNames { get; init; } = Array.Empty<string>(); // ordered as received
    public string? UserAgent { get; init; }
    public string? SecChUa { get; init; }
    public string? SecChUaMobile { get; init; }
    public string? SecChUaPlatform { get; init; }
    public string? AcceptLanguage { get; init; }
    public string? Referrer { get; init; }
    public string? TlsJa3 { get; init; }               // null unless Cloudflare-fronted (D13)
    public string? TlsJa4 { get; init; }
    public uint? CfAsn { get; init; }                  // ASN as reported by Cloudflare header

    // ---- enrichment snapshot (null = lookup unavailable/failed) ----
    public string? Country { get; init; }              // ISO 3166-1 alpha-2
    public uint? Asn { get; init; }
    public string? AsnOrg { get; init; }
    public string? AsnType { get; init; }              // e.g. "hosting"|"isp"|"business"|"education"|"unknown"
    public bool? IsDatacenter { get; init; }
    public bool? IsProxy { get; init; }
    public bool? IsVpn { get; init; }
    public bool? IsTor { get; init; }
    public bool? IsPrivateRelay { get; init; }

    // ---- SDK summary (absent => NaN / null; pixel mode & non-JS bots hit this path) ----
    public required bool HasJsBeacon { get; init; }
    public bool? BeaconIntegrityOk { get; init; }
    public string? FingerprintVisitorId { get; init; }
    public float StorageAgeSec { get; init; } = float.NaN;
    public bool? WebdriverFlag { get; init; }
    public bool? HeadlessBrowser { get; init; }
    public float ScreenWidth { get; init; } = float.NaN;
    public float ScreenHeight { get; init; } = float.NaN;
    public string? Timezone { get; init; }             // IANA name, e.g. "Europe/Stockholm"
    public string? Language { get; init; }             // navigator.language
    public float MouseEventCount { get; init; } = float.NaN;
    public float KeyEventCount { get; init; } = float.NaN;
    public float TouchEventCount { get; init; } = float.NaN;
    public float ScrollEventCount { get; init; } = float.NaN;
    public float MeanInterEventMs { get; init; } = float.NaN;
    public float StdInterEventMs { get; init; } = float.NaN;
    public float MousePathLinearity { get; init; } = float.NaN;   // ~1.0 = scripted straight line
    public float FirstInteractionDelayMs { get; init; } = float.NaN;
    public float FormFillTimeSec { get; init; } = float.NaN;
    public bool? AutofillDetected { get; init; }
    public bool? PasteInIdentityFields { get; init; }
    public bool? HoneypotTouched { get; init; }
    public bool? PointerUntrusted { get; init; }
    public bool? InputModalityMismatch { get; init; }
    public float TimeOnPageSec { get; init; } = float.NaN;
    public float PagesViewed { get; init; } = float.NaN;

    // ---- velocity snapshot (server-computed at scoring time; 0 = cold, never NaN) ----
    public int IpClicksLastMin { get; init; }
    public int IpDistinctUasLastHour { get; init; }
    public int DeviceSessionsLastHour { get; init; }
    public int DeviceIdsThisIpHour { get; init; }

    // ---- verdict block (null/empty until Kind == Verdict) ----
    public int? Score { get; init; }                   // 0–100
    public string? Band { get; init; }                 // VerdictBands constants
    public string? Action { get; init; }               // enforced outcome; may differ from Band (whitelist, ApprovalQueue)
    public IReadOnlyList<string> RuleHits { get; init; } = Array.Empty<string>(); // T1 rule names that fired
    public string? ScorerVersion { get; init; }        // e.g. "heuristic-1" (D18)
    public int? FeatureSetVersion { get; init; }       // 1 for the MVP contract

    // ---- RSK-08 training/listen-only block (null/"" until populated at verdict time) ----
    /// <summary>JSON-serialized FraudFeatureVector captured at scoring time
    /// (TelemetryGuard.RiskEngine.Contracts.FraudFeatureVectorJson options) — the
    /// offline trainer (TelemetryGuard.Training) deserializes this back for model
    /// input. "" (not null) when not yet populated or the session was whitelisted
    /// (extraction never runs on that short-circuit).</summary>
    public string Features { get; init; } = "";

    /// <summary>D18 listen-only: the shadow (non-enforcing) model's score, 0-100, when a
    /// shadow scorer ran successfully for this session. Null when no shadow scorer is
    /// registered, the session was whitelisted, or the shadow scorer threw.</summary>
    public int? ShadowScore { get; init; }

    /// <summary>D18 listen-only: the shadow scorer's version stamp (e.g.
    /// "lgbm-20260915-a1b2c3d4"), paired with <see cref="ShadowScore"/>.</summary>
    public string? ShadowScorerVersion { get; init; }

    // ---- storage control ----
    public required ushort RetentionDays { get; init; } // denormalized from tenant config at ingest (D20), 30–180
    public required DateTime TimestampUtc { get; init; } // must be DateTimeKind.Utc
}
