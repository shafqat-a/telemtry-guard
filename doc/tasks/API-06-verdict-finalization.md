---
id: API-06
title: Verdict finalization and grace-period worker
phase: 1
workstream: api
depends_on: [API-02, API-04, RSK-07, DAT-06]
size: L
spec_refs: ["§6.1 steps 3-5", "§6.3", D18, D19, D20, D21, D23]
detail_level: full
---

# API-06: Verdict finalization and grace-period worker

## Objective

Implement the single shared finalization path for every session: `VerdictFinalizer.FinalizeAsync` (used by `/decide` in API-05 and by the background `VerdictFinalizerService`, which polls the grace ZSETs every second and finalizes sessions whose ~10 s beacon grace period expired). Finalization scores the session through the RSK-07 pipeline, emits a `ClickEvent` of kind `verdict` stamped with `scorer_version` + `feature_set_version` (D18), enqueues block-band sources into the SQL exclusion queue with a status derived from the tenant's `EnforcementMode` (D21), MERGEs the per-day verdict summary (D23), and writes weak-positive training labels for T1 rule hits via `ILabelSink` (D18).

## Spec context (self-contained)

- **Flow (spec §6.1)**: tracker/pixel hits register a grace entry; if no beacon arrives within ~10 s the session is scored on HTTP + velocity features alone (`has_js_beacon=0` — the non-JS-bot path, by design). Verdicts are persisted to BOTH stores: ClickHouse event (via `IEventSink`) + SQL summary (D23). Score 71–100 → block: excluded from attribution and queued for exclusion-list sync.
- **Bands (§6.3)**: 0–30 allow · 31–70 challenge · 71–100 block (thresholds from `Scoring:Bands` config). Deterministic T1 rules only ever RAISE a score floor — never lower.
- **D18 (cold start)**: every verdict is stamped with `scorer_version` (heuristic-era vs model-era data must never be confused) and `feature_set_version` (= 1). T1 rule hits are **weak fraud positives** for the future label pipeline (RSK-08) — written at finalize time via `ILabelSink`.
- **D21 (enforcement autonomy)**: per-tenant `EnforcementMode`: `AutoEnforce` (default) → exclusion entries are immediately actionable (`approved`); `ApprovalQueue` → entries wait for tenant approval (`pending`). The full approval flow is INT-02; MVP only maps the status and leaves a TODO marker.
- **D23**: SQL Server holds summaries/breakdowns; ClickHouse holds raw events. The finalizer writes the daily summary MERGE through the DAT-06 repository on a tenant-stamped connection (RLS via `SESSION_CONTEXT('TenantId')`) — never directly to ClickHouse aggregates, never raw SQL outside a repository.
- **Idempotency**: a session may be finalized by `/decide` and claimed by the worker in the same second. Guard: `SETNX t:{tid}:fin:{sid}` with 1 h expiry; losers do nothing. Worker claim = `ZREM` returning 1. **Single-instance MVP assumption**: one API process runs the worker; the SETNX makes double-finalization harmless anyway; multi-instance claiming (Lua atomic pop) is explicitly deferred.
- **D20**: verdict `ClickEvent`s carry `retention_days` from tenant config like every analytics row.

## Prerequisites

- **API-02 / API-03**: grace entries `ZADD t:{tid}:grace <deadlineUnixSeconds> <sid>`; tenant bookkeeping `SADD grace:tenants <tid>`; click-context hash `t:{tid}:click:{sid}` (fields incl. `ip`, `campaign_id`, `click_id_type`, `click_id` — see API-02 step 3.7). `tid` = tenant Guid "D" lowercase.
- **API-04**: session hash `t:{tid}:sess:{sid}` (presence ⇒ `has_js_beacon=1`; fields listed in API-04 step 6).
- **RSK-07 (OWNS the scoring seam — defined in `TelemetryGuard.RiskEngine/Pipeline/IScoringPipeline.cs`; consume verbatim, never redefine)**:
  ```csharp
  public sealed record ScoringOutcome(
      ScoreResult Result, VerdictBand Band, bool Whitelisted, double DurationMs);

  public interface IScoringPipeline
  {
      /// <summary>Returns null when the session id is unknown (no click, no beacon).</summary>
      Task<ScoringOutcome?> ScoreSessionAsync(string sessionId, CancellationToken ct);
  }
  ```
  There is NO `ScoreAsync(ScoringRequest, ...)` and no `ScoringResult` record — the tenant is ambient (`ITenantContext`, which the worker stamps per scope) and the return is nullable. `ScoreResult` (RSK-01) carries `Score`, `RuleHits`, `ScorerVersion`, `FeatureSetVersion`; the band and the D19 `Whitelisted` flag ride `ScoringOutcome`. API-05's prerequisites quote the same types.
