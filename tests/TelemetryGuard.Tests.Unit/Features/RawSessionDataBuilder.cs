using TelemetryGuard.RiskEngine.Contracts;
using TelemetryGuard.RiskEngine.Enrichment;
using TelemetryGuard.RiskEngine.Features;
using TelemetryGuard.RiskEngine.Velocity;
using TelemetryGuard.Tests.Unit.TestSupport;

namespace TelemetryGuard.Tests.Unit.Features;

/// <summary>Builder with sane defaults (no UA, empty headers, all-null/Unknown enrichment,
/// zero velocity, no beacon) so each test mutates only what it asserts. Reused by
/// RSK-05/06/07 tests.</summary>
public sealed class RawSessionDataBuilder
{
    public const string ChromeWindowsUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

    public const string SafariIosUa =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1";

    public const string IpadSafariUa =
        "Mozilla/5.0 (iPad; CPU OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1";

    public const string FirefoxMacUa =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 14.4; rv:125.0) Gecko/20100101 Firefox/125.0";

    public const string AndroidEmulatorUa =
        "Mozilla/5.0 (Linux; Android 9; Android SDK built for x86 Build/PSR1.180720.075; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/69.0.3497.100 Mobile Safari/537.36";

    public const string SdkGphoneUa =
        "Mozilla/5.0 (Linux; Android 13; sdk_gphone64_x86_64 Build/TE1A.220922.021; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/113.0.5672.136 Mobile Safari/537.36";

    public const string HeadlessChromeUa =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/118.0.0.0 Safari/537.36";

    public const string CurlUa = "curl/8.5.0";

    private string _sessionId = "session-1";
    private string _ip = "203.0.113.10";
    private string? _userAgent;
    private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);
    private string? _tlsFingerprint;
    private bool _isPaidClick;
    private string? _clickId;
    private bool? _clickIdFresh;
    private BeaconData? _beacon;
    private IpEnrichment _enrichment = IpEnrichment.Empty;
    private VelocitySnapshot _velocity = new(0, 0, 0, 0, 0);
    private CampaignContext? _campaign;
    private ChallengeOutcome _challengeOutcome = ChallengeOutcome.NotChallenged;

    /// <summary>A plausible human JS-beacon session: 10 mouse points over a curved path,
    /// desktop geometry, some clicks/keys/scrolls, a day-old storage stamp.</summary>
    public static BeaconData HumanBeacon() => new()
    {
        VisitorId = "v-1234",
        MmN = 9,
        MmMeanMs = 120,
        MmM2 = 9000,
        MmPathLen = 640,
        FirstX = 10,
        FirstY = 20,
        PrevX = 300,
        PrevY = 400,
        FirstInteractionDelayMs = 800,
        ClickCount = 2,
        KeyCount = 5,
        ScrollEventCount = 3,
        TouchCount = 0,
        ScreenWidth = 1920,
        ScreenHeight = 1080,
        ViewportWidth = 1600,
        ViewportHeight = 900,
        DevicePixelRatio = 1.0,
        Timezone = "Europe/Berlin",
        Language = "de-DE",
        StorageAgeSec = 86_400,
        SessionDurationMs = 45_000,
        PagesViewed = 2,
    };

    public static FeatureExtractor Extractor(FeatureExtractionOptions? options = null) =>
        new(new TestOptionsMonitor<FeatureExtractionOptions>(options ?? new FeatureExtractionOptions()));

    public RawSessionDataBuilder WithSessionId(string sessionId) { _sessionId = sessionId; return this; }

    public RawSessionDataBuilder WithIp(string ip) { _ip = ip; return this; }

    public RawSessionDataBuilder WithUserAgent(string? userAgent) { _userAgent = userAgent; return this; }

    public RawSessionDataBuilder WithHeader(string name, string value) { _headers[name] = value; return this; }

    public RawSessionDataBuilder WithTlsFingerprint(string? fingerprint) { _tlsFingerprint = fingerprint; return this; }

    public RawSessionDataBuilder AsPaidClick(string? clickId, bool? clickIdFresh)
    {
        _isPaidClick = true;
        _clickId = clickId;
        _clickIdFresh = clickIdFresh;
        return this;
    }

    public RawSessionDataBuilder WithBeacon(BeaconData? beacon) { _beacon = beacon; return this; }

    public RawSessionDataBuilder WithEnrichment(IpEnrichment enrichment) { _enrichment = enrichment; return this; }

    public RawSessionDataBuilder WithVelocity(VelocitySnapshot velocity) { _velocity = velocity; return this; }

    public RawSessionDataBuilder WithCampaign(params string[] geoTargets)
    {
        _campaign = new CampaignContext { GeoTargets = geoTargets };
        return this;
    }

    public RawSessionDataBuilder WithChallengeOutcome(ChallengeOutcome outcome) { _challengeOutcome = outcome; return this; }

    public RawSessionData Build() => new()
    {
        SessionId = _sessionId,
        Ip = _ip,
        UserAgent = _userAgent,
        Headers = _headers,
        TlsFingerprint = _tlsFingerprint,
        IsPaidClick = _isPaidClick,
        ClickId = _clickId,
        ClickIdFresh = _clickIdFresh,
        Beacon = _beacon,
        Enrichment = _enrichment,
        Velocity = _velocity,
        Campaign = _campaign,
        ChallengeOutcome = _challengeOutcome,
    };
}
