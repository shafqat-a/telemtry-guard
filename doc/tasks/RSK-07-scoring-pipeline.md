---
id: RSK-07
title: "Scoring pipeline within the 50ms budget"
phase: 1
workstream: risk
depends_on: [RSK-04, RSK-05, RSK-06, DAT-07]
size: M
spec_refs: [D3, D19, "§2", "§6"]
detail_level: full
---

# RSK-07: Scoring pipeline within the 50ms budget

## Objective

Implement `IScoringPipeline.ScoreSessionAsync(sessionId, challengeOutcome)` in `TelemetryGuard.RiskEngine`: the orchestrator that turns a session id into a final `ScoringOutcome`. Order of operations: whitelist short-circuit (Redis set maintained by DAT-07 → forced allow, flagged whitelisted) → parallel prefetch of everything extraction needs (session state, velocity snapshot, memory-cached IP enrichment) → `IFeatureExtractor.Extract` → `IT1RuleEngine.Evaluate` → `IScorer.Score` → combine floor via `Math.Max` → `ScoreResult` + `VerdictBand`. Also defines the READ-ONLY `ISessionStateStore` (a typed reader over the Redis state that the ingest endpoints write: API-02/API-03's `t:{tid}:click:{sid}` flat hash and API-04's `t:{tid}:sess:{sid}` aggregate hash) and the `IWhitelistCheck` port. Duration is measured with a `Stopwatch` into an OpenTelemetry-compatible histogram, and an env-gated perf test asserts p99 < 50 ms over 1000 local iterations.

## Spec context (self-contained)

- §2 goal 2 / D3: score **in the request path in < 50 ms**; the scorer runs in-process (no network hop for scoring); the only network I/O allowed is the pre-fetch (Redis batch + nothing else — enrichment is in-process memory-mapped, RSK-02).
- §6.1 flow: tracker hit (/c) + SDK beacons (/i) are joined **by session ID**; if no beacon arrived within the grace period (~10 s) the session is scored on HTTP + velocity features alone (`has_js_beacon=0` — this pipeline must score beacon-less sessions normally, not fail).
- §6.3 bands: 0–30 allow / 31–70 challenge / 71–100 block; T1 rules only RAISE (floor folded with `Math.Max`); rules never lower a score.
- D19: tenant overrides whitelist a source; whitelisted sources must be force-allowed. The whitelist lives in SQL (DAT-07) and is mirrored into Redis sets for hot-path membership checks. A whitelisted verdict must be visibly FLAGGED as whitelisted (it must not pollute training data as an organic "allow": RSK-08 excludes whitelisted verdicts from label building).
- D18: every verdict is stamped with `scorer_version` and `feature_set_version`.
- Tenancy: all Redis keys are `t:{tenantId}:…` from ambient `ITenantContext` (D11) — session state and whitelist keys included.

## Prerequisites

- RSK-04: `IFeatureExtractor`, `RawSessionData`, `BeaconData`, `CampaignContext` (namespace `TelemetryGuard.RiskEngine.Features`).
- RSK-05: `IT1RuleEngine`, `T1Result` (namespace `TelemetryGuard.RiskEngine.Rules`).
- RSK-06: `IScorer` registration (`HeuristicScorer`, "heuristic-1").
- RSK-02: `IIpEnrichmentService` (synchronous, in-process). RSK-03: `IVelocityStore.ReadAsync` (single-RTT batch), `IConnectionMultiplexer` registered, `ITenantContext`/`IClock` from FND-04.
- DAT-07 (in depends_on): maintains the Redis whitelist mirror sets and DEFINES the binding cache contract this task reads: key `t:{tenantId:D}:wl:{sourceType}` (lower-case dashed GUID; sourceType ∈ `ip`, `device_id`, `fingerprint`), a Redis SET of whitelisted values with a 1-hour TTL. A missing key means "unknown": treat as not whitelisted for the current request and schedule `IWhitelistRepository.RebuildCacheAsync` OFF the request path (step 3) — never a SQL fallback on the hot path.
- API-02/API-03 (runtime producers, not deps): flat click-context hash `t:{tid}:click:{sid}` (fields per API-02 step 3.7). API-04 (runtime producer): flat aggregate session hash `t:{tid}:sess:{sid}` (fields per API-04 step 6; running aggregates ONLY — raw event points are never stored). This task only READS both.