- **API-05 (either order) — `ScoringBandOptions`**: step 4 reads `Scoring:Bands:ChallengeMax` via `IOptions<ScoringBandOptions>`, but that class is created by API-05. If API-05 has not landed yet, CREATE `TelemetryGuard.Api/Options/ScoringBandOptions.cs` with exactly:
  ```csharp
  namespace TelemetryGuard.Api.Options;

  public sealed class ScoringBandOptions
  {
      public int AllowMax { get; set; } = 30;
      public int ChallengeMax { get; set; } = 70;
  }
  ```
  bound in `Program.cs` via `builder.Services.Configure<ScoringBandOptions>(builder.Configuration.GetSection("Scoring:Bands"));` — identical to API-05 step 1, so whichever task lands second keeps the existing file.
- **DAT-06** (`TelemetryGuard.Data`, see `doc/tasks/DAT-06-verdict-exclusion-watermark-tables.md`): tables `dbo.VerdictDailySummaries`, `dbo.ExclusionQueue` (columns: `TenantId, Id identity, Platform 'google'|'meta'|'tiktok'|'other', SourceType 'ip'|'placement', Value varchar(256), Reason nvarchar(400), Status 'pending'|'approved'|'rejected'|'pushed'|'failed'|'unsupported', CampaignScope uniqueidentifier NULL, CreatedUtc, UpdatedUtc NULL, PushedUtc NULL, LastError nvarchar(2000) NULL`), all under the RLS policy (`rls.fn_tenantPredicate`). Repositories: `IVerdictSummaryRepository` with **absolute-value** `UpsertDailySummaryAsync(VerdictDailySummaryRow row, ct)` (rollup-owned — NOT usable for per-verdict increments) and reads; `VerdictDailySummaryRow(Guid TenantId, Guid CampaignId, DateOnly Date, int Allowed, int Challenged, int Blocked, long ScoreSum, int Events)`. **DAT-06 deliberately ships NO ExclusionQueue repository** — "API-06 (writer) owns its access path" — so this task creates it (step 3).
- **DAT-05 (transitively via API-02)**: `ITenantRepository.GetCurrentAsync` → `TenantRecord(..., int RetentionDays, byte EnforcementMode, ...)` with `EnforcementMode`: `0 = AutoEnforce`, `1 = ApprovalQueue` (D21).
- **ANA-01/ANA-03**: the label contract ALREADY EXISTS in `TelemetryGuard.Analytics.Abstractions` — do not create a new one:
  ```csharp
  public interface ILabelSink { ValueTask WriteAsync(LabelEvent label, CancellationToken ct); }
  public sealed record LabelEvent(TenantId TenantId, string SessionId,
      string Label /* LabelValues.Fraud|Legit */, string LabelSource /* LabelSources.T1Rule|... */,
      DateTime CreatedAtUtc);
  ```
  ANA-03's `ClickHouseLabelSink` implements it (bulk-inserts into ClickHouse `tg_labels`) and is registered by the analytics DI switch (ANA-05). "tg_labels" is a ClickHouse table (ANA-02), NOT SQL.
- **FND-04**: `ITenantContext` is scoped and settable by infrastructure — the background worker has no HTTP request, so it must create a DI scope and stamp the tenant itself. Check FND-04's file for the mutable implementation (e.g. `TenantContext.Set(TenantId)` or an `ITenantContextSetter`); every repository call below happens inside such a scope.
- **API-05 (either order)**: may already have created `Services/IVerdictFinalizer.cs` + `StubVerdictFinalizer`. The interface below is IDENTICAL to API-05's — if it exists, keep the file, DELETE the stub class, and swap the DI registration to the real implementation.

## Implementation steps

1. **Interface** — ensure `TelemetryGuard.Api/Services/IVerdictFinalizer.cs` exists with exactly:
   ```csharp
   namespace TelemetryGuard.Api.Services;

   using TelemetryGuard.RiskEngine.Pipeline;   // ScoringOutcome (RSK-07)

   public enum FinalizeTrigger { GraceExpired, Decide }

   public interface IVerdictFinalizer
   {
       Task FinalizeAsync(TenantId tenantId, string sessionId, FinalizeTrigger trigger,
                          ScoringOutcome? precomputed, CancellationToken ct);
   }
   ```

