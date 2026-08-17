using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Api.Options;
using TelemetryGuard.Api.Services;
using TelemetryGuard.Core.Analytics;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Core.Time;
using TelemetryGuard.Data.Models;
using TelemetryGuard.Data.Repositories;
using TelemetryGuard.RiskEngine.Contracts;
using TelemetryGuard.RiskEngine.Pipeline;

namespace TelemetryGuard.Tests.Unit.Api;

/// <summary>
/// API-06: VerdictFinalizer.FinalizeAsync — the single shared finalization path
/// consumed by /decide (API-05) and the grace-period worker
/// (VerdictFinalizerService). Everything here is a hand-rolled fake / NSubstitute
/// Redis harness (same pattern API-02/05's endpoint tests use) — no containers,
/// no real SQL/ClickHouse: idempotency, precomputed short-circuit, the
/// band/EnforcementMode/exclusion matrix, summary delta correctness, weak-label
/// emission rules, and partial-failure isolation across steps 5-8.
/// </summary>
public sealed class VerdictFinalizerTests
{
    private static readonly Guid TenantGuid = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");
    private static readonly TenantId TestTenantId = new(TenantGuid);
    private static readonly string Tid = TenantGuid.ToString("D");
    private const string Sid = "0123456789abcdef0123456789abcdef";
    private static readonly DateTime FixedNow = new(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

    // ---------------------------------------------------------------- fakes --

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = FixedNow;
    }

    private sealed class FakeTenantContext(string? siteKey) : ITenantContext
    {
        public TenantId TenantId => TestTenantId;
        public string? SiteKey => siteKey;
        public bool IsResolved => true;
    }

    private sealed class FakePipeline : IScoringPipeline
    {
        public int Calls;
        public Func<string, ChallengeOutcome, ScoringOutcome?> Respond = (_, _) => null;

        public Task<ScoringOutcome?> ScoreSessionAsync(
            string sessionId, ChallengeOutcome outcome = ChallengeOutcome.NotChallenged, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(Respond(sessionId, outcome));
        }
    }

    private sealed class CapturingSink : IEventSink
    {
        public readonly List<ClickEvent> Events = [];
        public Exception? ThrowOnCall;

