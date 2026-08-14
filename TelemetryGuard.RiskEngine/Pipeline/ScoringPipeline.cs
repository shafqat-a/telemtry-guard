using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TelemetryGuard.RiskEngine.Contracts;
using TelemetryGuard.RiskEngine.Enrichment;
using TelemetryGuard.RiskEngine.Features;
using TelemetryGuard.RiskEngine.Rules;
using TelemetryGuard.RiskEngine.Scoring;
using TelemetryGuard.RiskEngine.Velocity;

namespace TelemetryGuard.RiskEngine.Pipeline;

/// <summary>
/// The scoring orchestrator (RSK-07). Order of operations:
/// 1. session state batch read (one Redis RTT);
/// 2. whitelist short-circuit (one Redis RTT; hit → forced allow, FLAGGED Whitelisted=true,
///    extraction/rules/scoring never run — D19's override loop must never masquerade as an
///    organic allow);
/// 3. parallel prefetch: velocity batch (one Redis RTT) + memory-cached campaign lookup,
///    with enrichment running synchronously in-process meanwhile;
/// 4. pure compute: Extract → Evaluate → Score;
/// 5. finalScore = Math.Max(scorerScore, ruleFloor ?? 0) — the ONLY combination point;
///    rules only RAISE (§6.3), the scorer's version/feature-set stamps survive (D18).
/// Budget is law (D3/§2): the non-whitelisted path awaits exactly TWO Redis round trips
/// plus one optional cached campaign lookup; nothing else may await network. Degraded
/// inputs pass through untouched — RSK-04 owns all NaN/null mapping.
/// Register scoped (tenant context is ambient and scoped, D11).
/// </summary>
public sealed class ScoringPipeline : IScoringPipeline
{
    private const string WhitelistScorerVersion = "whitelist-short-circuit";
    private static readonly string[] WhitelistedHit = ["whitelisted"];
    private static readonly VelocitySnapshot ColdVelocity = new(0, 0, 0, 0, 0);

    private readonly ISessionStateStore _sessions;
    private readonly IWhitelistCheck _whitelist;
    private readonly IIpEnrichmentService _enrichment;
    private readonly IVelocityStore _velocity;
    private readonly IFeatureExtractor _extractor;
    private readonly IT1RuleEngine _rules;
    private readonly IScorer _scorer;
    private readonly ICampaignContextProvider _campaigns;
    private readonly ILogger<ScoringPipeline> _logger;
    private readonly IShadowScorer? _shadowScorer;

    public ScoringPipeline(
        ISessionStateStore sessions,
        IWhitelistCheck whitelist,
        IIpEnrichmentService enrichment,
        IVelocityStore velocity,
        IFeatureExtractor extractor,
        IT1RuleEngine rules,
        IScorer scorer,
        ICampaignContextProvider campaigns,
        ILogger<ScoringPipeline> logger,
        IShadowScorer? shadowScorer = null)
    {
        _sessions = sessions;
        _whitelist = whitelist;
        _enrichment = enrichment;
        _velocity = velocity;
        _extractor = extractor;
        _rules = rules;
        _scorer = scorer;
        _campaigns = campaigns;
        _logger = logger;
        _shadowScorer = shadowScorer;
    }

