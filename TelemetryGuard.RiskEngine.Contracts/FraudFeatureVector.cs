namespace TelemetryGuard.RiskEngine.Contracts;

/// <summary>
/// The complete fraud-signal vector for one session/click, feature_set_version = 1.
/// NORMATIVE NOTE: the companion document fraud-signal-feature-spec.md referenced by
/// doc/spec.md §7 is absent from this repository; THIS record is the normative
/// contract for feature names, types, and null semantics. Do not rename members
/// without bumping FeatureSetVersion.
///
/// Null/NaN semantics (spec §7, "missing ≠ zero"):
///  - float properties default to float.NaN = signal absent (e.g. no JS beacon).
///  - bool? properties: null = signal absent/indeterminate; never treat null as false.
///  - plain int/long/float-zero velocity counters are legitimately 0 when cold.
/// </summary>
public sealed record FraudFeatureVector
{
    /// <summary>Version of this feature contract. Stamped into every ScoreResult.</summary>
    public const int FeatureSetVersion = 1;

    // ================= T1 — near-deterministic when they fire (rule-eligible) =================

    /// <summary>SDK: an invisible honeypot form field was focused/filled. null = no beacon.</summary>
    public bool? HoneypotTouched { get; init; }

    /// <summary>SDK: click event observed before first paint/render completed. null = no beacon.</summary>
    public bool? ClickBeforeRender { get; init; }

    /// <summary>SDK: pointer event with isTrusted == false (synthetic dispatch). null = no beacon.</summary>
    public bool? PointerUntrusted { get; init; }

    /// <summary>SDK: navigator.webdriver was true. null = no beacon.</summary>
    public bool? WebdriverFlag { get; init; }

    /// <summary>SDK: Botd detected a headless/automated browser. null = no beacon.</summary>
    public bool? HeadlessBrowser { get; init; }

    /// <summary>SDK: beacon signature/integrity check result. false = tampered payload
    /// (rule fires on FALSE, not on null). null = no beacon received.</summary>
    public bool? BeaconIntegrityOk { get; init; }

    /// <summary>Enrichment: IP is a Tor exit node. Rule fires only when IsPaidClick.
    /// null = proxy DB unavailable.</summary>
    public bool? IpTor { get; init; }

    /// <summary>TLS (JA3/JA4 via Cloudflare header) contradicts the User-Agent
    /// ("UA says Chrome, handshake says Go binary"). null = no TLS fingerprint header
    /// (not fronted by Cloudflare yet — degrades gracefully per D13).</summary>
    public bool? TlsUaMismatch { get; init; }

    /// <summary>Enrichment: IP belongs to a hosting/datacenter ASN. Rule fires only when
    /// IsPaidClick. null = enrichment DBs unavailable.</summary>
    public bool? IpDatacenterAsn { get; init; }

    /// <summary>Velocity (Redis): clicks from this IP in the sliding last minute.
    /// Plain int — legitimately 0 when cold.</summary>
    public int IpClicksLastMin { get; init; }

    /// <summary>User-Agent OS contradicts Client Hints platform. null = Client Hints absent.</summary>
    public bool? UaOsMismatch { get; init; }

    /// <summary>Derived: straight-line(first,last) / sum(segment lengths) over mouse points.
    /// Near 1.0 = perfectly straight scripted movement. NaN when &lt; 5 points or no beacon.</summary>
    public float MousePathLinearity { get; init; } = float.NaN;

    /// <summary>Derived: population std-dev of inter-input-event gaps in ms.
    /// Near 0 = metronomic scripted input. NaN when &lt; 3 events or no beacon.</summary>
    public float StdInterEventMs { get; init; } = float.NaN;

    /// <summary>Paid click with missing OR replayed (Redis-deduped) gclid/fbclid.
    /// null = organic traffic (no click id expected).</summary>
    public bool? ClickIdInvalid { get; init; }

    // ================= T2 — strong model features =================

    /// <summary>A JS beacon arrived within the grace period. Itself a T2 feature
    /// (always known → plain bool). false ⇒ every SDK-derived member above/below is NaN/null.</summary>
    public bool HasJsBeacon { get; init; }

    /// <summary>UA/device heuristics indicate an emulator or VM. null = UA absent/no signal.</summary>
    public bool? EmulatorOrVm { get; init; }

    /// <summary>Reported screen/viewport geometry is implausible. null = no beacon.</summary>
    public bool? ScreenResAnomalous { get; init; }

    /// <summary>Velocity (Redis fpz counter): times this fingerprint presented zero-age
    /// first-party storage in the last 7 days (bot farm wiping state). Count 1 is an
    /// innocent first visit. float because NaN when no fingerprint (no beacon).</summary>
    public float StorageAgeZeroRepeat { get; init; } = float.NaN;

    /// <summary>Enrichment: proxy or VPN egress. FORCED to false when IsPrivateRelay
    /// (Apple Private Relay carve-out). null = proxy DB unavailable.</summary>
    public bool? IpProxyOrVpn { get; init; }

    /// <summary>IP country is outside the campaign's configured geo targets.
    /// null = no geo lookup, organic traffic, or campaign has no geo targets.</summary>
    public bool? IpGeoTargetMismatch { get; init; }

    /// <summary>SDK: seconds on page at beacon flush. NaN = no beacon.</summary>
    public float TimeOnPageSec { get; init; } = float.NaN;

    /// <summary>SDK: seconds from first form-field focus to submit. Condition on
    /// AutofillDetected before scoring low values. NaN = no form activity/no beacon.</summary>
    public float FormFillTimeSec { get; init; } = float.NaN;

