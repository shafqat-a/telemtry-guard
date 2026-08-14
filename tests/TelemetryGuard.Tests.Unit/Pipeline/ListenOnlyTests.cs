using Microsoft.Extensions.Logging.Abstractions;
using TelemetryGuard.RiskEngine.Contracts;
using TelemetryGuard.RiskEngine.Enrichment;
using TelemetryGuard.RiskEngine.Features;
using TelemetryGuard.RiskEngine.Pipeline;
using TelemetryGuard.RiskEngine.Rules;
using TelemetryGuard.RiskEngine.Scoring;
using TelemetryGuard.RiskEngine.Velocity;

namespace TelemetryGuard.Tests.Unit.Pipeline;

/// <summary>RSK-08 (D18 listen-only) acceptance: the shadow scorer never influences
/// the verdict — only the shadow_score/shadow_scorer_version fields are populated,
/// and only for non-whitelisted sessions.</summary>
public sealed class ListenOnlyTests
{
    private const string Sid = "s-listen-only-1";
    private const string Ip = "203.0.113.20";

    // ---------- hand-rolled fakes (mirrors ScoringPipelineTests' style) ----------

    private sealed class FakeSessionStore(SessionState? state) : ISessionStateStore
    {
        public Task<SessionState?> GetAsync(string sessionId, CancellationToken ct) => Task.FromResult(state);
    }

    private sealed class FakeWhitelist(bool result) : IWhitelistCheck
    {
        public Task<bool> IsWhitelistedAsync(string ip, string? visitorId, CancellationToken ct)
            => Task.FromResult(result);
    }

    private sealed class FakeVelocity(VelocitySnapshot snapshot) : IVelocityStore
    {
        public Task<bool?> RecordClickAsync(string ip, string? ua, string? clickId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task RecordSessionAsync(string ip, string? ua, string? visitorId, string sessionId,
            bool storageAgeZero, CancellationToken ct) => throw new NotSupportedException();
        public Task<VelocitySnapshot> ReadAsync(string ip, string? visitorId, CancellationToken ct)
            => Task.FromResult(snapshot);
    }

    private sealed class FakeEnrichment : IIpEnrichmentService
    {
        public IpEnrichment Enrich(string ip) => IpEnrichment.Empty;
    }

    private sealed class FakeRules(T1Result result) : IT1RuleEngine
    {
        public T1Result Evaluate(in FraudFeatureVector v) => result;
    }

    private sealed class FakeScorer(ScoreResult result) : IScorer
    {
        public int Calls;
        public string ScorerVersion => result.ScorerVersion;
        public ScoreResult Score(in FraudFeatureVector vector) { Calls++; return result; }
    }

    private sealed class FakeShadowScorer(ScoreResult result, Exception? throwException = null) : IShadowScorer
    {
        public int Calls;
        public ScoreResult Score(in FraudFeatureVector vector)
        {
            Calls++;
            if (throwException is not null) throw throwException;
            return result;
        }
    }

    private static ClickState Click() => new(
        Ip: Ip,
        UserAgent: "Mozilla/5.0",
        Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        ClickIdType: null,
        ClickId: null,
        ClickIdFresh: null,
        IsPaidClick: false,
        TlsFingerprint: null,
        Timestamp: DateTimeOffset.FromUnixTimeMilliseconds(1_754_000_000_000));

    private static ScoringPipeline Build(
        IScorer scorer, IShadowScorer? shadowScorer, bool whitelisted = false, SessionState? state = null)
    {
        state ??= new SessionState(Click(), Beacon: null, CampaignId: null);
        return new ScoringPipeline(
            new FakeSessionStore(state),
            new FakeWhitelist(whitelisted),
            new FakeEnrichment(),
            new FakeVelocity(new VelocitySnapshot(0, 0, 0, 0, 0)),
            new FeatureExtractor(new TestSupport.TestOptionsMonitor<FeatureExtractionOptions>(new())),
            new FakeRules(T1Result.None),
            scorer,
            new NullCampaignContextProvider(),
            NullLogger<ScoringPipeline>.Instance,
            shadowScorer);
    }

    [Fact]
    public async Task HeuristicEnforcing_FakeShadowScorer_PopulatesShadowFields_WithoutAffectingVerdict()
    {
        var scorer = new FakeScorer(new ScoreResult(20, Array.Empty<string>(), "heuristic-1", FraudFeatureVector.FeatureSetVersion));
        var shadow = new FakeShadowScorer(new ScoreResult(95, Array.Empty<string>(), "lgbm-20260915-a1b2c3d4", FraudFeatureVector.FeatureSetVersion));
        var pipeline = Build(scorer, shadow);

        var outcome = await pipeline.ScoreSessionAsync(Sid);

        Assert.NotNull(outcome);
        Assert.Equal("heuristic-1", outcome.Result.ScorerVersion); // enforcing scorer stamp, unaffected
        Assert.Equal(20, outcome.Result.Score);                    // band comes from the heuristic score
        Assert.Equal(VerdictBand.Allow, outcome.Band);
        Assert.Equal(95, outcome.ShadowScore);                     // shadow score logged separately
        Assert.Equal("lgbm-20260915-a1b2c3d4", outcome.ShadowScorerVersion);
        Assert.Equal(1, shadow.Calls);
    }