## Implementation steps

1. Create `TelemetryGuard.RiskEngine/Pipeline/ISessionStateStore.cs` — a READ-ONLY view over the ingest-written Redis state. The producers own the keys, formats and TTLs: API-02/API-03 write the flat click-context hash `t:{tid}:click:{sid}` and API-04 writes the flat aggregate session hash `t:{tid}:sess:{sid}`. This task ships NO write path — there are no Save/Append/Set methods, because no component writes session state through this interface (the ingest endpoints write their own contracts, and API-05 passes the Turnstile outcome as a `ScoreSessionAsync` parameter, step 5):

```csharp
using TelemetryGuard.RiskEngine.Features;

namespace TelemetryGuard.RiskEngine.Pipeline;

/// <summary>HTTP-layer facts captured at click time, parsed from the flat hash
/// t:{tid}:click:{sid} written by API-02/API-03 (fields: kind, ts, ip, ua, ch_ua,
/// ch_mobile, ch_platform, accept_language, referrer, header_order, site_key,
/// campaign_id, click_id_type, click_id, click_id_invalid; empty string = absent).</summary>
public sealed record ClickState(
    string Ip, string? UserAgent, IReadOnlyDictionary<string, string> Headers,
    string? ClickIdType, string? ClickId, bool? ClickIdFresh, bool IsPaidClick,
    string? TlsFingerprint, DateTimeOffset Timestamp);

/// <summary>Joined per-session read model. Beacon is RSK-04's aggregate-carrying
/// BeaconData mapped from API-04's session hash; null when no beacon arrived.</summary>
public sealed record SessionState(ClickState? Click, BeaconData? Beacon, string? CampaignId);

public interface ISessionStateStore
{
    /// <summary>Batched read of both hashes; null when neither exists (unknown session).</summary>
    Task<SessionState?> GetAsync(string sessionId, CancellationToken ct);
}
```

2. Create `RedisSessionStateStore` (`TelemetryGuard.RiskEngine/Pipeline/RedisSessionStateStore.cs`), scoped, ctor `(IConnectionMultiplexer mux, ITenantContext tenant)` — a pure READER (this class never writes; producer TTLs govern expiry):
   - `GetAsync`: one `IBatch` issuing `HGETALL t:{tid}:click:{sessionId}` + `HGETALL t:{tid}:sess:{sessionId}`; both empty → return null.
   - Click-hash mapping → `ClickState` (API-02 step 3.7 contract): `ip`/`ua`/client-hint/`accept_language`/`referrer`/`header_order` fields verbatim (empty string → null); `IsPaidClick = kind == "tracker"`; `ClickIdFresh` derived: no `click_id` → null; `click_id` present and `click_id_invalid=0` → true (fresh); `click_id` present and `click_id_invalid=1` → false (replayed). `CampaignId` from `campaign_id`.
   - Session-hash mapping → RSK-04's aggregate `BeaconData` (field-for-field from API-04 step 6): `mm_n→MmN`, `mm_mean_ms→MmMeanMs`, `mm_m2→MmM2`, `mm_path_len→MmPathLen`, `mm_first_x/mm_first_y→FirstX/FirstY`, `mm_prev_x/mm_prev_y→PrevX/PrevY`, `n_click/n_key/n_scroll/n_touch→ClickCount/KeyCount/ScrollEventCount/TouchCount`, `first_interaction_delay_ms→FirstInteractionDelayMs` (absent → null), `visitor_id→VisitorId`, `webdriver→WebdriverFlag`, `headless→HeadlessBrowser`, `hp_touched→HoneypotTouched`, `pointer_untrusted→PointerUntrusted`, `click_before_render→ClickBeforeRender`, `IntegrityOk = (integrity_fails absent or == 0)`, `screen_w/screen_h/dpr→Screen*/DevicePixelRatio`, `cookies_enabled/canvas_blocked`, `tz→Timezone`, `lang→Language`, `storage_age_sec→StorageAgeSec` (field absent when the HMAC did not verify — leave null, missing ≠ zero), `form_*` fields, `SessionDurationMs = last_beacon_ts − nav_ts` (when both present, else 0). Absent numeric fields map to the `BeaconData` member's NaN/null default — never 0.
   - The store does NOT finish any statistics: std/linearity/count math from the aggregates belongs to RSK-04's `TrajectoryStats` (population `std = sqrt(mm_m2 / mm_n)`, `linearity = Dist(first, prev) / mm_path_len` — see RSK-04 step 5).
   - Numeric parsing with `CultureInfo.InvariantCulture` (API-04 writes invariant-culture strings).

