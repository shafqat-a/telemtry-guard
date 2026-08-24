using System.Text.Json;
using Microsoft.Extensions.Options;
using TelemetryGuard.RiskEngine.Contracts;
using TelemetryGuard.RiskEngine.Enrichment;

namespace TelemetryGuard.RiskEngine.Features;

/// <summary>Pure, synchronous feature extraction (spec §7, D3, D4). Every piece of I/O
/// (enrichment, velocity, session aggregates, campaign config) is pre-fetched by the
/// scoring pipeline (RSK-07) into <see cref="RawSessionData"/>; this class performs no
/// I/O, no clock reads, no randomness. Register as singleton — thread-safe.</summary>
public sealed class FeatureExtractor : IFeatureExtractor
{
    private const string TlsFingerprintsResource =
        "TelemetryGuard.RiskEngine.Features.Data.tls-fingerprints.json";

    private const string CountryLanguagesResource =
        "TelemetryGuard.RiskEngine.Features.Data.country-languages.json";

    // Fingerprint families considered browsers; everything else in the map is a
    // non-browser client (go-http, python, curl, okhttp, java, ...).
    private static readonly HashSet<string> BrowserTlsFamilies =
        new(StringComparer.OrdinalIgnoreCase) { "chrome", "firefox", "safari", "edge", "opera" };

    private static readonly IReadOnlyDictionary<string, string> TlsFamilyByFingerprint =
        LoadTlsFingerprintMap();

    private static readonly IReadOnlyDictionary<string, string[]> PlausibleLanguagesByCountry =
        LoadCountryLanguageMap();

    private readonly IOptionsMonitor<FeatureExtractionOptions> _options;
    private readonly UaHeuristics _uaHeuristics = new();

    public FeatureExtractor(IOptionsMonitor<FeatureExtractionOptions> options)
        => _options = options;

