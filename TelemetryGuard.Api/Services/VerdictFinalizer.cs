namespace TelemetryGuard.Api.Services;

using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Api.Options;
using TelemetryGuard.Core.Analytics;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Core.Time;
using TelemetryGuard.Data.Models;
using TelemetryGuard.Data.Repositories;
using TelemetryGuard.RiskEngine.Contracts;
using TelemetryGuard.RiskEngine.Pipeline;

/// <summary>
/// API-06: the single shared finalization path for every session — called by
/// /decide (API-05, with a precomputed <see cref="ScoringOutcome"/>) and the
/// grace-period worker (<see cref="VerdictFinalizerService"/>, precomputed =
/// null). Scores the session (unless precomputed), persists to BOTH stores
/// (ClickHouse verdict event via IEventSink + the SQL daily-summary increment),
/// enqueues block-band sources into the exclusion queue with a status derived
/// from the tenant's EnforcementMode (D21), and writes weak-positive T1-rule
/// training labels via ILabelSink (D18), guarded against whitelist pollution (D19).
///
/// Idempotency: a session may be finalized by /decide and claimed by the grace
/// worker in the same second. Guard: SETNX t:{tid}:fin:{sid} with a 1 h expiry;
/// the losing caller is a no-op. SINGLE-INSTANCE MVP: this SETNX makes
/// double-finalization harmless even though only one API process runs the grace
/// worker; multi-instance claiming (an atomic Lua pop) is explicitly deferred.
///
/// Tenancy: <paramref name="tenantId"/> arrives as an explicit parameter, but the
/// scoped repositories this class calls (ITenantRepository, ICampaignRepository,
/// IVerdictSummaryRepository, IExclusionQueueRepository) all read the AMBIENT
/// ITenantContext internally — the caller must stamp that scope to the same
/// tenant before invoking FinalizeAsync (API-05's per-request scope and the
/// grace worker's manual scope both do this).
///
/// Must be resolved from a DI scope (its dependencies are scoped/tenant-bound).
/// </summary>
public sealed class VerdictFinalizer(
    IScoringPipeline pipeline,
    IEventSink sink,
    IExclusionQueueRepository exclusions,
    IVerdictSummaryRepository summaries,
    ILabelSink labelSink,
    ITenantRepository tenants,
    ICampaignRepository campaigns,
    IConnectionMultiplexer redis,
    IClock clock,
    IMemoryCache cache,
    ITenantContext tenant,
    IOptions<ScoringBandOptions> bandOptions,
    IOptions<RetentionOptions> retentionOptions,
    ILogger<VerdictFinalizer> logger) : IVerdictFinalizer
{
    public async Task FinalizeAsync(
        TenantId tenantId, string sessionId, FinalizeTrigger trigger,
        ScoringOutcome? precomputed, CancellationToken ct)
    {
        var tid = tenantId.Value.ToString("D");
        var db = redis.GetDatabase();
        var finKey = $"t:{tid}:fin:{sessionId}";

        // Idempotency claim: the loser of a /decide-vs-grace-worker race is a no-op.
        var claimed = await db.StringSetAsync(finKey, "1", TimeSpan.FromHours(1), When.NotExists)
            .ConfigureAwait(false);
        if (!claimed)
        {
            logger.LogDebug(
                "VerdictFinalizer: session {SessionId} tenant {TenantId} already finalized; " +
                "trigger {Trigger} short-circuits.", sessionId, tenantId, trigger);
            return;
        }

        // Idempotent cleanup regardless of trigger: a /decide finalize must clear
        // the grace entry too so the worker never double-processes it.
        await db.SortedSetRemoveAsync($"t:{tid}:grace", sessionId).ConfigureAwait(false);

        var outcome = precomputed
            ?? await pipeline.ScoreSessionAsync(sessionId, ChallengeOutcome.NotChallenged, ct).ConfigureAwait(false);
        if (outcome is null)
        {
            // Unknown session (no click, no beacon ever seen) — release the claim so
            // a later-arriving beacon can still be finalized.
            logger.LogWarning(
                "VerdictFinalizer: unknown session {SessionId} tenant {TenantId} (trigger {Trigger}); " +
                "releasing the finalize claim.", sessionId, tenantId, trigger);
            await db.KeyDeleteAsync(finKey).ConfigureAwait(false);
            return;
        }

        var result = outcome.Result;
        var band = MapBand(result.Score, bandOptions.Value);

        // Click context (site key / ip / campaign) + beacon presence, one Redis batch.
        var clickKey = $"t:{tid}:click:{sessionId}";
        var sessKey = $"t:{tid}:sess:{sessionId}";
        var batch = db.CreateBatch();
        var clickHashTask = batch.HashGetAllAsync(clickKey);
        var beaconExistsTask = batch.KeyExistsAsync(sessKey);
        batch.Execute();
        await Task.WhenAll(clickHashTask, beaconExistsTask).ConfigureAwait(false);

        var clickFields = clickHashTask.Result.ToDictionary(
            e => e.Name.ToString(), e => e.Value.ToString(), StringComparer.Ordinal);
        var hasJsBeacon = beaconExistsTask.Result;

        var ip = clickFields.TryGetValue("ip", out var ipVal) && ipVal.Length > 0 ? ipVal : null;
        var campaignIdStr = clickFields.TryGetValue("campaign_id", out var cidVal) && cidVal.Length > 0 ? cidVal : null;
        var campaignId = Guid.TryParse(campaignIdStr, out var parsedCampaignId) ? parsedCampaignId : (Guid?)null;
        var clickSiteKey = clickFields.TryGetValue("site_key", out var skVal) && skVal.Length > 0 ? skVal : null;
        // Click-less sessions (pure SDK beacon, no tracker/pixel hit) have no site_key
        // in Redis: fall back to the ambient tenant context, which ingest endpoints
        // (and API-05's /decide) resolve with the site key in the same DI scope.
        var siteKey = clickSiteKey ?? (tenant.IsResolved ? tenant.SiteKey : null) ?? "";

        // Tenant config (RetentionDays + EnforcementMode), 60 s cached under the SAME
        // key/TTL the ingest endpoints use, so the cache entry is shared, not duplicated.
        TenantRecord? tenantRecord = null;
        try
        {
            tenantRecord = await cache.GetOrCreateAsync($"tenantcfg:{tid}", entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
                return tenants.GetCurrentAsync(ct);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "VerdictFinalizer: tenant config read failed for tenant {TenantId}; using defaults " +
                "(retention={DefaultDays}d, EnforcementMode=AutoEnforce).",
                tenantId, retentionOptions.Value.DefaultDays);
        }
        var retentionDays = (ushort)(tenantRecord?.RetentionDays ?? retentionOptions.Value.DefaultDays);

        // ---- Step 5: verdict event (D18/D20 stamps). Sink call is an enqueue only —
        // never await ClickHouse round trips on the finalize path (spec §4).
        // RSK-08: `features` is the JSON-serialized FraudFeatureVector captured at
        // scoring time (null/"" on the whitelist short-circuit — extraction never ran)
        // so the offline trainer can rebuild MlFeatureRow later; shadow_score/
        // shadow_scorer_version are D18 listen-only fields, populated only when a
        // shadow scorer ran (never on whitelisted sessions). ----
        try
        {
            var featuresJson = outcome.Features is { } vector
                ? JsonSerializer.Serialize(vector, FraudFeatureVectorJson.Options)
                : "";
            var evt = new ClickEvent
            {
                TenantId = tenantId,
                SiteKey = siteKey,
                SessionId = sessionId,
                Kind = EventKind.Verdict,
                CampaignId = campaignIdStr ?? "",
                Ip = ip ?? "",
                HasJsBeacon = hasJsBeacon,
                Score = result.Score,
                Band = BandWire(band),
                RuleHits = result.RuleHits,
                ScorerVersion = result.ScorerVersion,
                FeatureSetVersion = result.FeatureSetVersion,
                RetentionDays = retentionDays,
                TimestampUtc = clock.UtcNow.UtcDateTime,
                Features = featuresJson,
                ShadowScore = outcome.ShadowScore,
                ShadowScorerVersion = outcome.ShadowScorerVersion,
            };
            await sink.WriteBatchAsync(new[] { evt }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "VerdictFinalizer: verdict event write failed for session {SessionId} tenant {TenantId}",
                sessionId, tenantId);
        }

        // ---- Step 6: block band -> exclusion queue (D21 EnforcementMode). ----
        if (band == VerdictBand.Block)
        {
            try
            {
                if (string.IsNullOrEmpty(ip))
                {
                    logger.LogWarning(
                        "VerdictFinalizer: block-band session {SessionId} tenant {TenantId} has no " +
                        "click-context IP; skipping exclusion enqueue.", sessionId, tenantId);
                }
                else
                {
                    var platform = "other"; // campaign-less (pixel/organic) default — never pushed by INT-03/INT-04
                    if (campaignId is { } cid)
                    {
                        var campaignRecord = await cache.GetOrCreateAsync($"campaignrec:{tid}:{cid:D}", entry =>
                        {
                            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
                            return campaigns.GetAsync(cid, ct);
                        }).ConfigureAwait(false);
                        if (campaignRecord is not null) platform = campaignRecord.Platform;
                    }

                    // Approval flow implemented by INT-02: /admin/enforcement endpoints
                    // transition pending->approved|rejected. AutoEnforce (default)
                    // enqueues straight to 'approved' for INT-03/INT-04 sync pickup.
                    var status = tenantRecord?.EnforcementMode == 1
                        ? ExclusionStatuses.Pending
                        : ExclusionStatuses.Approved;
                    await exclusions.EnqueueAsync(new ExclusionQueueInsert(
                        platform, "ip", ip,
                        $"score={result.Score} rules={string.Join('|', result.RuleHits)}",
                        status, campaignId), ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "VerdictFinalizer: exclusion enqueue failed for session {SessionId} tenant {TenantId}",
                    sessionId, tenantId);
            }
        }

        // ---- Step 7: live daily-summary increment — NOT the absolute rollup upsert. ----
        try
        {
            await summaries.IncrementDailySummaryAsync(new VerdictDailySummaryRow(
                tenantId.Value,
                campaignId ?? Guid.Empty,
                DateOnly.FromDateTime(clock.UtcNow.UtcDateTime),
                Allowed: band == VerdictBand.Allow ? 1 : 0,
                Challenged: band == VerdictBand.Challenge ? 1 : 0,
                Blocked: band == VerdictBand.Block ? 1 : 0,
                ScoreSum: result.Score,
                Events: 1,
                // REQ-01: a single verdict's histogram delta is always exactly one 1
                // in its own bucket — same "1 event" shape as Events: 1 above.
                ScoreHistogram: ScoreHistogramMath.SingleScore(result.Score)), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "VerdictFinalizer: summary increment failed for session {SessionId} tenant {TenantId}",
                sessionId, tenantId);
        }

        // ---- Step 8: weak T1-rule labels (D18), guarded against whitelist pollution (D19). ----
        try
        {
            if (!outcome.Whitelisted && result.RuleHits.Count > 0
                && !result.RuleHits.Contains("whitelisted"))
            {
                await labelSink.WriteAsync(new LabelEvent(
                    tenantId, sessionId, LabelValues.Fraud, LabelSources.T1Rule, clock.UtcNow.UtcDateTime),
                    ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "VerdictFinalizer: label write failed for session {SessionId} tenant {TenantId}",
                sessionId, tenantId);
        }
    }

    // Locally-mapped band from CONFIGURED thresholds (Scoring:Bands) — never the
    // hardcoded BandMapper constants baked into RSK-07's outcome.Band (same
    // rationale as API-05's /decide: a tenant/test override of the thresholds
    // must still drive enforcement/summary decisions here).
    private static VerdictBand MapBand(int score, ScoringBandOptions bands)
        => score <= bands.AllowMax ? VerdictBand.Allow
         : score <= bands.ChallengeMax ? VerdictBand.Challenge
         : VerdictBand.Block;

    private static string BandWire(VerdictBand band) => band switch
    {
        VerdictBand.Allow => VerdictBands.Allow,
        VerdictBand.Challenge => VerdictBands.Challenge,
        VerdictBand.Block => VerdictBands.Block,
        _ => VerdictBands.Block, // unreachable; MapBand only yields the three bands
    };
}
