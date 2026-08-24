using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.RiskEngine.Contracts;
using TelemetryGuard.RiskEngine.Enrichment;
using TelemetryGuard.RiskEngine.Features;
using TelemetryGuard.RiskEngine.Pipeline;
using TelemetryGuard.RiskEngine.Rules;
using TelemetryGuard.RiskEngine.Velocity;
using TelemetryGuard.Tests.Unit.TestSupport;

namespace TelemetryGuard.Tests.Unit.Pipeline;

public sealed class ScoringPipelineTests
{
    private const string Sid = "s-0123456789abcdef";
    private const string Ip = "203.0.113.10";

    // ---------- hand-rolled fakes (no mocking framework needed for the pipeline) ----------

    private sealed class FakeSessionStore(SessionState? state) : ISessionStateStore
    {
        public int Calls;
        public Task<SessionState?> GetAsync(string sessionId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(state);
        }
    }

    private sealed class FakeWhitelist(bool result) : IWhitelistCheck
    {
        public int Calls;
        public Task<bool> IsWhitelistedAsync(string ip, string? visitorId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeVelocity(VelocitySnapshot snapshot) : IVelocityStore
    {
        public int ReadCalls;
        public Task<bool?> RecordClickAsync(string ip, string? ua, string? clickId, CancellationToken ct)
            => throw new NotSupportedException("scoring never records");
        public Task RecordSessionAsync(string ip, string? ua, string? visitorId, string sessionId,
            bool storageAgeZero, CancellationToken ct)
            => throw new NotSupportedException("scoring never records");
        public Task<VelocitySnapshot> ReadAsync(string ip, string? visitorId, CancellationToken ct)
        {
            ReadCalls++;
            if (string.IsNullOrEmpty(ip))
            {
                throw new ArgumentException("IP must not be null or empty.", nameof(ip)); // mirror RSK-03
            }
            return Task.FromResult(snapshot);
        }
    }

    private sealed class FakeEnrichment : IIpEnrichmentService
    {
        public IpEnrichment Enrich(string ip) => IpEnrichment.Empty;
    }

    private sealed class FakeRules(T1Result result) : IT1RuleEngine
    {
        public int Calls;
        public T1Result Evaluate(in FraudFeatureVector v)
        {
            Calls++;
            return result;
        }
    }

    private sealed class FakeScorer(ScoreResult result) : IScorer
    {
        public int Calls;
        public string ScorerVersion => result.ScorerVersion;
        public ScoreResult Score(in FraudFeatureVector vector)
        {
            Calls++;
            return result;
        }
    }

    /// <summary>Runs the REAL extractor but captures input and output for assertions.</summary>
    private sealed class CapturingExtractor : IFeatureExtractor
    {
        private readonly FeatureExtractor _real = new(new TestOptionsMonitor<FeatureExtractionOptions>(new()));
        public int Calls;
        public RawSessionData? LastRaw;
        public FraudFeatureVector? LastVector;

        public FraudFeatureVector Extract(RawSessionData raw)
        {
            Calls++;
            LastRaw = raw;
            LastVector = _real.Extract(raw);
            return LastVector;
        }
    }

    private sealed class FakeRebuilder : IWhitelistCacheRebuilder
    {
        public readonly List<string> Scheduled = [];
        public void ScheduleRebuild(string sourceType) => Scheduled.Add(sourceType);
    }

    private sealed class FakeTenantContext : ITenantContext
    {
        public TenantId TenantId { get; } = new(Guid.Parse("aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa"));
        public string? SiteKey => null;
        public bool IsResolved => true;
        public IReadOnlyList<string> Scopes => Array.Empty<string>();
    }

    // ---------- helpers ----------

    private static ClickState Click(string ip = Ip, bool paid = true, string? clickId = "gclid-1", bool? fresh = true)
        => new(
            Ip: ip,
            UserAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/125.0.0.0 Safari/537.36",
            Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Accept-Language"] = "de-DE,de;q=0.9",
                ["Referer"] = "https://ads.example/",
            },
            ClickIdType: clickId is null ? null : "gclid",
            ClickId: clickId,
            ClickIdFresh: fresh,
            IsPaidClick: paid,
            TlsFingerprint: null,
            Timestamp: DateTimeOffset.FromUnixTimeMilliseconds(1_754_000_000_000));