    /// <summary>
    /// Builds the feature vector. Missing ≠ zero — NaN-propagation table for degraded inputs:
    /// <code>
    /// Degraded input                       | Effect on vector
    /// -------------------------------------|------------------------------------------------
    /// Beacon == null (no JS — bot or       | HasJsBeacon=false; NaN: MousePathLinearity,
    /// pixel mode)                          |   MeanInterEventMs, StdInterEventMs,
    ///                                      |   FirstInteractionDelayMs, TimeOnPageSec,
    ///                                      |   FormFillTimeSec, StorageAgeZeroRepeat,
    ///                                      |   InputEventCount, ScrollEvents, PagesViewed;
    ///                                      | null: HoneypotTouched, ClickBeforeRender,
    ///                                      |   PointerUntrusted, WebdriverFlag,
    ///                                      |   HeadlessBrowser, BeaconIntegrityOk,
    ///                                      |   ScreenResAnomalous, CookiesDisabled,
    ///                                      |   CanvasFpBlocked, PasteInIdentityFields,
    ///                                      |   TimezoneIpMismatch (beacon side),
    ///                                      |   InputModalityMismatch, FormSubmitted,
    ///                                      |   AutofillDetected
    /// Enrichment DBs missing               | null: IpTor, IpProxyOrVpn, IpDatacenterAsn,
    ///                                      |   IpGeoTargetMismatch, TimezoneIpMismatch,
    ///                                      |   LanguageGeoMismatch; AsnType=Unknown;
    ///                                      |   velocity untouched
    /// UserAgent null                       | null: UaOsMismatch, EmulatorOrVm, IsMobile
    ///                                      |   (and InputModalityMismatch when IsMobile null)
    /// TlsFingerprint null or gate off      | TlsUaMismatch = null
    /// Organic (not paid)                   | ClickIdInvalid = null; IsPaidClick=false
    /// </code>
    /// Velocity members are NEVER NaN (legitimately 0 cold) — only StorageAgeZeroRepeat
    /// maps to NaN, and only because it is keyed by a fingerprint that does not exist
    /// without a beacon.
    /// </summary>
    public FraudFeatureVector Extract(RawSessionData raw)
    {
        var beacon = raw.Beacon;
        var enrichment = raw.Enrichment;
        var velocity = raw.Velocity;

        var ua = string.IsNullOrEmpty(raw.UserAgent) ? null : raw.UserAgent;
        var uaInfo = ua is null ? null : _uaHeuristics.Parse(ua);

        var isMobile = ComputeIsMobile(raw.Headers, uaInfo);

        return new FraudFeatureVector
        {
            // ---- T1 ----
            HoneypotTouched = beacon?.HoneypotTouched,
            ClickBeforeRender = beacon?.ClickBeforeRender,
            PointerUntrusted = beacon?.PointerUntrusted,
            WebdriverFlag = beacon?.WebdriverFlag,
            HeadlessBrowser = beacon?.HeadlessBrowser,
            BeaconIntegrityOk = beacon?.IntegrityOk,
            IpTor = enrichment.IsTor,
            TlsUaMismatch = ComputeTlsUaMismatch(raw.TlsFingerprint, uaInfo),
            IpDatacenterAsn = ComputeIpDatacenterAsn(enrichment),
            IpClicksLastMin = velocity.IpClicksLastMin,
            UaOsMismatch = ComputeUaOsMismatch(raw.Headers, uaInfo),
            MousePathLinearity = beacon is null ? float.NaN : TrajectoryStats.MousePathLinearity(beacon),
            StdInterEventMs = beacon is null ? float.NaN : TrajectoryStats.StdInterEventMs(beacon),
            ClickIdInvalid = ComputeClickIdInvalid(raw),

            // ---- T2 ----
            HasJsBeacon = beacon is not null,
            EmulatorOrVm = uaInfo is null ? null : UaHeuristics.IsEmulatorOrVm(ua!, uaInfo),
            ScreenResAnomalous = ComputeScreenResAnomalous(beacon, isMobile),
            StorageAgeZeroRepeat = beacon?.VisitorId is null ? float.NaN : velocity.StorageAgeZeroRepeat,
            // §7 Private Relay carve-out: Private Relay users are legitimate Safari users.
            IpProxyOrVpn = enrichment.IsPrivateRelay ? false : enrichment.IsProxyOrVpn,
            IpGeoTargetMismatch = ComputeGeoTargetMismatch(raw.Campaign, enrichment.CountryCode),
            TimeOnPageSec = beacon is null ? float.NaN : TrajectoryStats.TimeOnPageSec(beacon),
            FormFillTimeSec = beacon is null ? float.NaN : TrajectoryStats.FormFillTimeSec(beacon),
            FirstInteractionDelayMs = beacon is null ? float.NaN : TrajectoryStats.FirstInteractionDelayMs(beacon),
            InputModalityMismatch = ComputeInputModalityMismatch(beacon, isMobile),
            MeanInterEventMs = beacon is null ? float.NaN : TrajectoryStats.MeanInterEventMs(beacon),
            DeviceSessionsLastHour = velocity.DeviceSessionsLastHour,
            IpDistinctUasLastHour = velocity.IpDistinctUasLastHour,
            DeviceIdsThisIpHour = velocity.DeviceIdsThisIpHour,

            // ---- T3 ----
            CookiesDisabled = beacon is null ? null : !beacon.CookiesEnabled,
            CanvasFpBlocked = beacon?.CanvasFpBlocked,
            TimezoneIpMismatch = ComputeTimezoneIpMismatch(beacon?.Timezone, enrichment.TimeZone),
            LanguageGeoMismatch = ComputeLanguageGeoMismatch(raw.Headers, beacon, enrichment.CountryCode),
            // TODO(P2-06): reputation store — no producer in current plan; RSK-06's default
            // weight is 0 until one exists.
            IpReputationBad = 0f,
            PasteInIdentityFields = beacon?.PasteInIdentityFields,
            ReferrerMissing = !(raw.Headers.TryGetValue("Referer", out var referer)
                                && !string.IsNullOrEmpty(referer)),
            ClockSkewBad = beacon?.ClockSkewBad,
            InputEventCount = beacon is null ? float.NaN : TrajectoryStats.InputEventCount(beacon),
            ScrollEvents = beacon is null ? float.NaN : beacon.ScrollEventCount,
            PagesViewed = beacon is null ? float.NaN : beacon.PagesViewed,

            // ---- CTX ----
            IsMobile = isMobile,
            AsnType = enrichment.AsnType,
            IsPrivateRelay = enrichment.IsPrivateRelay,
            MouseMoveGaps = beacon is null ? float.NaN : beacon.MmN,
            FormSubmitted = beacon?.FormSubmitted,
            AutofillDetected = beacon?.AutofillDetected,
            IsPaidClick = raw.IsPaidClick,
            ChallengeOutcome = raw.ChallengeOutcome,
        };
    }