2. **Label sink** — consume ANA-01's `ILabelSink`/`LabelEvent` (`TelemetryGuard.Analytics.Abstractions`) as-is; ANA-03/ANA-05 provide and register `ClickHouseLabelSink` → ClickHouse `tg_labels`. **Do NOT create a new label interface, a SQL labels table, or a migration.** If the analytics DI switch (ANA-05) is not merged yet, register a temporary no-op `LoggingLabelSink : ILabelSink` in `Program.cs` with `// TODO(ANA-05): replaced by provider registration`.

3. **Exclusion-queue repository** — DAT-06 ships the table only; create the writer here, in `TelemetryGuard.Data/Repositories/ExclusionQueueRepository.cs` (Dapper, constructor `(ITenantConnectionFactory connections, ITenantContext tenant)`, connection via `OpenAsync` only — same pattern as DAT-05/06 repos):
   ```csharp
   namespace TelemetryGuard.Data.Repositories;

   public sealed record ExclusionQueueInsert(
       string Platform /* "google" | "meta" | "tiktok" | "other" — routes the row to its sync worker (INT-03/INT-04) */,
       string SourceType /* "ip" | "placement" */, string Value, string Reason,
       string Status /* "pending" | "approved" */, Guid? CampaignScope);

   public interface IExclusionQueueRepository
   { Task EnqueueAsync(ExclusionQueueInsert entry, CancellationToken ct); }
   ```
   SQL (explicit TenantId; RLS BLOCK predicate backstops it):
   ```sql
   INSERT INTO dbo.ExclusionQueue (TenantId, Platform, SourceType, Value, Reason, Status, CampaignScope)
   VALUES (@TenantId, @Platform, @SourceType, @Value, @Reason, @Status, @CampaignScope);
   ```
   Guard `Platform` ∈ {google, meta, tiktok, other}, `SourceType` ∈ {ip, placement} and `Status` ∈ {pending, approved} in C# before SQL. Register scoped in `AddTelemetryGuardData` (`TryAddScoped`).

3b. **Live summary increment** — DAT-06's `UpsertDailySummaryAsync` is an ABSOLUTE-value MERGE reserved for the ANA-07 rollup (re-runs must converge; increments there would double-count). The live path needs increments, so ADD to `IVerdictSummaryRepository` (same file, `TelemetryGuard.Data/Repositories/ISummaryRepositories.cs`) and implement in `VerdictSummaryRepository`:
   ```csharp
   /// <summary>Live-path per-verdict increment (API-06). The ANA-07 rollup's absolute
   /// upsert later overwrites these rows from ClickHouse — the rollup stays authoritative.</summary>
   Task IncrementDailySummaryAsync(VerdictDailySummaryRow delta, CancellationToken ct);
   ```
   ```sql
   MERGE dbo.VerdictDailySummaries WITH (HOLDLOCK) AS t
   USING (SELECT @TenantId AS TenantId, @CampaignId AS CampaignId, @Date AS [Date]) AS s
       ON t.TenantId = s.TenantId AND t.CampaignId = s.CampaignId AND t.[Date] = s.[Date]
   WHEN MATCHED THEN UPDATE SET
       Allowed = t.Allowed + @Allowed, Challenged = t.Challenged + @Challenged,
       Blocked = t.Blocked + @Blocked, ScoreSum = t.ScoreSum + @ScoreSum,
       Events = t.Events + @Events, UpdatedUtc = SYSUTCDATETIME()
   WHEN NOT MATCHED THEN INSERT (TenantId, CampaignId, [Date], Allowed, Challenged, Blocked, ScoreSum, Events)
       VALUES (@TenantId, @CampaignId, @Date, @Allowed, @Challenged, @Blocked, @ScoreSum, @Events);
   ```
   Guard `delta.TenantId == tenant.TenantId.Value` like the existing methods. Sessions with no campaign (pixel-only) use `Guid.Empty` as the `CampaignId` sentinel — document this on the method.