    public async Task<ScoringOutcome?> ScoreSessionAsync(
        string sessionId,
        ChallengeOutcome outcome = ChallengeOutcome.NotChallenged,
        CancellationToken ct = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();

        // 1. Session state (one batched Redis RTT). Unknown session → null, no throw.
        var state = await _sessions.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (state is null)
        {
            RiskMetrics.RecordScoringDuration(ElapsedMs(startTimestamp), "not_found", whitelisted: false);
            _logger.LogDebug("Session {SessionId} unknown (no click, no beacon)", sessionId);
            return null;
        }

        // 2. Beacon-only sessions have no click hash and API-04's aggregate hash stores
        //    no request info: ip may be null → "" for RawSessionData.Ip; enrichment of ""
        //    yields the all-null Empty enrichment — degraded-not-zero per RSK-04.
        //    (If API-04 later adds an ip field to the session hash, map it here.)
        var ip = state.Click?.Ip ?? string.Empty;
        var visitorId = state.Beacon?.VisitorId;

        // 3. Whitelist short-circuit (D19): forced allow, explicitly flagged; NOTHING
        //    else runs — no velocity read, no extraction, no rules, no scorer.
        if (await _whitelist.IsWhitelistedAsync(ip, visitorId, ct).ConfigureAwait(false))
        {
            var wlElapsed = ElapsedMs(startTimestamp);
            RiskMetrics.RecordScoringDuration(wlElapsed, BandTag(VerdictBand.Allow), whitelisted: true);
            return new ScoringOutcome(
                new ScoreResult(0, WhitelistedHit, WhitelistScorerVersion, FraudFeatureVector.FeatureSetVersion),
                VerdictBand.Allow,
                Whitelisted: true,
                wlElapsed);
        }

        // 4. Parallel prefetch: velocity batch (single Redis RTT, RSK-03) + 60 s
        //    memory-cached campaign lookup, while enrichment runs synchronously
        //    in-process (RSK-02, decorated with a 5-minute cache).
        //    RSK-03 rejects an empty ip by contract, so the no-click path substitutes
        //    cold counters (velocity is legitimately 0 when cold — never NaN).
        var velocityTask = ip.Length == 0
            ? Task.FromResult(ColdVelocity)
            : _velocity.ReadAsync(ip, visitorId, ct);
        var campaignTask = _campaigns.GetAsync(state.CampaignId, ct);
        var enrichment = _enrichment.Enrich(ip);
        await Task.WhenAll(velocityTask, campaignTask).ConfigureAwait(false);

        // 5. Assemble the pre-fetched input. ChallengeOutcome comes from the PARAMETER —
        //    session state stores no challenge outcome. Beacon possibly null: that IS the
        //    non-JS path (§6.1) and must score normally on HTTP + velocity alone.
        var click = state.Click;
        var raw = new RawSessionData
        {
            SessionId = sessionId,
            Ip = ip,
            UserAgent = click?.UserAgent,
            Headers = click?.Headers ?? EmptyHeaders,
            TlsFingerprint = click?.TlsFingerprint,
            IsPaidClick = click?.IsPaidClick ?? false,
            ClickId = click?.ClickId,
            ClickIdFresh = click?.ClickIdFresh,
            Beacon = state.Beacon,
            Enrichment = enrichment,
            Velocity = velocityTask.Result,
            Campaign = campaignTask.Result,
            ChallengeOutcome = outcome,
        };

        // 6–8. Pure, synchronous compute.
        var vector = _extractor.Extract(raw);
        var t1 = _rules.Evaluate(in vector);
        var scored = _scorer.Score(in vector);

        // 8½. RSK-08 (D18 listen-only): an optional secondary model run purely for
        //     shadow logging. Runs on the SAME vector, never on the whitelist path
        //     (we are already past that branch here). A throw is logged and the
        //     shadow fields simply stay null — it can NEVER affect the verdict below.
        int? shadowScore = null;
        string? shadowScorerVersion = null;
        if (_shadowScorer is not null)
        {
            try
            {
                var shadow = _shadowScorer.Score(in vector);
                shadowScore = shadow.Score;
                shadowScorerVersion = shadow.ScorerVersion;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Shadow scorer failed for session {SessionId}; verdict unaffected (D18 listen-only).",
                    sessionId);
            }
        }

        // 9. The single combination point: rules only RAISE (§6.3). The scorer's
        //    version stamp survives — the floor never overwrites it (D18).
        var finalScore = Math.Max(scored.Score, t1.Floor ?? 0);
        var result = new ScoreResult(finalScore, t1.Hits, scored.ScorerVersion, scored.FeatureSetVersion);

        // 10–11. Band + metric.
        var band = BandMapper.ToBand(finalScore);
        var elapsed = ElapsedMs(startTimestamp);
        RiskMetrics.RecordScoringDuration(elapsed, BandTag(band), whitelisted: false);
        return new ScoringOutcome(result, band, Whitelisted: false, elapsed, vector, shadowScore, shadowScorerVersion);
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static double ElapsedMs(long startTimestamp)
        => Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

    private static string BandTag(VerdictBand band) => band switch
    {
        VerdictBand.Allow => "allow",
        VerdictBand.Challenge => "challenge",
        VerdictBand.Block => "block",
        _ => "block", // unreachable; BandMapper only yields the three bands
    };
}