    /// <summary>ip_datacenter_asn as the T1 rule should see it. The provider's IsDatacenter
    /// is "Datacenter or Cdn" (D24) — right for the flag, wrong for a rule that challenges
    /// every paid click: Private Relay users egress via Cloudflare/Fastly/Akamai, WARP and
    /// Google One VPN users via Cloudflare/Google, and all of them are people. The §7
    /// Private Relay carve-out therefore applies here exactly as it does to IpProxyOrVpn,
    /// and a CDN classification is not a datacenter for this feature (the model still
    /// sees it through the AsnType CTX one-hot). null (unknown) propagates unchanged.</summary>
    private static bool? ComputeIpDatacenterAsn(IpEnrichment enrichment)
    {
        if (enrichment.IsPrivateRelay) return false;
        if (enrichment.AsnType == AsnType.Cdn) return false;
        return enrichment.IsDatacenter;
    }

    /// <summary>Step 6 is_mobile: Sec-CH-UA-Mobile wins when present ("?1"/"?0"); else
    /// DeviceDetector device type ∈ {smartphone, tablet, phablet}; UA absent → null.</summary>
    private static bool? ComputeIsMobile(IReadOnlyDictionary<string, string> headers, UaInfo? uaInfo)
    {
        if (headers.TryGetValue("Sec-CH-UA-Mobile", out var mobileHint))
        {
            if (mobileHint == "?1") return true;
            if (mobileHint == "?0") return false;
        }

        return uaInfo?.IsMobileDevice;
    }

    /// <summary>Step 6 ua_os_mismatch: UA OS family vs Sec-CH-UA-Platform (quotes stripped),
    /// both normalized (iPadOS → iOS). Either side absent/unmappable → null.</summary>
    private static bool? ComputeUaOsMismatch(IReadOnlyDictionary<string, string> headers, UaInfo? uaInfo)
    {
        if (uaInfo?.OsFamily is not string uaOs) return null;
        if (!headers.TryGetValue("Sec-CH-UA-Platform", out var platformHeader)) return null;

        var headerOs = UaHeuristics.NormalizeOsFamily(platformHeader.Trim().Trim('"'));
        if (headerOs is null) return null;

        return !string.Equals(uaOs, headerOs, StringComparison.Ordinal);
    }

    /// <summary>Step 6 screen_res_anomalous. null when no beacon or screen dimensions absent.</summary>
    private static bool? ComputeScreenResAnomalous(BeaconData? beacon, bool? isMobile)
    {
        if (beacon is null) return null;
        if (beacon.ScreenWidth is not int width || beacon.ScreenHeight is not int height) return null;

        if (width <= 0 || height <= 0) return true;
        if (beacon.DevicePixelRatio is double dpr && (dpr <= 0 || dpr > 5)) return true;
        if (beacon.ViewportWidth is int vw && vw > width) return true;
        if (beacon.ViewportHeight is int vh && vh > height) return true;

        var aspectRatio = (double)Math.Max(width, height) / Math.Min(width, height);
        if (aspectRatio > 3.6) return true;

        if (isMobile == false && width < 800) return true;

        return false;
    }

    /// <summary>Step 7 click-id validity truth table.</summary>
    private static bool? ComputeClickIdInvalid(RawSessionData raw)
    {
        if (!raw.IsPaidClick) return null;              // organic — no click id expected
        if (raw.ClickId is null) return true;           // paid but missing
        if (raw.ClickIdFresh == false) return true;     // replayed (Redis dedupe hit)
        if (raw.ClickIdFresh == true) return false;     // fresh
        return null;                                    // capture-time dedupe unavailable
    }