    private static BeaconData Beacon(string? visitorId = "v-1") => new()
    {
        VisitorId = visitorId,
        MmN = 40,
        MmMeanMs = 35,
        MmM2 = 52_000,
        MmPathLen = 900,
        FirstX = 10, FirstY = 10, PrevX = 500, PrevY = 400,
        ClickCount = 2, KeyCount = 4, ScrollEventCount = 3,
        FirstInteractionDelayMs = 450,
        SessionDurationMs = 45_000,
    };

    private static (ScoringPipeline pipeline, FakeVelocity velocity, FakeRules rules, FakeScorer scorer,
        CapturingExtractor extractor, FakeWhitelist whitelist)
        Build(SessionState? state, bool whitelisted = false, int rulesFloor = 0,
              IReadOnlyList<string>? hits = null, int scorerScore = 0)
    {
        var velocity = new FakeVelocity(new VelocitySnapshot(1, 1, 1, 1, 0));
        var rules = new FakeRules(rulesFloor > 0
            ? new T1Result(rulesFloor, hits ?? ["fake_rule"])
            : T1Result.None);
        var scorer = new FakeScorer(new ScoreResult(
            scorerScore, Array.Empty<string>(), "fake-scorer", FraudFeatureVector.FeatureSetVersion));
        var extractor = new CapturingExtractor();
        var whitelist = new FakeWhitelist(whitelisted);
        var pipeline = new ScoringPipeline(
            new FakeSessionStore(state),
            whitelist,
            new FakeEnrichment(),
            velocity,
            extractor,
            rules,
            scorer,
            new NullCampaignContextProvider(),
            NullLogger<ScoringPipeline>.Instance);
        return (pipeline, velocity, rules, scorer, extractor, whitelist);
    }

    // ---------- criteria ----------

    [Fact]
    public async Task UnknownSession_ReturnsNull_NoThrow()
    {
        var (pipeline, velocity, rules, scorer, _, _) = Build(state: null);

        var outcome = await pipeline.ScoreSessionAsync(Sid);

        Assert.Null(outcome);
        Assert.Equal(0, velocity.ReadCalls);
        Assert.Equal(0, rules.Calls);
        Assert.Equal(0, scorer.Calls);
    }

    [Fact]
    public async Task WhitelistedIp_ShortCircuits_SkippingEverything()
    {
        var state = new SessionState(Click(), Beacon(), CampaignId: null);
        var (pipeline, velocity, rules, scorer, extractor, whitelist) = Build(state, whitelisted: true);

        var outcome = await pipeline.ScoreSessionAsync(Sid);

        Assert.NotNull(outcome);
        Assert.Equal(0, outcome.Result.Score);
        Assert.Equal(["whitelisted"], outcome.Result.RuleHits);
        Assert.Equal("whitelist-short-circuit", outcome.Result.ScorerVersion);
        Assert.Equal(FraudFeatureVector.FeatureSetVersion, outcome.Result.FeatureSetVersion);
        Assert.Equal(VerdictBand.Allow, outcome.Band);
        Assert.True(outcome.Whitelisted);
        Assert.True(outcome.DurationMs >= 0);
        // NOTHING else ran (D19 short-circuit skips extraction/rules/scoring/velocity):
        Assert.Equal(1, whitelist.Calls);
        Assert.Equal(0, velocity.ReadCalls);
        Assert.Equal(0, extractor.Calls);
        Assert.Equal(0, rules.Calls);
        Assert.Equal(0, scorer.Calls);
    }

    [Fact]
    public async Task RuleFloorAboveScorer_MaxFoldTakesFloor_KeepsScorerVersion()
    {
        var state = new SessionState(Click(), Beacon(), CampaignId: null);
        var (pipeline, _, _, _, _, _) = Build(
            state, rulesFloor: 85, hits: ["webdriver_flag"], scorerScore: 40);

        var outcome = await pipeline.ScoreSessionAsync(Sid);

        Assert.NotNull(outcome);
        Assert.Equal(85, outcome.Result.Score);              // Max(40, 85)
        Assert.Equal(VerdictBand.Block, outcome.Band);
        Assert.Equal(["webdriver_flag"], outcome.Result.RuleHits); // hits from the rule engine
        Assert.Equal("fake-scorer", outcome.Result.ScorerVersion); // NOT overwritten by the floor
        Assert.False(outcome.Whitelisted);
    }