        public ValueTask WriteBatchAsync(ReadOnlyMemory<ClickEvent> events, CancellationToken ct)
        {
            if (ThrowOnCall is not null) throw ThrowOnCall;
            lock (Events) Events.AddRange(events.ToArray());
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingExclusions : IExclusionQueueRepository
    {
        public readonly List<ExclusionQueueInsert> Entries = [];
        public Exception? ThrowOnCall;

        public Task EnqueueAsync(ExclusionQueueInsert entry, CancellationToken ct)
        {
            if (ThrowOnCall is not null) throw ThrowOnCall;
            lock (Entries) Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    /// <summary>UpsertDailySummaryAsync throws: proves the acceptance criterion
    /// "the absolute UpsertDailySummaryAsync is never called from this task's code"
    /// by making a call to it a test failure, not just an unasserted no-op.</summary>
    private sealed class CapturingSummaries : IVerdictSummaryRepository
    {
        public readonly List<VerdictDailySummaryRow> Increments = [];
        public Exception? ThrowOnIncrement;

        public Task IncrementDailySummaryAsync(VerdictDailySummaryRow delta, CancellationToken ct)
        {
            if (ThrowOnIncrement is not null) throw ThrowOnIncrement;
            lock (Increments) Increments.Add(delta);
            return Task.CompletedTask;
        }

        public Task UpsertDailySummaryAsync(VerdictDailySummaryRow row, CancellationToken ct)
            => throw new InvalidOperationException("VerdictFinalizer must never call the absolute rollup upsert.");
        public Task UpsertFlaggedSourceAsync(FlaggedSourceDailyRow row, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<VerdictDailySummaryRow>> GetDailySummariesAsync(
            Guid campaignId, DateOnly from, DateOnly to, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<VerdictDailySummaryRow>> GetTenantDailySummariesAsync(
            DateOnly from, DateOnly to, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<FlaggedSourceDailyRow>> GetTopFlaggedSourcesAsync(
            DateOnly from, DateOnly to, int limit, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class CapturingLabelSink : ILabelSink
    {
        public readonly List<LabelEvent> Labels = [];
        public Exception? ThrowOnCall;

        public ValueTask WriteAsync(LabelEvent label, CancellationToken ct)
        {
            if (ThrowOnCall is not null) throw ThrowOnCall;
            lock (Labels) Labels.Add(label);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeTenantRepository : ITenantRepository
    {
        public TenantRecord? Record;
        public Task<TenantRecord?> GetCurrentAsync(CancellationToken ct) => Task.FromResult(Record);
        public Task<bool> UpdateRetentionDaysAsync(int retentionDays, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> UpdateEnforcementModeAsync(byte enforcementMode, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeCampaignRepository : ICampaignRepository
    {
        public readonly Dictionary<Guid, CampaignRecord> Campaigns = new();
        public Task CreateAsync(CampaignRecord campaign, CancellationToken ct) => throw new NotSupportedException();
        public Task<CampaignRecord?> GetAsync(Guid campaignId, CancellationToken ct)
            => Task.FromResult(Campaigns.TryGetValue(campaignId, out var c) ? c : null);
        public Task<CampaignRedirect?> GetRedirectAsync(Guid campaignId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CampaignRecord>> ListAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> UpdateAsync(CampaignRecord campaign, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>NSubstitute IConnectionMultiplexer/IDatabase/IBatch harness — same
    /// pattern as DecisionEndpointTests' RedisHarness, extended with the batched
    /// HashGetAll/KeyExists reads VerdictFinalizer issues for the click context and
    /// beacon presence.</summary>
    private sealed class RedisHarness
    {
        public readonly IConnectionMultiplexer Mux;
        public readonly IDatabase Db;
        public readonly List<(string Key, string Member)> SortedSetRemoves = [];
        public readonly List<string> KeyDeletes = [];

        private readonly Dictionary<string, HashEntry[]> _hashes = new();
        private readonly Dictionary<string, bool> _keyExists = new();

        /// <summary>Governs the StringSetAsync (SETNX) idempotency claim result.</summary>
        public bool ClaimResult = true;

        public RedisHarness()
        {
            Mux = Substitute.For<IConnectionMultiplexer>();
            Db = Substitute.For<IDatabase>();
            Mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(Db);

            // VerdictFinalizer calls the exact 4-arg overload (key, value, expiry, when) —
            // StackExchange.Redis 3.x has several StringSetAsync overloads that resolve
            // differently by argument count/type, so the substitute setup must match the
            // SAME overload the production code invokes, not just "similar" arguments.
            Db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>())
                .Returns(_ => Task.FromResult(ClaimResult));

            Db.SortedSetRemoveAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
                .Returns(true)
                .AndDoes(ci =>
                {
                    lock (SortedSetRemoves)
                        SortedSetRemoves.Add((ci.ArgAt<RedisKey>(0).ToString(), ci.ArgAt<RedisValue>(1).ToString()));
                });

            Db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
                .Returns(true)
                .AndDoes(ci => { lock (KeyDeletes) KeyDeletes.Add(ci.ArgAt<RedisKey>(0).ToString()); });

            var batch = Substitute.For<IBatch>();
            Db.CreateBatch(Arg.Any<object?>()).Returns(batch);

            batch.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
                .Returns(ci => Task.FromResult(
                    _hashes.TryGetValue(ci.ArgAt<RedisKey>(0).ToString(), out var v) ? v : Array.Empty<HashEntry>()));

            batch.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
                .Returns(ci => Task.FromResult(
                    _keyExists.TryGetValue(ci.ArgAt<RedisKey>(0).ToString(), out var v) && v));
        }

        public void SeedClickHash(string sid, params HashEntry[] entries) => _hashes[$"t:{Tid}:click:{sid}"] = entries;
        public void SeedBeaconExists(string sid, bool exists) => _keyExists[$"t:{Tid}:sess:{sid}"] = exists;
    }

    // ------------------------------------------------------------- harness --

    private sealed class Harness
    {
        public readonly FakePipeline Pipeline = new();
        public readonly CapturingSink Sink = new();
        public readonly CapturingExclusions Exclusions = new();
        public readonly CapturingSummaries Summaries = new();
        public readonly CapturingLabelSink Labels = new();
        public readonly FakeTenantRepository Tenants = new();
        public readonly FakeCampaignRepository Campaigns = new();
        public readonly RedisHarness Redis = new();
        public readonly FakeClock Clock = new();
        public readonly MemoryCache Cache = new(new MemoryCacheOptions());
        public ScoringBandOptions Bands = new();
        public RetentionOptions Retention = new() { DefaultDays = 90 };
        public string? SiteKey;

        public VerdictFinalizer Build() => new(
            Pipeline, Sink, Exclusions, Summaries, Labels, Tenants, Campaigns,
            Redis.Mux, Clock, Cache, new FakeTenantContext(SiteKey),
            Options.Create(Bands), Options.Create(Retention),
            NullLogger<VerdictFinalizer>.Instance);
    }

    private static ScoringOutcome Outcome(
        int score, string[]? ruleHits = null, bool whitelisted = false, string scorerVersion = "test-scorer-1")
        => new(new ScoreResult(score, ruleHits ?? Array.Empty<string>(), scorerVersion, 1),
               BandMapper.ToBand(score), whitelisted, 1.0);

    // ------------------------------------------------------------------ tests --

    [Fact]
    public async Task Idempotency_SecondCall_ShortCircuitsOnTheSetnxClaim()
    {
        var h = new Harness();
        h.Pipeline.Respond = (_, _) => Outcome(10);
        var sut = h.Build();

        await sut.FinalizeAsync(TestTenantId, Sid, FinalizeTrigger.Decide, null, CancellationToken.None);
        Assert.Equal(1, h.Pipeline.Calls);
        Assert.Single(h.Sink.Events);
        Assert.Single(h.Summaries.Increments);

        h.Redis.ClaimResult = false; // simulate: another caller already holds the claim
        await sut.FinalizeAsync(TestTenantId, Sid, FinalizeTrigger.GraceExpired, null, CancellationToken.None);

        Assert.Equal(1, h.Pipeline.Calls);       // no second score
        Assert.Single(h.Sink.Events);             // no second verdict event
        Assert.Single(h.Summaries.Increments);    // no second summary increment
    }

    [Fact]
    public async Task PrecomputedOutcome_NeverCallsScoreSessionAsyncAgain()
    {
        var h = new Harness();
        h.Pipeline.Respond = (_, _) => throw new InvalidOperationException("must not be called");
        var sut = h.Build();
        var precomputed = Outcome(10);

        await sut.FinalizeAsync(TestTenantId, Sid, FinalizeTrigger.Decide, precomputed, CancellationToken.None);

        Assert.Equal(0, h.Pipeline.Calls);
        Assert.Single(h.Sink.Events);
        Assert.Equal(10, h.Sink.Events[0].Score);
    }

    [Fact]
    public async Task UnknownSession_ReleasesClaim_NoSinkOrSqlOrLabelWrites_NoThrow()
    {
        var h = new Harness();
        h.Pipeline.Respond = (_, _) => null;
        var sut = h.Build();

        await sut.FinalizeAsync(TestTenantId, Sid, FinalizeTrigger.GraceExpired, null, CancellationToken.None);

        Assert.Contains($"t:{Tid}:fin:{Sid}", h.Redis.KeyDeletes); // claim released
        Assert.Empty(h.Sink.Events);
        Assert.Empty(h.Exclusions.Entries);
        Assert.Empty(h.Summaries.Increments);
        Assert.Empty(h.Labels.Labels);
    }

    [Theory]
    [InlineData(10, "allow")]
    [InlineData(50, "challenge")]
    [InlineData(90, "block")]
    public async Task VerdictEvent_StampsScorerVersionFeatureSetVersionAndBand(int score, string expectedBand)
    {
        var h = new Harness();
        h.Redis.SeedClickHash(Sid, new HashEntry("ip", "203.0.113.9"), new HashEntry("site_key", "sk_live"));
        h.Redis.SeedBeaconExists(Sid, true);
        h.Pipeline.Respond = (_, _) => Outcome(score, scorerVersion: "heuristic-1");
        var sut = h.Build();

        await sut.FinalizeAsync(TestTenantId, Sid, FinalizeTrigger.GraceExpired, null, CancellationToken.None);

        var evt = Assert.Single(h.Sink.Events);
        Assert.Equal(EventKind.Verdict, evt.Kind);
        Assert.Equal(score, evt.Score);
        Assert.Equal(expectedBand, evt.Band);
        Assert.Equal("heuristic-1", evt.ScorerVersion);
        Assert.Equal(1, evt.FeatureSetVersion);
        Assert.True(evt.HasJsBeacon);
        Assert.Equal(90, evt.RetentionDays); // no tenant record -> RetentionOptions.DefaultDays
        Assert.Equal("sk_live", evt.SiteKey);
        Assert.Equal("203.0.113.9", evt.Ip);
        Assert.Equal(FixedNow, evt.TimestampUtc);
    }

    [Fact]
    public async Task NoBeaconSession_VerdictEventCarriesHasJsBeaconFalse()
    {
        var h = new Harness();
        h.Redis.SeedClickHash(Sid, new HashEntry("ip", "203.0.113.9"));
        h.Redis.SeedBeaconExists(Sid, false); // no t:{tid}:sess:{sid} key
        h.Pipeline.Respond = (_, _) => Outcome(20);
        var sut = h.Build();

        await sut.FinalizeAsync(TestTenantId, Sid, FinalizeTrigger.GraceExpired, null, CancellationToken.None);

        Assert.False(Assert.Single(h.Sink.Events).HasJsBeacon);
    }

    [Fact]
    public async Task RetentionDays_UsesTenantRecordWhenAvailable()
    {
        var h = new Harness();
        h.Tenants.Record = new TenantRecord(TenantGuid, "Acme", 0, 45, 0, DateTime.UtcNow);
        h.Pipeline.Respond = (_, _) => Outcome(10);
        var sut = h.Build();

        await sut.FinalizeAsync(TestTenantId, Sid, FinalizeTrigger.GraceExpired, null, CancellationToken.None);

        Assert.Equal(45, Assert.Single(h.Sink.Events).RetentionDays);
    }

    [Theory]
    [InlineData((byte)0, "approved")] // AutoEnforce
    [InlineData((byte)1, "pending")]  // ApprovalQueue
    public async Task BlockBand_WithCampaign_EnqueuesExclusion_StatusFromEnforcementMode(
        byte enforcementMode, string expectedStatus)
    {
        var h = new Harness();
        var campaignId = Guid.NewGuid();
        h.Tenants.Record = new TenantRecord(TenantGuid, "Acme", 0, 90, enforcementMode, DateTime.UtcNow);
        h.Campaigns.Campaigns[campaignId] = new CampaignRecord(
            TenantGuid, campaignId, "google", null, "https://a.example.com", null, 0, DateTime.UtcNow);
        h.Redis.SeedClickHash(Sid,
            new HashEntry("ip", "198.51.100.7"), new HashEntry("campaign_id", campaignId.ToString("D")));
        h.Pipeline.Respond = (_, _) => Outcome(87, ["ip_datacenter_asn"]);
        var sut = h.Build();

        await sut.FinalizeAsync(TestTenantId, Sid, FinalizeTrigger.GraceExpired, null, CancellationToken.None);

        var entry = Assert.Single(h.Exclusions.Entries);
        Assert.Equal("google", entry.Platform);
        Assert.Equal("ip", entry.SourceType);
        Assert.Equal("198.51.100.7", entry.Value);
        Assert.Equal(expectedStatus, entry.Status);
        Assert.Equal(campaignId, entry.CampaignScope);
        Assert.Contains("score=87", entry.Reason);
        Assert.Contains("ip_datacenter_asn", entry.Reason);
    }

    [Fact]
    public async Task BlockBand_CampaignLess_PlatformIsOther()
    {
        var h = new Harness();
        h.Tenants.Record = new TenantRecord(TenantGuid, "Acme", 0, 90, 0, DateTime.UtcNow);
        h.Redis.SeedClickHash(Sid, new HashEntry("ip", "198.51.100.7")); // no campaign_id — pixel/organic
        h.Pipeline.Respond = (_, _) => Outcome(95, ["honeypot_touched"]);
        var sut = h.Build();

        await sut.FinalizeAsync(TestTenantId, Sid, FinalizeTrigger.GraceExpired, null, CancellationToken.None);

        var entry = Assert.Single(h.Exclusions.Entries);
        Assert.Equal("other", entry.Platform);
        Assert.Null(entry.CampaignScope);
    }

    [Fact]
    public async Task BlockBand_MissingIp_SkipsExclusionEnqueue_NoThrow()
    {
        var h = new Harness();
        h.Tenants.Record = new TenantRecord(TenantGuid, "Acme", 0, 90, 0, DateTime.UtcNow);
        // No click hash at all -> no ip.
        h.Pipeline.Respond = (_, _) => Outcome(90, ["honeypot_touched"]);
        var sut = h.Build();

        await sut.FinalizeAsync(TestTenantId, Sid, FinalizeTrigger.GraceExpired, null, CancellationToken.None);

        Assert.Empty(h.Exclusions.Entries);
        Assert.Single(h.Sink.Events); // verdict event still ships
    }

    [Theory]
    [InlineData(10, 1, 0, 0)]  // allow
    [InlineData(50, 0, 1, 0)]  // challenge
    [InlineData(90, 0, 0, 1)]  // block
    public async Task SummaryIncrement_CalledExactlyOnce_CountsMatchBand(
        int score, int allowed, int challenged, int blocked)
    {
        var h = new Harness();
        h.Redis.SeedClickHash(Sid, new HashEntry("ip", "203.0.113.9"));
        h.Pipeline.Respond = (_, _) => Outcome(score);
        var sut = h.Build();

        await sut.FinalizeAsync(TestTenantId, Sid, FinalizeTrigger.GraceExpired, null, CancellationToken.None);

        var delta = Assert.Single(h.Summaries.Increments);
        Assert.Equal(TenantGuid, delta.TenantId);
        Assert.Equal(Guid.Empty, delta.CampaignId); // no campaign_id in the click hash
        Assert.Equal(allowed, delta.Allowed);
        Assert.Equal(challenged, delta.Challenged);
        Assert.Equal(blocked, delta.Blocked);
        Assert.Equal(score, delta.ScoreSum);
        Assert.Equal(1, delta.Events);

        // REQ-01: the live-path histogram delta is exactly ScoreHistogramMath's
        // single-score contribution — one 1 in the score's own bucket.
        Assert.Equal(ScoreHistogramMath.SingleScore(score), delta.ScoreHistogram);
        Assert.Equal(1, delta.ScoreHistogram.Total);
    }

    [Fact]
    public async Task SummaryIncrement_UsesCampaignIdFromClickContext_WhenPresent()
    {
        var h = new Harness();
        var campaignId = Guid.NewGuid();
        h.Redis.SeedClickHash(Sid, new HashEntry("campaign_id", campaignId.ToString("D")));
        h.Pipeline.Respond = (_, _) => Outcome(10);
        var sut = h.Build();

        await sut.FinalizeAsync(TestTenantId, Sid, FinalizeTrigger.GraceExpired, null, CancellationToken.None);

        Assert.Equal(campaignId, Assert.Single(h.Summaries.Increments).CampaignId);
    }

    [Fact]
    public async Task T1RuleHit_WritesExactlyOneWeakFraudLabel()
    {
        var h = new Harness();
        h.Pipeline.Respond = (_, _) => Outcome(85, ["honeypot_touched"]);
        var sut = h.Build();

        await sut.FinalizeAsync(TestTenantId, Sid, FinalizeTrigger.GraceExpired, null, CancellationToken.None);

        var label = Assert.Single(h.Labels.Labels);
        Assert.Equal(TestTenantId, label.TenantId);
        Assert.Equal(Sid, label.SessionId);
        Assert.Equal(LabelValues.Fraud, label.Label);
        Assert.Equal(LabelSources.T1Rule, label.LabelSource);
    }

    [Fact]
    public async Task EmptyRuleHits_WritesNoLabel()
    {
        var h = new Harness();
        h.Pipeline.Respond = (_, _) => Outcome(10, Array.Empty<string>());
        var sut = h.Build();

        await sut.FinalizeAsync(TestTenantId, Sid, FinalizeTrigger.GraceExpired, null, CancellationToken.None);

        Assert.Empty(h.Labels.Labels);
    }

    [Fact]
    public async Task WhitelistShortCircuit_NoLabel_NoExclusion_ButVerdictEventStillShips()
    {
        var h = new Harness();
        h.Redis.SeedClickHash(Sid, new HashEntry("ip", "203.0.113.9"));
        h.Pipeline.Respond = (_, _) =>
            Outcome(0, ["whitelisted"], whitelisted: true, scorerVersion: "whitelist-short-circuit");
        var sut = h.Build();

        await sut.FinalizeAsync(TestTenantId, Sid, FinalizeTrigger.GraceExpired, null, CancellationToken.None);

        Assert.Empty(h.Labels.Labels);
        Assert.Empty(h.Exclusions.Entries);
        var evt = Assert.Single(h.Sink.Events);
        Assert.Equal("whitelist-short-circuit", evt.ScorerVersion);
        Assert.Equal(VerdictBands.Allow, evt.Band);
    }

    [Fact]
    public async Task BeltAndBraces_WhitelistedHitWithoutWhitelistedFlag_StillSkipsLabel()
    {
        // Defensive guard (D19): even if Whitelisted somehow reads false, a
        // "whitelisted" rule hit must never be labeled fraud.
        var h = new Harness();
        h.Pipeline.Respond = (_, _) => Outcome(0, ["whitelisted"], whitelisted: false);
        var sut = h.Build();

        await sut.FinalizeAsync(TestTenantId, Sid, FinalizeTrigger.GraceExpired, null, CancellationToken.None);

        Assert.Empty(h.Labels.Labels);
    }

    [Fact]
    public async Task SinkFailure_DoesNotPreventSummaryIncrement()
    {
        var h = new Harness();
        h.Sink.ThrowOnCall = new InvalidOperationException("sink boom");
        h.Pipeline.Respond = (_, _) => Outcome(10);
        var sut = h.Build();

        await sut.FinalizeAsync(TestTenantId, Sid, FinalizeTrigger.GraceExpired, null, CancellationToken.None); // no throw

        Assert.Single(h.Summaries.Increments);
    }

    [Fact]
    public async Task SummaryFailure_DoesNotPreventSinkWrite()
    {
        var h = new Harness();
        h.Summaries.ThrowOnIncrement = new InvalidOperationException("summary boom");
        h.Pipeline.Respond = (_, _) => Outcome(10);
        var sut = h.Build();

        await sut.FinalizeAsync(TestTenantId, Sid, FinalizeTrigger.GraceExpired, null, CancellationToken.None); // no throw

        Assert.Single(h.Sink.Events);
    }

    [Fact]
    public async Task ExclusionFailure_DoesNotPreventLabelWrite()
    {
        var h = new Harness();
        h.Tenants.Record = new TenantRecord(TenantGuid, "Acme", 0, 90, 0, DateTime.UtcNow);
        h.Redis.SeedClickHash(Sid, new HashEntry("ip", "203.0.113.9"));
        h.Exclusions.ThrowOnCall = new InvalidOperationException("exclusion boom");
        h.Pipeline.Respond = (_, _) => Outcome(90, ["honeypot_touched"]);
        var sut = h.Build();

        await sut.FinalizeAsync(TestTenantId, Sid, FinalizeTrigger.GraceExpired, null, CancellationToken.None); // no throw

        Assert.Single(h.Labels.Labels);
    }

    [Fact]
    public async Task LabelFailure_DoesNotThrow_AndOtherStepsAlreadyCompleted()
    {
        var h = new Harness();
        h.Labels.ThrowOnCall = new InvalidOperationException("label boom");
        h.Pipeline.Respond = (_, _) => Outcome(85, ["honeypot_touched"]);
        var sut = h.Build();

        await sut.FinalizeAsync(TestTenantId, Sid, FinalizeTrigger.GraceExpired, null, CancellationToken.None); // no throw

        Assert.Single(h.Sink.Events);
        Assert.Single(h.Summaries.Increments);
    }

    [Fact]
    public async Task ClickLessBeaconOnlySession_FallsBackToAmbientTenantSiteKey()
    {
        var h = new Harness { SiteKey = "sk_from_ambient_context" };
        h.Redis.SeedBeaconExists(Sid, true); // sess hash exists, no click hash at all
        h.Pipeline.Respond = (_, _) => Outcome(10);
        var sut = h.Build();

        await sut.FinalizeAsync(TestTenantId, Sid, FinalizeTrigger.Decide, null, CancellationToken.None);

        Assert.Equal("sk_from_ambient_context", Assert.Single(h.Sink.Events).SiteKey);
    }

    [Fact]
    public async Task GraceEntry_IsRemovedRegardlessOfTrigger()
    {
        var h = new Harness();
        h.Pipeline.Respond = (_, _) => Outcome(10);
        var sut = h.Build();

        await sut.FinalizeAsync(TestTenantId, Sid, FinalizeTrigger.GraceExpired, null, CancellationToken.None);

        var zrem = Assert.Single(h.Redis.SortedSetRemoves);
        Assert.Equal($"t:{Tid}:grace", zrem.Key);
        Assert.Equal(Sid, zrem.Member);
    }
}