    /// <summary>Step 8 tls_ua_mismatch — config-gated (OFF until INT-05 / D13).
    /// Unknown fingerprint proves nothing → null (avoid false positives).</summary>
    private bool? ComputeTlsUaMismatch(string? tlsFingerprint, UaInfo? uaInfo)
    {
        if (!_options.CurrentValue.TlsUaMismatchEnabled || tlsFingerprint is null) return null;
        if (uaInfo is null) return null;   // no UA to contradict — indeterminate
        if (!TlsFamilyByFingerprint.TryGetValue(tlsFingerprint.Trim(), out var tlsFamily)) return null;

        var uaBrowserFamily = uaInfo.BrowserFamily;
        if (!BrowserTlsFamilies.Contains(tlsFamily))
        {
            // Non-browser handshake (go-http/python/curl/okhttp/java): mismatch iff the
            // UA claims to be a browser.
            return uaBrowserFamily is not null;
        }

        // Browser handshake: mismatch iff the UA browser family is present and different.
        if (uaBrowserFamily is null) return false;
        return !string.Equals(tlsFamily, uaBrowserFamily, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Step 9 ip_geo_target_mismatch.</summary>
    private static bool? ComputeGeoTargetMismatch(CampaignContext? campaign, string? countryCode)
    {
        if (campaign is null || campaign.GeoTargets.Count == 0 || countryCode is null) return null;
        return !campaign.GeoTargets.Contains(countryCode, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Step 9 timezone_ip_mismatch: compare BaseUtcOffset of both IANA zones;
    /// ≥ 2 h difference → true (tolerance avoids neighboring-zone false positives).</summary>
    private static bool? ComputeTimezoneIpMismatch(string? beaconTimezone, string? ipTimezone)
    {
        if (beaconTimezone is null || ipTimezone is null) return null;

        var beaconOffset = TryGetBaseUtcOffset(beaconTimezone);
        var ipOffset = TryGetBaseUtcOffset(ipTimezone);
        if (beaconOffset is not TimeSpan b || ipOffset is not TimeSpan i) return null;

        return Math.Abs((b - i).TotalHours) >= 2;
    }

    private static TimeSpan? TryGetBaseUtcOffset(string timezoneId)
    {
        // IANA ids resolve cross-platform on .NET 8 (ICU). Pure: tz data is a static
        // OS-level table, cached by the runtime after first use.
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezoneId).BaseUtcOffset;
        }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Step 9 language_geo_mismatch: primary Accept-Language subtag (fallback
    /// Beacon.Language) vs the embedded country → plausible-languages map. English is
    /// globally plausible → explicit false (FP guard). Country not in map → null.</summary>
    private static bool? ComputeLanguageGeoMismatch(
        IReadOnlyDictionary<string, string> headers, BeaconData? beacon, string? countryCode)
    {
        string? language = null;
        if (headers.TryGetValue("Accept-Language", out var acceptLanguage))
            language = PrimaryLanguageSubtag(acceptLanguage);
        language ??= PrimaryLanguageSubtag(beacon?.Language);

        if (language is null || countryCode is null) return null;
        if (!PlausibleLanguagesByCountry.TryGetValue(countryCode, out var plausible)) return null;
        if (language == "en") return false; // English is globally plausible — explicit FP guard

        return !plausible.Contains(language, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>First entry before ','/';', then the language subtag before '-', lowercase.
    /// "de-DE,de;q=0.9" → "de". Wildcard/empty → null.</summary>
    private static string? PrimaryLanguageSubtag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var first = value.Split(',')[0].Split(';')[0].Trim();
        if (first.Length == 0 || first == "*") return null;

        var subtag = first.Split('-')[0].Split('_')[0].Trim().ToLowerInvariant();
        return subtag.Length == 0 ? null : subtag;
    }

    /// <summary>Step 10 input_modality_mismatch: input events contradict the claimed device
    /// class. null when no beacon, fewer than 10 input events, or IsMobile indeterminate.</summary>
    private static bool? ComputeInputModalityMismatch(BeaconData? beacon, bool? isMobile)
    {
        if (beacon is null) return null;

        var mousePoints = TrajectoryStats.MousePoints(beacon);
        var inputEventCount = mousePoints + beacon.ClickCount + beacon.KeyCount + beacon.TouchCount;
        if (inputEventCount < 10) return null;
        if (isMobile is not bool mobile) return null;

        if (mobile && mousePoints > 10 && beacon.TouchCount == 0) return true;
        if (!mobile && beacon.TouchCount > 10 && mousePoints == 0) return true;
        return false;
    }

    private static Dictionary<string, string> LoadTlsFingerprintMap()
    {
        using var document = ParseEmbeddedJson(TlsFingerprintsResource);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name.StartsWith('_')) continue; // _comment
            map[property.Name] = property.Value.GetString()
                ?? throw new InvalidOperationException(
                    $"tls-fingerprints.json: value for '{property.Name}' must be a string.");
        }

        return map;
    }

    private static Dictionary<string, string[]> LoadCountryLanguageMap()
    {
        using var document = ParseEmbeddedJson(CountryLanguagesResource);
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name.StartsWith('_')) continue; // _comment
            map[property.Name] = property.Value.EnumerateArray()
                .Select(element => element.GetString()?.ToLowerInvariant()
                    ?? throw new InvalidOperationException(
                        $"country-languages.json: '{property.Name}' contains a non-string entry."))
                .ToArray();
        }

        return map;
    }

    private static JsonDocument ParseEmbeddedJson(string resourceName)
    {
        using var stream = typeof(FeatureExtractor).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        return JsonDocument.Parse(stream);
    }
}