    [Fact]
    public async Task ScorerAboveFloor_MaxFoldTakesScorer_NotSum()
    {
        var state = new SessionState(Click(), Beacon(), CampaignId: null);
        var (pipeline, _, _, _, _, _) = Build(
            state, rulesFloor: 31, hits: ["ua_os_mismatch"], scorerScore: 60);

        var outcome = await pipeline.ScoreSessionAsync(Sid);

        Assert.NotNull(outcome);
        Assert.Equal(60, outcome.Result.Score);              // Max(60, 31), not 91
        Assert.Equal(VerdictBand.Challenge, outcome.Band);
        Assert.Equal(["ua_os_mismatch"], outcome.Result.RuleHits);
    }

    [Fact]
    public async Task NoBeaconSession_ScoresWithHasJsBeaconFalse()
    {
        var state = new SessionState(Click(), Beacon: null, CampaignId: null);
        var (pipeline, velocity, _, _, extractor, _) = Build(state, scorerScore: 20);

        var outcome = await pipeline.ScoreSessionAsync(Sid);

        Assert.NotNull(outcome); // §6.1: beacon-less sessions score normally, never fail
        Assert.NotNull(extractor.LastVector);
        Assert.False(extractor.LastVector!.HasJsBeacon);
        Assert.True(float.IsNaN(extractor.LastVector.MousePathLinearity)); // degraded, not zero
        Assert.Equal(1, velocity.ReadCalls); // ip present → velocity still read
    }

    [Fact]
    public async Task BeaconOnlySession_NoClickHash_ScoresWithEmptyIpAndColdVelocity()
    {
        // No click hash → no ip. RSK-03's ReadAsync rejects an empty ip by contract,
        // so the pipeline substitutes cold counters instead of calling it.
        var state = new SessionState(Click: null, Beacon(), CampaignId: null);
        var (pipeline, velocity, _, _, extractor, _) = Build(state, scorerScore: 10);

        var outcome = await pipeline.ScoreSessionAsync(Sid);

        Assert.NotNull(outcome);
        Assert.Equal(0, velocity.ReadCalls);
        Assert.NotNull(extractor.LastRaw);
        Assert.Equal(string.Empty, extractor.LastRaw!.Ip);
        Assert.True(extractor.LastVector!.HasJsBeacon);
        Assert.False(extractor.LastRaw.IsPaidClick);
    }

    [Theory]
    [InlineData(ChallengeOutcome.Passed)]
    [InlineData(ChallengeOutcome.Failed)]
    public async Task ChallengeRescore_OutcomeParameterFlowsIntoVector(ChallengeOutcome challenge)
    {
        var state = new SessionState(Click(), Beacon(), CampaignId: null);
        var (pipeline, _, _, _, extractor, _) = Build(state, scorerScore: 5);

        await pipeline.ScoreSessionAsync(Sid, challenge);

        Assert.Equal(challenge, extractor.LastVector!.ChallengeOutcome);
    }

    [Fact]
    public async Task DefaultCall_ProducesNotChallengedVector()
    {
        var state = new SessionState(Click(), Beacon(), CampaignId: null);
        var (pipeline, _, _, _, extractor, _) = Build(state, scorerScore: 5);

        await pipeline.ScoreSessionAsync(Sid);

        Assert.Equal(ChallengeOutcome.NotChallenged, extractor.LastVector!.ChallengeOutcome);
    }

    [Fact]
    public async Task ClickContext_FlowsVerbatimIntoRawSessionData()
    {
        var state = new SessionState(Click(), Beacon(), CampaignId: "c-42");
        var (pipeline, _, _, _, extractor, _) = Build(state, scorerScore: 5);

        await pipeline.ScoreSessionAsync(Sid);

        var raw = extractor.LastRaw!;
        Assert.Equal(Ip, raw.Ip);
        Assert.True(raw.IsPaidClick);
        Assert.Equal("gclid-1", raw.ClickId);
        Assert.True(raw.ClickIdFresh);
        Assert.Equal("de-DE,de;q=0.9", raw.Headers["Accept-Language"]);
        Assert.Same(state.Beacon, raw.Beacon); // untouched pass-through (RSK-04 owns mapping)
    }

    // ---------- metric emission ----------

