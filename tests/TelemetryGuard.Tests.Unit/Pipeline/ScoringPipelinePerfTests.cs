using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TelemetryGuard.RiskEngine.Contracts;
using TelemetryGuard.RiskEngine.Enrichment;
using TelemetryGuard.RiskEngine.Features;
using TelemetryGuard.RiskEngine.Pipeline;
using TelemetryGuard.RiskEngine.Rules;
using TelemetryGuard.RiskEngine.Scoring;
using TelemetryGuard.RiskEngine.Velocity;
using TelemetryGuard.Tests.Unit.TestSupport;
using Xunit.Abstractions;

namespace TelemetryGuard.Tests.Unit.Pipeline;

/// <summary>
/// Env-gated perf test (RSK-07 step 8): measures the full COMPUTE path — real
/// FeatureExtractor + T1RuleEngine + HeuristicScorer + IpEnrichmentService (no DBs) —
/// with in-memory fakes for the Redis-backed pieces (production Redis RTTs are bounded
/// separately by RSK-03's single-batch guarantee). Run with:
///   RUN_PERF_TESTS=1 dotnet test --filter "FullyQualifiedName~ScoringPipelinePerf"
/// Skipped (honest skip reporting via SkippableFact) when the env var is absent —
/// CI boxes are noisy; do not run perf assertions in CI by default.
/// </summary>
public sealed class ScoringPipelinePerfTests
{
    private readonly ITestOutputHelper _output;

    public ScoringPipelinePerfTests(ITestOutputHelper output) => _output = output;

    private sealed class FixedSessionStore(SessionState state) : ISessionStateStore
    {
        public Task<SessionState?> GetAsync(string sessionId, CancellationToken ct)
            => Task.FromResult<SessionState?>(state);
    }

    private sealed class NeverWhitelisted : IWhitelistCheck
    {
        public Task<bool> IsWhitelistedAsync(string ip, string? visitorId, CancellationToken ct)
            => Task.FromResult(false);
    }

    private sealed class FixedVelocity(VelocitySnapshot snapshot) : IVelocityStore
    {
        public Task<bool?> RecordClickAsync(string ip, string? ua, string? clickId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task RecordSessionAsync(string ip, string? ua, string? visitorId, string sessionId,
            bool storageAgeZero, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<VelocitySnapshot> ReadAsync(string ip, string? visitorId, CancellationToken ct)
            => Task.FromResult(snapshot);
    }

    /// <summary>Rich pre-built session: aggregates modelling a 200-move session with all
    /// counters populated, plus a paid click with full header context.</summary>
    private static SessionState RichSession()
    {
        var click = new ClickState(
            Ip: "203.0.113.77",
            UserAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36",
            Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Accept-Language"] = "de-DE,de;q=0.9,en;q=0.8",
                ["Referer"] = "https://www.google.com/",
                ["Sec-CH-UA"] = "\"Chromium\";v=\"125\", \"Google Chrome\";v=\"125\"",
                ["Sec-CH-UA-Mobile"] = "?0",
                ["Sec-CH-UA-Platform"] = "\"Windows\"",
                ["header_order"] = "Host,Connection,User-Agent,Accept,Referer,Accept-Language",
            },
            ClickIdType: "gclid",
            ClickId: "EAIaIQobChMI-perf-test",
            ClickIdFresh: true,
            IsPaidClick: true,
            TlsFingerprint: null,
            Timestamp: DateTimeOffset.FromUnixTimeMilliseconds(1_754_000_000_000));

        var beacon = new BeaconData
        {
            VisitorId = "v-perf-fingerprint",
            MmN = 200,                       // 200 inter-move gaps → 201 points
            MmMeanMs = 34.7,
            MmM2 = 61_250,                   // population std ≈ 17.5 ms — human-ish
            MmPathLen = 5_400,
            FirstX = 12, FirstY = 40, PrevX = 880, PrevY = 560,
            FirstInteractionDelayMs = 480,
            ClickCount = 3,
            KeyCount = 22,
            ScrollEventCount = 9,
            TouchCount = 0,
            WebdriverFlag = false,
            HeadlessBrowser = false,
            HoneypotTouched = false,
            PointerUntrusted = false,
            ClickBeforeRender = false,
            IntegrityOk = true,
            ScreenWidth = 1920,
            ScreenHeight = 1080,
            DevicePixelRatio = 1.25,
            Timezone = "Europe/Berlin",
            Language = "de-DE",
            CookiesEnabled = true,
            CanvasFpBlocked = false,
            StorageAgeSec = 86_400,
            SessionDurationMs = 47_500,
            PagesViewed = 2,
            FormSubmitted = true,
            FormFirstFocusTMs = 12_000,
            FormSubmitTMs = 29_500,
            AutofillDetected = false,
            PasteInIdentityFields = false,
        };

        return new SessionState(click, beacon, CampaignId: "11111111-2222-4333-8444-555555555555");
    }

    [SkippableFact]
    public async Task ScoreSessionAsync_P99_Under50ms_Over1000Iterations()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("RUN_PERF_TESTS") == "1",
            "Set RUN_PERF_TESTS=1 to run the scoring-pipeline perf assertion (D3 budget).");

        // Real compute path; enrichment with no database files (null path — degrades
        // to Empty fields exactly like a fresh install).
        using var enrichmentService = new IpEnrichmentService(
            Options.Create(new IpEnrichmentOptions
            {
                DataDir = Path.Combine(Path.GetTempPath(), "tg-perf-no-geo-dbs"),
            }),
            NullLogger<IpEnrichmentService>.Instance);
        using var cachedEnrichment = new CachedIpEnrichmentService(enrichmentService);

        var pipeline = new ScoringPipeline(
            new FixedSessionStore(RichSession()),
            new NeverWhitelisted(),
            cachedEnrichment,
            new FixedVelocity(new VelocitySnapshot(3, 2, 1, 2, 0)),
            new FeatureExtractor(new TestOptionsMonitor<FeatureExtractionOptions>(new())),
            new T1RuleEngine(new TestOptionsMonitor<RulesOptions>(new())),
            new HeuristicScorer(new TestOptionsMonitor<HeuristicWeights>(new())),
            new NullCampaignContextProvider(),
            NullLogger<ScoringPipeline>.Instance);

        const int warmup = 100;
        const int iterations = 1000;

        for (var i = 0; i < warmup; i++)
        {
            var outcome = await pipeline.ScoreSessionAsync("s-perf-warmup");
            Assert.NotNull(outcome);
        }

        var samples = new double[iterations];
        for (var i = 0; i < iterations; i++)
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            var outcome = await pipeline.ScoreSessionAsync("s-perf");
            samples[i] = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Assert.NotNull(outcome);
        }

        Array.Sort(samples);
        var p50 = Percentile(samples, 0.50);
        var p95 = Percentile(samples, 0.95);
        var p99 = Percentile(samples, 0.99);
        _output.WriteLine($"scoring pipeline latency over {iterations} iterations (ms): " +
                          $"p50={p50:F3} p95={p95:F3} p99={p99:F3} max={samples[^1]:F3}");

        Assert.True(p99 < 50, $"p99 {p99:F3} ms breaches the 50 ms budget (D3)");
    }

    /// <summary>Nearest-rank percentile over a pre-sorted ascending array.</summary>
    private static double Percentile(double[] sorted, double p)
    {
        var rank = (int)Math.Ceiling(p * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }
}