    [Fact]
    public async Task ShadowScorerThrows_VerdictUnaffected_ShadowFieldsStayNull()
    {
        var scorer = new FakeScorer(new ScoreResult(15, Array.Empty<string>(), "heuristic-1", FraudFeatureVector.FeatureSetVersion));
        var shadow = new FakeShadowScorer(
            new ScoreResult(0, Array.Empty<string>(), "lgbm-broken", FraudFeatureVector.FeatureSetVersion),
            throwException: new InvalidOperationException("model pool exploded"));
        var pipeline = Build(scorer, shadow);

        var outcome = await pipeline.ScoreSessionAsync(Sid);

        Assert.NotNull(outcome);
        Assert.Equal(15, outcome.Result.Score);
        Assert.Equal("heuristic-1", outcome.Result.ScorerVersion);
        Assert.Null(outcome.ShadowScore);
        Assert.Null(outcome.ShadowScorerVersion);
        Assert.Equal(1, shadow.Calls);
    }

    [Fact]
    public async Task NoShadowScorerRegistered_ShadowFieldsStayNull()
    {
        var scorer = new FakeScorer(new ScoreResult(10, Array.Empty<string>(), "heuristic-1", FraudFeatureVector.FeatureSetVersion));
        var pipeline = Build(scorer, shadowScorer: null);

        var outcome = await pipeline.ScoreSessionAsync(Sid);

        Assert.NotNull(outcome);
        Assert.Null(outcome.ShadowScore);
        Assert.Null(outcome.ShadowScorerVersion);
    }

    [Fact]
    public async Task WhitelistedSession_GetsNoShadowScore_ShadowScorerNeverCalled()
    {
        var scorer = new FakeScorer(new ScoreResult(0, Array.Empty<string>(), "heuristic-1", FraudFeatureVector.FeatureSetVersion));
        var shadow = new FakeShadowScorer(new ScoreResult(99, Array.Empty<string>(), "lgbm-x", FraudFeatureVector.FeatureSetVersion));
        var pipeline = Build(scorer, shadow, whitelisted: true);

        var outcome = await pipeline.ScoreSessionAsync(Sid);

        Assert.NotNull(outcome);
        Assert.True(outcome.Whitelisted);
        Assert.Null(outcome.ShadowScore);
        Assert.Null(outcome.ShadowScorerVersion);
        Assert.Null(outcome.Features); // extraction never ran on the whitelist short-circuit
        Assert.Equal(0, shadow.Calls);
    }

    [Fact]
    public async Task NonWhitelistedSession_PopulatesFeaturesVector()
    {
        var scorer = new FakeScorer(new ScoreResult(5, Array.Empty<string>(), "heuristic-1", FraudFeatureVector.FeatureSetVersion));
        var pipeline = Build(scorer, shadowScorer: null);

        var outcome = await pipeline.ScoreSessionAsync(Sid);

        Assert.NotNull(outcome);
        Assert.NotNull(outcome.Features);
    }

    [Fact]
    public async Task T1FloorStillBinds_InMlNetShadowMode_ViaMathMax()
    {
        // T1 floor 85 outranks BOTH the enforcing scorer's low score and the shadow
        // model's low score — rules only raise (§6.3), identically in every mode.
        var scorer = new FakeScorer(new ScoreResult(10, Array.Empty<string>(), "heuristic-1", FraudFeatureVector.FeatureSetVersion));
        var shadow = new FakeShadowScorer(new ScoreResult(10, Array.Empty<string>(), "lgbm-x", FraudFeatureVector.FeatureSetVersion));
        var pipeline = new ScoringPipeline(
            new FakeSessionStore(new SessionState(Click(), Beacon: null, CampaignId: null)),
            new FakeWhitelist(false),
            new FakeEnrichment(),
            new FakeVelocity(new VelocitySnapshot(0, 0, 0, 0, 0)),
            new FeatureExtractor(new TestSupport.TestOptionsMonitor<FeatureExtractionOptions>(new())),
            new FakeRules(new T1Result(85, ["webdriver_flag"])),
            scorer,
            new NullCampaignContextProvider(),
            NullLogger<ScoringPipeline>.Instance,
            shadow);

        var outcome = await pipeline.ScoreSessionAsync(Sid);

        Assert.NotNull(outcome);
        Assert.Equal(85, outcome.Result.Score);   // Math.Max(10, 85) — floor wins
        Assert.Equal(VerdictBand.Block, outcome.Band);
        Assert.Equal(10, outcome.ShadowScore);    // shadow score is logged as-is, NOT floored
    }
}