    [Fact]
    public async Task Metric_EmittedOncePerCall_WithBandTag()
    {
        var recorded = new ConcurrentBag<(double Value, string? Band, object? Whitelisted)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == RiskMetrics.MeterName
                    && instrument.Name == "tg.scoring.duration_ms")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            string? band = null;
            object? whitelisted = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "band") band = tag.Value as string;
                if (tag.Key == "whitelisted") whitelisted = tag.Value;
            }
            recorded.Add((value, band, whitelisted));
        });
        listener.Start();

        // not_found band is unique to this test's unknown-session call (avoids
        // cross-class pollution on the process-wide static meter).
        var (pipeline, _, _, _, _, _) = Build(state: null);
        var outcome = await pipeline.ScoreSessionAsync(Sid);
        Assert.Null(outcome);

        var notFound = recorded.Where(m => m.Band == "not_found").ToList();
        var single = Assert.Single(notFound);
        Assert.True(single.Value >= 0);
        Assert.Equal(false, single.Whitelisted);
    }

    [Fact]
    public async Task Metric_ScoredCall_CarriesBandAndWhitelistedTags()
    {
        var recorded = new ConcurrentBag<(string? Band, object? Whitelisted)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == RiskMetrics.MeterName
                    && instrument.Name == "tg.scoring.duration_ms")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            string? band = null;
            object? whitelisted = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "band") band = tag.Value as string;
                if (tag.Key == "whitelisted") whitelisted = tag.Value;
            }
            recorded.Add((band, whitelisted));
        });
        listener.Start();

        var state = new SessionState(Click(), Beacon(), CampaignId: null);
        var (pipeline, _, _, _, _, _) = Build(state, rulesFloor: 85, hits: ["webdriver_flag"], scorerScore: 40);
        var outcome = await pipeline.ScoreSessionAsync(Sid);

        Assert.Equal(VerdictBand.Block, outcome!.Band);
        Assert.Contains(recorded, m => m.Band == "block" && Equals(m.Whitelisted, false));
    }

    // ---------- whitelist check (RedisWhitelistCheck over substituted Redis) ----------

    private static (RedisWhitelistCheck check, IBatch batch, FakeRebuilder rebuilder) BuildWhitelistCheck(
        bool ipExists, bool ipMember, bool fpExists = true, bool fpMember = false)
    {
        var mux = Substitute.For<IConnectionMultiplexer>();
        var db = Substitute.For<IDatabase>();
        var batch = Substitute.For<IBatch>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        db.CreateBatch(Arg.Any<object?>()).Returns(batch);

        batch.SetContainsAsync(
                Arg.Is<RedisKey>(k => k.ToString().EndsWith(":wl:ip")),
                Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(ipMember));
        batch.KeyExistsAsync(
                Arg.Is<RedisKey>(k => k.ToString().EndsWith(":wl:ip")), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(ipExists));
        batch.SetContainsAsync(
                Arg.Is<RedisKey>(k => k.ToString().EndsWith(":wl:fingerprint")),
                Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(fpMember));
        batch.KeyExistsAsync(
                Arg.Is<RedisKey>(k => k.ToString().EndsWith(":wl:fingerprint")), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(fpExists));

        var rebuilder = new FakeRebuilder();
        return (new RedisWhitelistCheck(mux, new FakeTenantContext(), rebuilder), batch, rebuilder);
    }

    [Fact]
    public async Task WhitelistCacheMiss_NotWhitelisted_SchedulesRebuildWithSourceType()
    {
        var (check, _, rebuilder) = BuildWhitelistCheck(ipExists: false, ipMember: false);

        // DAT-07 miss behavior: missing key = "unknown" → NOT whitelisted this request;
        // ScheduleRebuild is void (fire-and-forget) — the request path cannot await it.
        var result = await check.IsWhitelistedAsync(Ip, "v-1", CancellationToken.None);

        Assert.False(result);
        Assert.Contains("ip", rebuilder.Scheduled);
        Assert.DoesNotContain("device_id", rebuilder.Scheduled); // MVP: device_id set not consulted
    }

    [Fact]
    public async Task WhitelistHit_ReturnsTrue_NoRebuild()
    {
        var (check, _, rebuilder) = BuildWhitelistCheck(ipExists: true, ipMember: true);

        var result = await check.IsWhitelistedAsync(Ip, "v-1", CancellationToken.None);

        Assert.True(result);
        Assert.Empty(rebuilder.Scheduled);
    }

    [Fact]
    public async Task WhitelistKeysExistButNoMember_False_NoRebuild()
    {
        var (check, _, rebuilder) = BuildWhitelistCheck(ipExists: true, ipMember: false);

        var result = await check.IsWhitelistedAsync(Ip, "v-1", CancellationToken.None);

        Assert.False(result);
        Assert.Empty(rebuilder.Scheduled);
    }

    [Fact]
    public async Task WhitelistFingerprintMatch_ReturnsTrue()
    {
        var (check, _, _) = BuildWhitelistCheck(ipExists: true, ipMember: false, fpExists: true, fpMember: true);

        Assert.True(await check.IsWhitelistedAsync(Ip, "v-1", CancellationToken.None));
    }

    [Fact]
    public async Task WhitelistNoVisitorId_ConsultsOnlyIpSet()
    {
        var (check, batch, rebuilder) = BuildWhitelistCheck(ipExists: false, ipMember: false);

        var result = await check.IsWhitelistedAsync(Ip, visitorId: null, CancellationToken.None);

        Assert.False(result);
        Assert.Equal(["ip"], rebuilder.Scheduled);
        await batch.DidNotReceive().SetContainsAsync(
            Arg.Is<RedisKey>(k => k.ToString().EndsWith(":wl:fingerprint")),
            Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
    }

    // ---------- DI wiring ----------

    [Fact]
    public void AddScoringPipeline_ResolvesWithFakesForRedisBackedPieces()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var config = new ConfigurationBuilder().Build();

        // Fakes for every Redis-backed piece — no multiplexer needed.
        services.AddSingleton<ISessionStateStore>(new FakeSessionStore(null));
        services.AddSingleton<IWhitelistCheck>(new FakeWhitelist(false));
        services.AddSingleton<IVelocityStore>(new FakeVelocity(new VelocitySnapshot(0, 0, 0, 0, 0)));

        services.AddScoringPipeline(config);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<IScoringPipeline>();
        Assert.IsType<ScoringPipeline>(pipeline);

        // Enrichment interface is decorated with the memory cache.
        var enrichment = scope.ServiceProvider.GetRequiredService<IIpEnrichmentService>();
        Assert.IsType<CachedIpEnrichmentService>(enrichment);

        // Defaults are TryAdd'ed: no-op rebuilder + null campaign provider.
        Assert.IsType<NoOpWhitelistCacheRebuilder>(
            scope.ServiceProvider.GetRequiredService<IWhitelistCacheRebuilder>());
        Assert.IsType<NullCampaignContextProvider>(
            scope.ServiceProvider.GetRequiredService<ICampaignContextProvider>());
    }

    [Fact]
    public void AddScoringPipeline_PreRegisteredFakeEnrichment_IsNotClobbered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISessionStateStore>(new FakeSessionStore(null));
        services.AddSingleton<IWhitelistCheck>(new FakeWhitelist(false));
        services.AddSingleton<IVelocityStore>(new FakeVelocity(new VelocitySnapshot(0, 0, 0, 0, 0)));
        var fake = new FakeEnrichment();
        services.AddSingleton<IIpEnrichmentService>(fake);

        services.AddScoringPipeline(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        Assert.Same(fake, provider.GetRequiredService<IIpEnrichmentService>());
    }

    // ---------- enrichment cache decorator ----------

    private sealed class CountingEnrichment : IIpEnrichmentService
    {
        public int Calls;
        public IpEnrichment Enrich(string ip)
        {
            Calls++;
            return new IpEnrichment { CountryCode = "DE" };
        }
    }

    [Fact]
    public void CachedEnrichment_SecondLookupServedFromCache()
    {
        var inner = new CountingEnrichment();
        using var cached = new CachedIpEnrichmentService(inner);

        var first = cached.Enrich(Ip);
        var second = cached.Enrich(Ip);

        Assert.Equal(1, inner.Calls);
        Assert.Same(first, second);
        Assert.Equal("DE", second.CountryCode);
    }

    [Fact]
    public void CachedEnrichment_EmptyIp_BypassesCache()
    {
        var inner = new CountingEnrichment();
        using var cached = new CachedIpEnrichmentService(inner);

        cached.Enrich("");
        cached.Enrich("");

        Assert.Equal(2, inner.Calls);
    }
}