3. Create `TelemetryGuard.RiskEngine/Pipeline/IWhitelistCheck.cs` + `RedisWhitelistCheck`:

```csharp
public interface IWhitelistCheck
{
    /// <summary>True when ip or visitorId is tenant-whitelisted (D19 override loop).</summary>
    Task<bool> IsWhitelistedAsync(string ip, string? visitorId, CancellationToken ct);
}
```

`RedisWhitelistCheck` (scoped; ctor `IConnectionMultiplexer, ITenantContext, IWhitelistCacheRebuilder`): one `IBatch` issuing, per DAT-07's binding cache contract (`t:{tenantId:D}:wl:{sourceType}`, sourceType ∈ `ip` | `device_id` | `fingerprint`): `SISMEMBER t:{tid}:wl:ip {ip}` and (when visitorId present) `SISMEMBER t:{tid}:wl:fingerprint {visitorId}` — the FingerprintJS visitorId is checked against the `fingerprint` set (documented decision; the `device_id` set is not consulted at MVP) — plus an `EXISTS` per consulted key in the same batch; OR the SISMEMBER results. **DAT-07 miss behavior**: a consulted key that does not EXIST means "unknown" — treat as NOT whitelisted for this request and fire-and-forget `IWhitelistCacheRebuilder.ScheduleRebuild(sourceType)` off the awaited path (never a SQL call on the hot path). `IWhitelistCacheRebuilder` is a small port defined in this task (`TelemetryGuard.RiskEngine/Pipeline/IWhitelistCacheRebuilder.cs`, `void ScheduleRebuild(string sourceType)`) with a no-op default registered via `TryAdd`; the API host replaces it with an adapter that queues DAT-07's `IWhitelistRepository.RebuildCacheAsync` on a background task. Both sets are populated by DAT-07's repository/mirror — this class only reads.