    /// <summary>SDK: ms from navigation start to first input event. NaN = no beacon/no input.</summary>
    public float FirstInteractionDelayMs { get; init; } = float.NaN;

    /// <summary>Input events contradict claimed device class (e.g. mobile UA, mouse-only
    /// input). null = no beacon or too few events.</summary>
    public bool? InputModalityMismatch { get; init; }

    /// <summary>Derived: mean inter-input-event gap in ms. NaN when &lt; 3 events/no beacon.</summary>
    public float MeanInterEventMs { get; init; } = float.NaN;

    /// <summary>Velocity (Redis HLL): distinct sessions for this device fingerprint,
    /// last hour. 0 when cold or no fingerprint.</summary>
    public long DeviceSessionsLastHour { get; init; }

    /// <summary>Velocity (Redis HLL): distinct User-Agents seen from this IP, last hour.
    /// 0 when cold.</summary>
    public long IpDistinctUasLastHour { get; init; }

    /// <summary>Velocity (Redis HLL): distinct device fingerprints from this IP, last hour.
    /// Normalize by AsnType (carrier-grade NAT) before judging. 0 when cold.</summary>
    public long DeviceIdsThisIpHour { get; init; }

    // ================= T3 — weak/supporting; NEVER rule-eligible =================
    // (privacy tools and edge cases fire these on real humans — Brave, Firefox,
    //  password managers, corporate proxies)

    /// <summary>SDK: cookies disabled. null = no beacon.</summary>
    public bool? CookiesDisabled { get; init; }

    /// <summary>SDK: canvas fingerprint blocked/randomized (Brave/Firefox do this by
    /// design). null = no beacon.</summary>
    public bool? CanvasFpBlocked { get; init; }

    /// <summary>Browser-reported IANA timezone vs IP geolocation timezone offset differ
    /// materially. null = either side missing.</summary>
    public bool? TimezoneIpMismatch { get; init; }

    /// <summary>Accept-Language primary language implausible for IP country.
    /// null = either side missing.</summary>
    public bool? LanguageGeoMismatch { get; init; }

    /// <summary>Decaying 0..1 bad-reputation score for this IP. 0 = no negative history
    /// (legitimately 0 when cold — plain float, not NaN). No producer in Phase 1;
    /// always 0 until a reputation store exists.</summary>
    public float IpReputationBad { get; init; }

    /// <summary>SDK: paste events into identity fields (email/name/phone). Password
    /// managers do this legitimately. null = no beacon.</summary>
    public bool? PasteInIdentityFields { get; init; }

    /// <summary>HTTP: Referer header absent on the tracked request. Plain bool (always
    /// observable). Largely superseded by ClickIdInvalid but retained as weak T3.</summary>
    public bool ReferrerMissing { get; init; }

    /// <summary>SDK: an envelope's sent_at differed from the server receive time by more
    /// than the configured skew (API-04 skew_bad). A wrong device clock is ordinary, so
    /// this is its own weak T3 signal and deliberately NOT part of BeaconIntegrityOk —
    /// it must never reach the beacon_integrity_failed T1 floor. null = no beacon.</summary>
    public bool? ClockSkewBad { get; init; }

    /// <summary>SDK: total count of input timing events (mouse/key/touch). Used by rules
    /// as a minimum-evidence gate. NaN = no beacon.</summary>
    public float InputEventCount { get; init; } = float.NaN;

    /// <summary>SDK: count of scroll events. NaN = no beacon.</summary>
    public float ScrollEvents { get; init; } = float.NaN;

    /// <summary>SDK: pages viewed this session (SPA navigations included). NaN = no beacon.</summary>
    public float PagesViewed { get; init; } = float.NaN;

    // ================= CTX — context/conditioning only; never scored directly =================

    /// <summary>Device class is mobile (UA/Client Hints). null = UA absent.</summary>
    public bool? IsMobile { get; init; }

    /// <summary>ASN classification; Unknown when enrichment unavailable. One-hot for models.</summary>
    public AsnType AsnType { get; init; } = AsnType.Unknown;

    /// <summary>IP is in Apple iCloud Private Relay egress ranges. When true,
    /// IpProxyOrVpn and IpDatacenterAsn are forced false. Always known (embedded range
    /// list) → plain bool.</summary>
    public bool IsPrivateRelay { get; init; }

    /// <summary>SDK: number of inter-move gaps behind MeanInterEventMs/StdInterEventMs
    /// (API-04 mm_n). Evidence gate for the cadence/linearity rules — a std computed
    /// from a handful of gaps proves nothing. NaN = no beacon.</summary>
    public float MouseMoveGaps { get; init; } = float.NaN;

    /// <summary>SDK: a monitored form was submitted. null = no beacon.</summary>
    public bool? FormSubmitted { get; init; }

    /// <summary>SDK: browser autofill detected on the form. Conditions FormFillTimeSec.
    /// null = no beacon/no form.</summary>
    public bool? AutofillDetected { get; init; }

    /// <summary>This session originated from a paid ad click (tracker hit /c with a click
    /// id). Gates the ip_tor and ip_datacenter_asn T1 rules. Always known → plain bool.</summary>
    public bool IsPaidClick { get; init; }

    /// <summary>Turnstile outcome for re-scoring (API-05). Default NotChallenged.</summary>
    public ChallengeOutcome ChallengeOutcome { get; init; } = ChallengeOutcome.NotChallenged;
}