4. **Real finalizer** — `TelemetryGuard.Api/Services/VerdictFinalizer.cs` (`IVerdictFinalizer`), DI: `IScoringPipeline`, `IEventSink`, `IExclusionQueueRepository`, `IVerdictSummaryRepository`, `ILabelSink`, tenant-config repo, `ICampaignRepository` (DAT-05 — for the exclusion row's `Platform`), `IConnectionMultiplexer`, `IClock`, `IMemoryCache`, `IOptions<ScoringBandOptions>`, `IOptions<RetentionOptions>`, `ILogger`. Logic:
   1. `tid = tenantId.Value.ToString("D")`; **idempotency**: `db.StringSetAsync($"t:{tid}:fin:{sessionId}", "1", TimeSpan.FromHours(1), When.NotExists)` → `false` ⇒ return.
   2. `ZREM t:{tid}:grace {sessionId}` (idempotent cleanup regardless of trigger).
   3. `var outcome = precomputed ?? await pipeline.ScoreSessionAsync(sessionId, ct);` — **null handling is mandatory** (RSK-07 returns null for unknown sessions): `if (outcome is null) { logger.LogWarning(...); await db.KeyDeleteAsync($"t:{tid}:fin:{sessionId}"); return; }` (release the claim so a later-arriving beacon can still be finalized). Below, `result = outcome.Result` (RSK-01 `ScoreResult`: `Score`, `RuleHits`, `ScorerVersion`, `FeatureSetVersion`) and `band = outcome.Band`.
   4. Read click context `HGETALL t:{tid}:click:{sessionId}` (ip, campaign_id, click ids, site_key) and `EXISTS t:{tid}:sess:{sessionId}` → `hasJsBeacon`.
   5. **Verdict event**: build `ClickEvent` with `Kind = EventKind.Verdict` (ANA-01) — sid, site key, campaign id, ip, `TimestampUtc` (`IClock`, `DateTimeKind.Utc`), `Score`, band, `RuleHits`, `ScorerVersion` (D18 stamp), `FeatureSetVersion` (= 1), `HasJsBeacon`, `RetentionDays` (ushort, from cached `TenantRecord.RetentionDays`, default 90) — `await sink.WriteBatchAsync(new[]{evt}, ct);` (non-blocking enqueue).
   6. **Block band** (`result.Score > Scoring:Bands:ChallengeMax`; a whitelisted outcome is Score 0/Allow and never reaches this branch): read `TenantRecord.EnforcementMode` (byte, DAT-05: 0 = AutoEnforce, 1 = ApprovalQueue); resolve `platform`: when `clickCtx.CampaignId` is present → `campaigns.GetAsync(campaignGuid, ct)` (cache in `IMemoryCache` 60 s under `campaignrec:{tid}:{cid}`) → `CampaignRecord.Platform`; campaign-less sessions (pixel/organic) → `"other"` (documented default — INT-03/INT-04 filter on their own platform, so `"other"` rows are simply never pushed);
      ```csharp
      var status = tenantRecord.EnforcementMode == 1 ? "pending" : "approved";
      // TODO(INT-02): ApprovalQueue flow (tenant approval + notifications) lands in INT-02;
      // AutoEnforce (default) enqueues straight to 'approved' for INT-03/INT-04 sync pickup.
      await exclusions.EnqueueAsync(new ExclusionQueueInsert(platform, "ip", clickCtx.Ip,
          $"score={result.Score} rules={string.Join('|', result.RuleHits)}",
          status, clickCtx.CampaignId /* Guid? — null = tenant-wide */), ct);
      ```
      Skip (log warning) when the click-context ip is missing.
   7. **Daily summary increment** (step 3b's method — NOT the absolute rollup upsert): `IncrementDailySummaryAsync(new VerdictDailySummaryRow(tenantId.Value, clickCtx.CampaignId ?? Guid.Empty, DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), Allowed: band==Allow?1:0, Challenged: band==Challenge?1:0, Blocked: band==Block?1:0, ScoreSum: result.Score, Events: 1), ct)`.
   8. **Weak labels (D18) — guarded against whitelist pollution (D19)**:
      ```csharp
      if (!outcome.Whitelisted && result.RuleHits.Count > 0)
          await labelSink.WriteAsync(new LabelEvent(tenantId, sessionId,
              LabelValues.Fraud, LabelSources.T1Rule, clock.UtcNow.UtcDateTime), ct);
      ```
      RSK-07's whitelist short-circuit returns `RuleHits ["whitelisted"]` / `ScorerVersion "whitelist-short-circuit"` — a human just marked that source as a real customer; labeling it fraud would poison training (D19, RSK-07 guardrail). The `Whitelisted` flag is the guard; belt-and-braces, also skip when `RuleHits.Contains("whitelisted")`. Rule ids and scorer/feature-set stamps are already on the verdict event (same sid, joinable in ClickHouse; `LabelEvent` deliberately carries only the label facts, per ANA-01). **This live sink is the ONLY writer of `source='t1_rule'` rows** — RSK-08's batch LabelBuilder must not re-emit t1_rule labels for finalized sessions (tg_labels is a non-deduplicating MergeTree; RSK-08's source (a) is superseded by this path and its file must treat t1_rule as live-written).
   9. Steps 5–8 each in try/catch (log + continue): one store failing must not abort the others; the idempotency key is already claimed so there is no retry at MVP — log loudly.

5. **Grace worker** — `TelemetryGuard.Api/Services/VerdictFinalizerService.cs` (`BackgroundService`):
   ```csharp
   protected override async Task ExecuteAsync(CancellationToken stoppingToken)
   {
       using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
       while (await timer.WaitForNextTickAsync(stoppingToken))
       {
           try
           {
               var db = redis.GetDatabase();
               var now = clock.UtcNow.ToUnixTimeSeconds();
               foreach (var tidVal in await db.SetMembersAsync("grace:tenants"))
               {
                   var tid = (string)tidVal!;
                   var graceKey = $"t:{tid}:grace";
                   var due = await db.SortedSetRangeByScoreAsync(graceKey, double.NegativeInfinity, now, take: 100);
                   foreach (var sidVal in due)
                   {
                       var sid = (string)sidVal!;
                       if (await db.SortedSetRemoveAsync(graceKey, sid))       // claim (single-instance MVP)
                       {
                           using var scope = scopeFactory.CreateScope();
                           // stamp tenant context for repositories (FND-04's mutable scoped impl):
                           scope.ServiceProvider.GetRequiredService<TenantContext>()
                               .Set(new TenantId(Guid.Parse(tid)));
                           await scope.ServiceProvider.GetRequiredService<IVerdictFinalizer>()
                               .FinalizeAsync(new TenantId(Guid.Parse(tid)), sid,
                                              FinalizeTrigger.GraceExpired, null, stoppingToken);
                       }
                   }
                   if (await db.SortedSetLengthAsync(graceKey) == 0)
                       await db.SetRemoveAsync("grace:tenants", tid);   // self-heals: next click re-adds
               }
           }
           catch (Exception ex) when (ex is not OperationCanceledException)
           { logger.LogError(ex, "grace sweep failed"); }   // never crash the host
       }
   }
   ```
   `// SINGLE-INSTANCE MVP: ZREM-as-claim is only safe with one worker process. Multi-instance needs an atomic Lua claim — deferred.`

6. **Registration** in `Program.cs`: `builder.Services.AddScoped<IVerdictFinalizer, VerdictFinalizer>();` (replacing `StubVerdictFinalizer` if present — delete the stub file) and `builder.Services.AddHostedService<VerdictFinalizerService>();`. `IExclusionQueueRepository` registers inside `AddTelemetryGuardData` (step 3); `ILabelSink` comes from the analytics provider registration (ANA-05) or the temporary `LoggingLabelSink` (step 2). `VerdictFinalizer` must be resolved from a scope (it consumes scoped tenant-bound repositories); API-05's per-request scope and the worker's manual scope both satisfy this.

## Files to create or modify

- `TelemetryGuard.Api/Services/IVerdictFinalizer.cs` (create if API-05 hasn't; else keep)
- `TelemetryGuard.Api/Services/VerdictFinalizer.cs`
- `TelemetryGuard.Api/Services/VerdictFinalizerService.cs`
- `TelemetryGuard.Api/Services/StubVerdictFinalizer.cs` (DELETE if present)
- `TelemetryGuard.Data/Repositories/ExclusionQueueRepository.cs` (new: `IExclusionQueueRepository` + impl)
- `TelemetryGuard.Data/Repositories/ISummaryRepositories.cs` + `VerdictSummaryRepository.cs` (add `IncrementDailySummaryAsync`)
- `TelemetryGuard.Api/Services/LoggingLabelSink.cs` (only if ANA-05's provider registration isn't merged yet)
- `TelemetryGuard.Api/Program.cs` (registrations)
- `tests/TelemetryGuard.Tests.Unit/Api/VerdictFinalizerTests.cs`
- `tests/TelemetryGuard.Tests.Integration/Api/GraceWorkerTests.cs`

## Acceptance criteria

- Calling `FinalizeAsync` twice for the same (tenant, sid) performs the pipeline/sink/SQL work exactly once (second call short-circuits on the `t:{tid}:fin:{sid}` SETNX).
- A tracker hit with no beacon: after ~11 s the worker emits a kind-`verdict` `ClickEvent` with `has_js_beacon=0`, non-empty `ScorerVersion`, `FeatureSetVersion=1`, `RetentionDays` in [30,180]; the grace ZSET no longer contains the sid; `grace:tenants` is pruned once empty.
- Precomputed path (`/decide`): `FinalizeAsync(..., precomputed: outcome, ...)` performs NO second `ScoreSessionAsync` call (assert with capturing fake).
- Unknown session: pipeline fake returning **null** → warning logged, `t:{tid}:fin:{sid}` key RELEASED (deleted), no sink/SQL/label writes, no throw.
- Block-band result (score ≥ 71 with default bands): one `dbo.ExclusionQueue` row with `SourceType='ip'`, the click-context IP in `Value`, `Platform` = the campaign's `CampaignRecord.Platform` (e.g. `'google'`) when the click context carries a campaign id, `'other'` when campaign-less; `Status='approved'` when `TenantRecord.EnforcementMode==0` (AutoEnforce), `'pending'` when `==1` (ApprovalQueue); a `TODO(INT-02)` comment exists at the status-mapping site.
- Every finalization calls `IncrementDailySummaryAsync` exactly once (counts consistent with the band; `ScoreSum` = score; `Events` = 1); two finalizations for the same tenant/campaign/day leave a row with summed counts (incremental MERGE proven in the SQL integration test). The absolute `UpsertDailySummaryAsync` is never called from this task's code.
- `ScoringOutcome` with `Whitelisted=false` and `Result.RuleHits=["honeypot_touched"]` → exactly one `LabelEvent(Label=LabelValues.Fraud, LabelSource=LabelSources.T1Rule)` written to `ILabelSink`; empty `RuleHits` → none.
- **Whitelist short-circuit outcome** (`Whitelisted=true`, `RuleHits=["whitelisted"]`, `ScorerVersion="whitelist-short-circuit"`, score 0) → NO `LabelEvent` and NO exclusion row; the verdict event still ships (flagged data, not training data).
- Cross-tenant proof: exclusion rows written for tenant A are invisible on a tenant-B-stamped connection (RLS; extends DAT-08's harness).
- Sink failure (throwing fake) does not prevent the summary MERGE, and vice versa; worker survives a Redis outage (logs, keeps ticking).
- `grep -rn "StubVerdictFinalizer" TelemetryGuard.Api/` → only in git history, not in code.

## Testing

- **Unit** (`VerdictFinalizerTests`): fakes for pipeline/sink/repos/labels + Redis fake or Testcontainer — idempotency, precomputed short-circuit, band→status matrix (`AutoEnforce`/`ApprovalQueue` × allow/challenge/block: exclusion row ONLY for block), summary delta correctness, label emission rules, partial-failure isolation, missing click-context IP (warn, no exclusion row).
- **Integration** (`GraceWorkerTests`, Testcontainers Redis + SQL Server with DbUp migrations, per DAT-08 harness): seed grace entry with past deadline + click-context hash → start the hosted service → within 3 s assert verdict event captured, summary row upserted, fin-key set. Include the deliberate cross-tenant RLS read (must return empty).

## Out of scope / guardrails

- **Rules only raise scores** — the finalizer consumes the `ScoringOutcome` verbatim; NEVER adjust, clamp, or "correct" a score here.
- **NaN-not-zero**: `has_js_beacon=0` is a legitimate observed value (no beacon arrived); do NOT fabricate SDK feature values for the verdict event — absent stays absent (RSK-04/RSK-07 already encoded NaN semantics into the pipeline).
- **Dapper only, tenant-stamped connections only** (RLS via `SESSION_CONTEXT('TenantId')` is primary enforcement; explicit `TenantId` in WHERE clauses anyway). No EF Core, no raw `SqlConnection`, no SQL outside `TelemetryGuard.Data` repositories. `TenantId` is never optional — the worker stamps a scope per claimed session.
- **Real-time path never blocks on the event store** (spec §4): the sink call is an enqueue; never await ClickHouse round-trips or add retries that delay finalization.
- **No enforcement execution** — no Google Ads/Meta API calls (INT-03/INT-04), no approval-queue UX (INT-02). This task only writes queue rows.
- **No ML/training code** (D18: Phase 1 is rules + heuristic): `ILabelSink` writes rows; the pipeline that consumes them is RSK-08. No LightGBM anywhere.
- **Scoring stays <50 ms** in-process (D3) — the worker calls the same pipeline; do not add per-finalization analytics queries (no `IAnalyticsQueries` here; no generic cross-engine query layer, D7).
- Single-instance worker assumption must remain documented in code; do not build distributed locking now.