4. Create the enrichment cache decorator `CachedIpEnrichmentService` (`TelemetryGuard.RiskEngine/Pipeline/CachedIpEnrichmentService.cs`): wraps `IIpEnrichmentService` with `IMemoryCache`, key `"ipe:" + ip`, absolute TTL 5 minutes, size-limited cache (set `SizeLimit = 100_000`, entry size 1). Register so the pipeline receives the cached instance (decorate in DI: register `IpEnrichmentService` concrete + `CachedIpEnrichmentService` as the `IIpEnrichmentService` the pipeline resolves — keep RSK-02's `AddIpEnrichment` and override the interface registration in `AddScoringPipeline`).

5. Create `TelemetryGuard.RiskEngine/Pipeline/IScoringPipeline.cs` + `ScoringPipeline.cs`:

```csharp
using TelemetryGuard.RiskEngine.Contracts;

namespace TelemetryGuard.RiskEngine.Pipeline;

public sealed record ScoringOutcome(
    ScoreResult Result, VerdictBand Band, bool Whitelisted, double DurationMs);

public interface IScoringPipeline
{
    /// <summary>Returns null when the session id is unknown (no click, no beacon).
    /// <paramref name="outcome"/> is the Turnstile result for §6.3 re-scoring:
    /// API-05 passes ChallengeOutcome.Passed/Failed after verification; first-score
    /// callers (API-06 worker, initial /decide call) omit it. Tenant stays ambient
    /// via ITenantContext (D11) — never a parameter.</summary>
    Task<ScoringOutcome?> ScoreSessionAsync(
        string sessionId,
        ChallengeOutcome outcome = ChallengeOutcome.NotChallenged,
        CancellationToken ct = default);
}
```

   **Signature authority note:** API-05/API-06 quote an "expected shape" (`ScoreAsync(ScoringRequest(TenantId, SessionId, ChallengeOutcome))` returning `ScoringResult`) and both explicitly defer to THIS file as authoritative ("consult RSK-07's task file for the real signature; adapt call sites, do not redefine"). Their call sites map to `ScoreSessionAsync(sid, outcome, ct)`; `ScoringResult`'s members live on `ScoringOutcome.Result` (Score/RuleHits/ScorerVersion/FeatureSetVersion) plus `ScoringOutcome.Band`; the tenant comes from the ambient scoped `ITenantContext`, which both callers already stamp before resolving the pipeline.

`ScoringPipeline` (scoped) ctor: `(ISessionStateStore sessions, IWhitelistCheck whitelist, IIpEnrichmentService enrichment, IVelocityStore velocity, IFeatureExtractor extractor, IT1RuleEngine rules, IScorer scorer, ICampaignContextProvider campaigns, ILogger<ScoringPipeline>)`. Algorithm:

```
var sw = ValueStopwatch/Stopwatch.StartNew();
1. state = await sessions.GetAsync(sid) ; null → record metric(outcome="not_found"), return null
2. ip = state.Click?.Ip; visitorId = state.Beacon?.VisitorId
   // beacon-only sessions have no click hash and API-04's aggregate hash stores no
   // request info: ip may be null → use "" for RawSessionData.Ip; enrichment of ""
   // yields the all-null Empty enrichment and velocity reads only device-keyed
   // counters — degraded-not-zero per RSK-04. (If API-04 later adds an ip field
   // to the session hash, map it here.)
3. if (await whitelist.IsWhitelistedAsync(ip, visitorId, ct))
       → outcome = new ScoringOutcome(
             new ScoreResult(0, new[] { "whitelisted" }, "whitelist-short-circuit",
                             FraudFeatureVector.FeatureSetVersion),
             VerdictBand.Allow, Whitelisted: true, sw.ElapsedMs);
         record metric; return outcome;                       // NOTHING else runs
4. Parallel prefetch (Task.WhenAll):
       velocityTask = velocity.ReadAsync(ip, visitorId, ct)   // single Redis RTT (RSK-03)
       campaignTask = campaigns.GetAsync(state.CampaignId, ct) // 60s memory-cached DAT-05 lookup; null-safe
   enrichmentResult = enrichment.Enrich(ip)                    // synchronous, in-process (cached 5 min)
5. raw = assemble RawSessionData from state + enrichmentResult + velocity + campaign
        (ChallengeOutcome from the `outcome` PARAMETER — session state stores no
         challenge outcome; IsPaidClick/ClickId/ClickIdFresh from ClickState;
         Beacon possibly null — that IS the non-JS path and must proceed)
6. vector = extractor.Extract(raw)                             // pure
7. t1 = rules.Evaluate(in vector)                              // pure
8. sr = scorer.Score(in vector)                                // pure, RuleHits empty
9. finalScore = Math.Max(sr.Score, t1.Floor ?? 0)
   result = new ScoreResult(finalScore, t1.Hits, sr.ScorerVersion, sr.FeatureSetVersion)
10. band = BandMapper.ToBand(finalScore)
11. metric histogram record(sw.ElapsedMs, tags: band, whitelisted=false); return outcome
```

`ICampaignContextProvider` is a small port defined here (`Task<CampaignContext?> GetAsync(string? campaignId, CancellationToken)`) with a default implementation backed by the DAT-05 campaign repository + `IMemoryCache` (60 s TTL); when DAT-05's repository type is unavailable at wiring time, a `NullCampaignContextProvider` returning null keeps the engine testable — the API host wires the real one.

6. Metrics (`TelemetryGuard.RiskEngine/Pipeline/RiskMetrics.cs`): static `Meter` named `"TelemetryGuard.RiskEngine"`; `Histogram<double> ScoringDuration = meter.CreateHistogram<double>("tg.scoring.duration_ms", unit: "ms")` with tags `band` (`allow|challenge|block|not_found`) and `whitelisted` (bool). API-01's OpenTelemetry setup subscribes to this meter by name — document the meter name in the class comment.

7. DI extension `AddScoringPipeline(this IServiceCollection, IConfiguration)` registering: session store, whitelist check, whitelist cache rebuilder (TryAdd the no-op default — the API host overrides it with the DAT-07 adapter), cached enrichment decoration, campaign provider (Try-add null impl), pipeline; and calling the earlier `AddIpEnrichment`/`AddVelocityStore`/rules/scorer registrations if the host has not already (use `TryAdd` patterns so double-registration is harmless).

8. Env-gated perf test (`tests/TelemetryGuard.Tests.Unit/Pipeline/ScoringPipelinePerfTests.cs`):
   - Skipped unless `RUN_PERF_TESTS=1` (`[SkippableFact]` via the `Xunit.SkippableFact` package, or a plain `[Fact]` that returns early with `Assert.True(true)` when the env var is absent — prefer SkippableFact for honest reporting).
   - Setup: in-memory fakes for `ISessionStateStore` (returns a rich pre-built `SessionState` whose `BeaconData` aggregates model a 200-move session: `MmN=200`, mean/M2/path-length and all counters populated), `IWhitelistCheck` (false), `IVelocityStore` (returns fixed snapshot), real `FeatureExtractor` + `T1RuleEngine` + `HeuristicScorer`, `IpEnrichmentService` with no DBs (null path) — i.e. measures the full COMPUTE path without container I/O.
   - Warmup 100 iterations; measure 1000 iterations of `ScoreSessionAsync`; collect per-iteration ms; assert `p99 < 50` and log p50/p95/p99 to test output. (Production Redis RTTs are bounded separately by RSK-03's single-batch guarantee.)

## Files to create or modify

- `TelemetryGuard.RiskEngine/Pipeline/ISessionStateStore.cs`
- `TelemetryGuard.RiskEngine/Pipeline/RedisSessionStateStore.cs`
- `TelemetryGuard.RiskEngine/Pipeline/IWhitelistCheck.cs`
- `TelemetryGuard.RiskEngine/Pipeline/IWhitelistCacheRebuilder.cs` (+ no-op default impl)
- `TelemetryGuard.RiskEngine/Pipeline/RedisWhitelistCheck.cs`
- `TelemetryGuard.RiskEngine/Pipeline/CachedIpEnrichmentService.cs`
- `TelemetryGuard.RiskEngine/Pipeline/ICampaignContextProvider.cs` (+ null impl)
- `TelemetryGuard.RiskEngine/Pipeline/IScoringPipeline.cs`
- `TelemetryGuard.RiskEngine/Pipeline/ScoringPipeline.cs`
- `TelemetryGuard.RiskEngine/Pipeline/RiskMetrics.cs`
- `TelemetryGuard.RiskEngine/Pipeline/PipelineServiceCollectionExtensions.cs`
- `tests/TelemetryGuard.Tests.Unit/Pipeline/ScoringPipelineTests.cs`
- `tests/TelemetryGuard.Tests.Unit/Pipeline/ScoringPipelinePerfTests.cs`
- `tests/TelemetryGuard.Tests.Integration/Pipeline/SessionStateStoreTests.cs`

## Acceptance criteria

- `dotnet build` passes; `AddScoringPipeline` wires a resolvable `IScoringPipeline` in a test `ServiceCollection` with fakes for Redis-backed pieces.
- Whitelist short-circuit: whitelisted ip → outcome `(Score 0, RuleHits ["whitelisted"], ScorerVersion "whitelist-short-circuit", Band Allow, Whitelisted true)` AND `IVelocityStore`/`IScorer`/`IT1RuleEngine` fakes record zero calls.
- Unknown session id → returns null, no throw.
- Floor combination: fakes produce scorer score 40 + rule floor 85 → final 85, band Block, hits from the rule engine, `ScorerVersion` from the scorer (not overwritten by the floor).
- Scorer higher than floor: score 60, floor 31 → final 60 (Max, not sum).
- No-beacon session (state has Click only) scores successfully with `HasJsBeacon=false` semantics (asserted via a capturing fake extractor or the real one).
- Challenge re-score: `ScoreSessionAsync(sid, ChallengeOutcome.Passed)` produces a vector with `ChallengeOutcome == Passed`, `Failed` produces `Failed`, and the default call produces `NotChallenged` (capturing fake extractor).
- Whitelist cache miss: a consulted `wl:` key that does not EXIST → session treated as NOT whitelisted, `IWhitelistCacheRebuilder.ScheduleRebuild` invoked with the right sourceType (fake), and the request path never awaits the rebuild.
- Reader contract (integration, Testcontainers Redis): seed `t:{tid}:click:{sid}` exactly as API-02 step 3.7 writes it and `t:{tid}:sess:{sid}` exactly as API-04 step 6 writes it (flat fields, invariant-culture strings) → `GetAsync` returns a `SessionState` with a correctly typed `ClickState` and aggregate `BeaconData` (spot-assert `MmN`, `MmMeanMs`, `MmM2`, `MmPathLen`, counters, `IntegrityOk` derived from `integrity_fails`, `ClickIdFresh` derived from `click_id`/`click_id_invalid`); click hash only → `Beacon == null`; sess hash only → `Click == null`; neither → null; the store performs NO Redis writes.
- Metric emitted once per `ScoreSessionAsync` call with a `band` tag (assert via `MetricCollector<double>` from `Microsoft.Extensions.Diagnostics.Testing`, or a `MeterListener`).
- `RUN_PERF_TESTS=1 dotnet test --filter "FullyQualifiedName~ScoringPipelinePerf"` passes locally with p99 < 50 ms; without the env var the test reports skipped.

## Testing

- Unit (`ScoringPipelineTests`): hand-rolled fakes (no mocking framework needed) covering the criteria above — short-circuit, null session, Max-fold both directions, band mapping, whitelist flag, metric emission.
- Integration (`SessionStateStoreTests`): Testcontainers Redis (share the fixture pattern from RSK-03), fake `ITenantContext`; seed the producer-format hashes (API-02 step 3.7 / API-04 step 6) and assert the read mapping, null cases, read-only behavior, and tenant-prefix usage.
- Perf: as step 8. Do not run perf assertions in CI by default (env-gated) — CI boxes are noisy; FND-03's pipeline may opt in later.

## Out of scope / guardrails

- **Budget is law (D3/§2):** in the non-whitelisted path the pipeline performs exactly TWO awaited Redis round trips (session `GetAsync` batch, velocity `ReadAsync` batch) plus one optional cached SQL campaign lookup — nothing else may await network. Enrichment stays synchronous in-process. Never add SQL/ClickHouse/HTTP calls to this path; verdict persistence and analytics writes are API-06/ANA-03's job, AFTER scoring, never blocking it.
- Rules only RAISE: the single `Math.Max(sr.Score, floor ?? 0)` in step 9 is the only combination point; nothing may lower the scorer's output (§6.3).
- Whitelist short-circuit must skip extraction/rules/scoring entirely and be explicitly flagged (`Whitelisted=true`, hit `"whitelisted"`) so D19's override loop never masquerades as an organic allow in verdicts or training data.
- NaN-not-zero: the pipeline passes degraded inputs through untouched — no defaulting of missing beacon/enrichment values before `Extract` (RSK-04 owns all NaN/null mapping).
- Tenancy: every Redis key here is `t:{tenantId}:…` from `ITenantContext` (D11); tenant id is never a method parameter or optional. Do not cache enrichment per-tenant (it is tenant-independent reference data) but NEVER cache session/whitelist data across tenants.
- Do not implement verdict persistence, grace-period timing, or finalization triggers here — API-06 owns when/why `ScoreSessionAsync` is called and what happens to the outcome. Do not verify Turnstile tokens here (INT-01/API-05).
- No generic query/provider layers; no EF (D9); no server-side Python/Node (D1). Redis via `StackExchange.Redis` only (D5).
- ListenOnly/shadow-scoring fields do NOT exist yet — RSK-08 (Phase 1.5) extends `ScoringOutcome`; do not pre-build them.
