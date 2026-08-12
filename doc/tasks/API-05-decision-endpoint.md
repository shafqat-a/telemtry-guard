---
id: API-05
title: Decision endpoint /decide
phase: 1
workstream: api
depends_on: [API-01, RSK-07, INT-01]
size: M
spec_refs: ["§6.2", "§6.3", D14, D18, D19, D21]
detail_level: full
---

# API-05: Decision endpoint /decide

## Objective

Implement `POST /decide` — the endpoint a tenant's page calls on form submit (spec §6.2) to gate the lead before it is accepted. It scores the session via the RSK-07 pipeline, maps the score to a band (allow / challenge / block), drives the Cloudflare Turnstile round-trip for the challenge band (verify token via INT-01, then re-score with the challenge outcome as a context feature), and triggers immediate verdict finalization so the grace-period worker doesn't double-process the session.

## Spec context (self-contained)

- **Lead-form flow (spec §6.2)**: SDK monitors the form; on submit the page calls the decision endpoint with the session ID → allow / challenge (Turnstile token round-trip) / block before the lead is accepted.
- **Bands (spec §6.3)**: score 0–30 → allow; 31–70 → challenge (Turnstile); 71–100 → block + exclude from attribution. Band thresholds come from config `Scoring:Bands` (`AllowMax`=30, `ChallengeMax`=70) — never hardcode.
- **Challenge is always automatic** in both enforcement modes (D21) — a Turnstile prompt is low-harm; `EnforcementMode` affects only block-band *enforcement actions* (handled in API-06/INT-02), not this endpoint's responses.
- **Re-score with challenge outcome**: after Turnstile verification, the session is scored again with `challenge_outcome` as a CTX (context/conditioning) feature (spec §6.3 "re-score with challenge outcome"; feature contract in RSK-01). Rules only ever RAISE scores — a passed challenge influences the model/heuristic through the context feature, it does not subtract points.
- **Scoring budget**: the pipeline call is in-process and must fit the <50 ms budget (D3); this endpoint adds only Turnstile network I/O (challenge path) on top.
- The response never exposes the numeric score or rule hits — clients (and therefore bots) see only the action.
- Turnstile (D14): server-side verification of the widget token; the widget needs the tenant's Turnstile *site key*, which this endpoint returns in the challenge response.

## Prerequisites

- **API-01**: host + middleware; `Scoring` and `Turnstile` appsettings sections; registration point `// app.MapDecisionEndpoints();`.
- **DAT-04 (transitive) — REQUIRED ROUTE EXEMPTION**: `TenantResolutionMiddleware` 401s any unresolved non-ingestion route, and it only reads `?k=` on `/c` and `/p.gif`. `/decide` is called from tenant pages with a site key, so: (1) add `/decide` to the middleware's pass-through predicate (alongside its `/i` beacon-route check, e.g. `IsBeaconRoute(path) || path.StartsWithSegments("/decide")`) in `TelemetryGuard.Api/Tenancy/TenantResolutionMiddleware.cs`; (2) this endpoint resolves the tenant itself exactly like API-04: `resolver.ResolveSiteKeyAsync(k)` (DAT-04's `ITenantResolver`) then `tenantContext.Set(new TenantId(resolved.TenantId))` (FND-04 `TelemetryGuard.Core.Tenancy.TenantContext`). Unknown key → 404 problem (this route is called by first-party SDK code that reads responses — no success-shaped drop needed, and 404 leaks nothing beyond what /c already does).
- **RSK-01** (`TelemetryGuard.RiskEngine.Contracts`, exists): `ChallengeOutcome` enum (`NotChallenged` default, plus passed/failed members — use the exact member names in that file), `VerdictBand` enum + `BandMapper` (0–30 allow / 31–70 challenge / 71–100 block).
- **RSK-07 (OWNS the scoring seam — these types are defined in `TelemetryGuard.RiskEngine/Pipeline/IScoringPipeline.cs`; consume them verbatim, never redefine or wrap them)**:
  ```csharp
  public sealed record ScoringOutcome(
      ScoreResult Result, VerdictBand Band, bool Whitelisted, double DurationMs);

  public interface IScoringPipeline
  {
      /// <summary>Returns null when the session id is unknown (no click, no beacon).</summary>
      Task<ScoringOutcome?> ScoreSessionAsync(string sessionId, CancellationToken ct);
  }
  ```
  The tenant is AMBIENT (`ITenantContext`, stamped in step 2.0) — there is no TenantId parameter, and there is no ChallengeOutcome parameter either: the pipeline reads the challenge outcome from session state. `ScoreResult` (RSK-01) carries `Score`, `RuleHits`, `ScorerVersion`, `FeatureSetVersion`. The return is NULLABLE — every call site below handles the unknown-session case explicitly.
- **RSK-07's `ISessionStateStore`** (same project, `Pipeline/ISessionStateStore.cs`): `Task SetChallengeOutcomeAsync(string sessionId, ChallengeOutcome outcome, CancellationToken ct)` — how this endpoint records a Turnstile result before re-scoring (the pipeline then picks it up as the CTX feature).
- **INT-01**: registered Turnstile verifier — expected shape (consult INT-01's file):
  ```csharp
  public interface ITurnstileVerifier
  { Task<TurnstileVerifyResult> VerifyAsync(string token, string? remoteIp, CancellationToken ct); }
  public sealed record TurnstileVerifyResult(bool Success, string? ErrorCodes);
  ```
- **API-06 (not a listed dependency — build-order shim)**: finalization is API-06's shared service. To keep both build orders working:
  - If `TelemetryGuard.Api/Services/IVerdictFinalizer.cs` already exists (API-06 done first), consume it as-is.
  - Otherwise CREATE it with EXACTLY this content (API-06 specifies the identical interface and will replace the stub with the real implementation):
    ```csharp
    namespace TelemetryGuard.Api.Services;

    using TelemetryGuard.RiskEngine.Pipeline;   // ScoringOutcome (RSK-07)

    public enum FinalizeTrigger { GraceExpired, Decide }

    public interface IVerdictFinalizer
    {
        /// <summary>Idempotently finalizes a session's verdict. precomputed avoids double scoring
        /// when the caller already ran the pipeline.</summary>
        Task FinalizeAsync(TenantId tenantId, string sessionId, FinalizeTrigger trigger,
                           ScoringOutcome? precomputed, CancellationToken ct);
    }
    ```
    and register a stub `StubVerdictFinalizer : IVerdictFinalizer` (logs at Debug, removes the grace entry `ZREM t:{tid}:grace {sid}`, nothing else) in `Program.cs` with a `// TODO(API-06): replaced by real VerdictFinalizer` marker.

## Request/response contract (authoritative)

`POST /decide?k={siteKey}` — body is UTF-8 JSON accepted under `Content-Type: text/plain` **or** `application/json` (text/plain lets the SDK use a preflight-free simple request):
```json
{ "sid": "9f8e7d6c5b4a39281706f5e4d3c2b1a0", "turnstileToken": "optional-widget-token" }
```
(`k` may also appear in the body; if both present they must match → else 400.)

Responses (always `200 application/json` for a decision):
```json
{ "action": "allow" }
{ "action": "challenge", "turnstileSiteKey": "0x4AAAAAAA..." }
{ "action": "block" }
```

**CORS (the caller MUST be able to read the response — unlike `/i`)**: when an `Origin` header is present, every response (including errors) carries `Access-Control-Allow-Origin: *` and `Vary: Origin` (no credentials are used — sid and k ride the body/query, so `*` is correct and simplest). Also map `OPTIONS /decide` → 204 with `Access-Control-Allow-Origin: *`, `Access-Control-Allow-Methods: POST`, `Access-Control-Allow-Headers: Content-Type`, `Access-Control-Max-Age: 86400` for integrators that send `application/json` (which triggers preflight).

Status codes: **200** decision made · **400** malformed JSON, invalid `sid` (must match `^[A-Za-z0-9_-]{8,64}$` — the same pattern API-04 accepts, covering both the tracker's 32-hex ids and SDK-02's `crypto.randomUUID()` fallback for organic sessions), or `k` mismatch (RFC 7807 body) · **404** unknown site key (resolved endpoint-side, problem body) · **429** rate limited (API-01, keyed by IP since the middleware passes this route through unresolved) · **500** RFC 7807. The numeric score, band name, and rule hits are NEVER in any response.

## Implementation steps

1. **Create `TelemetryGuard.Api/Endpoints/DecisionEndpoints.cs`** with `MapDecisionEndpoints(this IEndpointRouteBuilder)` mapping `POST /decide`; uncomment registration in `Program.cs`. Create `Options/ScoringBandOptions.cs` binding `Scoring:Bands` (`AllowMax`, `ChallengeMax`) and reuse the `Turnstile` options class INT-01 created (it holds `SiteKey`, `SecretKey`).

2. **Handler flow** (`DecideRequest(string Sid, string? TurnstileToken, string? K)` DTO parsed from the raw body string via System.Text.Json regardless of declared content type; DI: `ITenantResolver`, `TenantContext`, `IScoringPipeline`, `ISessionStateStore`, `ITurnstileVerifier`, `IVerdictFinalizer`, `IConnectionMultiplexer`, `IOptions<ScoringBandOptions>`, `IOptions<TurnstileOptions>`, `ILogger`):
   0. **Resolve tenant** (middleware passed this route through): `k` from query (fallback body); `resolver.ResolveSiteKeyAsync(k, ct)` → null ⇒ 404 problem; else `tenantContext.Set(new TenantId(resolved.TenantId))`.
   1. Validate body + sid + k-match (400 via `Results.ValidationProblem`).
   2. `var outcome = await pipeline.ScoreSessionAsync(sid, ct);` (tenant is ambient — stamped in step 0).
   3. **Unknown session (`outcome is null` — no click, no beacon ever seen)**: log a warning; do NOT finalize (there is nothing to finalize). Without a token → return `{ "action": "challenge", "turnstileSiteKey": <Turnstile:SiteKey> }` (challenge, never silently allow an unknown sid — low-harm per D21). With a token → verify it: success ⇒ `{ "action": "allow" }`, failure ⇒ `{ "action": "block" }`; still no finalize, no re-score.
   4. Map band from `outcome.Result.Score` using configured thresholds (`<= AllowMax` allow; `<= ChallengeMax` challenge; else block). Trust `outcome.Band` (RSK-07 already maps it) — but assert consistency in Debug. A whitelisted outcome (`outcome.Whitelisted`) is Score 0 / Band Allow by construction and follows the allow path.
   5. **allow** → finalize (step 9) → `{ "action": "allow" }`.
   6. **block** → finalize → `{ "action": "block" }`.
   7. **challenge, no token** → do NOT finalize (the round-trip is still in flight; the grace worker's deadline still backstops abandonment) → `{ "action": "challenge", "turnstileSiteKey": <Turnstile:SiteKey> }`.
   8. **challenge, token present** →
      ```csharp
      var verify = await turnstile.VerifyAsync(req.TurnstileToken!, ctx.Connection.RemoteIpAddress?.ToString(), ct);
      var challengeOutcome = verify.Success ? ChallengeOutcome.Passed : ChallengeOutcome.Failed;
      await sessions.SetChallengeOutcomeAsync(sid, challengeOutcome, ct);   // pipeline reads it from state
      var rescored = await pipeline.ScoreSessionAsync(sid, ct);             // CTX feature now populated
      if (rescored is null) { /* state evaporated mid-flight: log, no finalize */
          return Results.Json(new { action = verify.Success ? "allow" : "block" }); }
      if (!verify.Success) { await Finalize(rescored); return Results.Json(new { action = "block" }); }
      var final = rescored.Result.Score > bands.ChallengeMax ? "block" : "allow";  // challenge never loops
      await Finalize(rescored); return Results.Json(new { action = final });
      ```
      A passed challenge that STILL scores in the block band blocks (rules only raise; a solved Turnstile doesn't wash out T1 evidence). A passed challenge scoring allow-or-challenge allows — never re-challenge.
   9. **Finalize helper**: `await finalizer.FinalizeAsync(tenant.TenantId, sid, FinalizeTrigger.Decide, outcome, ct);` (passing the `ScoringOutcome` the decision used) then remove the grace entry so the worker skips it: `await redis.GetDatabase().SortedSetRemoveAsync($"t:{tid}:grace", sid);` (also done inside the real finalizer — belt and braces; ZREM is idempotent). Wrap finalize in try/catch: a finalization failure must not turn a computed decision into a 500 — log and still return the decision.

3. **Telemetry**: tag the current Activity with `tg.decide.action` and `tg.decide.challenged` (bool). Do not tag the raw score.

## Files to create or modify

- `TelemetryGuard.Api/Endpoints/DecisionEndpoints.cs`
- `TelemetryGuard.Api/Tenancy/TenantResolutionMiddleware.cs` (add `/decide` to the pass-through predicate — one line)
- `TelemetryGuard.Api/Options/ScoringBandOptions.cs`
- `TelemetryGuard.Api/Services/IVerdictFinalizer.cs` (only if API-06 hasn't created it — exact content above)
- `TelemetryGuard.Api/Services/StubVerdictFinalizer.cs` (only if API-06 hasn't replaced it)
- `TelemetryGuard.Api/Program.cs` (bind options; register stub if needed; `app.MapDecisionEndpoints();`)
- `tests/TelemetryGuard.Tests.Unit/Api/DecisionEndpointTests.cs`

## Acceptance criteria

- With a fake pipeline returning a `ScoringOutcome` of score 10 → `{"action":"allow"}`, 200; score 50 → `{"action":"challenge","turnstileSiteKey":"..."}`; score 90 → `{"action":"block"}`. No response body ever contains `score`, `band`, or rule names (assert on raw JSON).
- Fake pipeline returning **null** (unknown session): no token → `{"action":"challenge",...}`, finalizer NOT called; with token + verifier Success → `{"action":"allow"}`; verifier failure → `{"action":"block"}` — finalizer never called in any null-outcome path, and no NRE anywhere.
- Score 50 + valid token + verifier Success + re-score 20 → `{"action":"allow"}`; re-score 85 → `{"action":"block"}`; verifier failure → `{"action":"block"}` and `ISessionStateStore.SetChallengeOutcomeAsync` was called with `ChallengeOutcome.Failed`.
- The token path calls `SetChallengeOutcomeAsync` (Passed/Failed per verifier) BEFORE the second `ScoreSessionAsync`; the initial score call is not preceded by any challenge-outcome write (assert order via capturing fakes).
- Finalizer called exactly once for allow/block/token paths with the SAME `ScoringOutcome` instance the decision used (no double scoring); NOT called on the tokenless challenge response; grace entry `t:{tid}:grace` no longer contains sid after finalize.
- Malformed JSON, bad sid (fails `^[A-Za-z0-9_-]{8,64}$`), body-k ≠ query-k → 400 `application/problem+json`; a 36-char UUID-shaped sid is VALID; unknown site key → 404 problem.
- A POST with `Content-Type: text/plain` and a JSON body behaves identically to `application/json`; responses to requests bearing an `Origin` header carry `Access-Control-Allow-Origin: *`; `OPTIONS /decide` → 204 with the preflight headers.
- Finalizer throwing → decision still returned (200) and error logged.
- Band thresholds read from configuration (override `Scoring:Bands:AllowMax=5` in a test → score 10 challenges).

## Testing

- `WebApplicationFactory<Program>` with fakes for `IScoringPipeline` (scripted scores + call capture), `ITurnstileVerifier` (scripted success/failure), `IVerdictFinalizer` (capture), Redis fake. Cover every acceptance criterion above plus: 429 interaction is API-01's (no test), unknown-k handled by middleware (no test here).
- One integration-flavored test wiring the real `StubVerdictFinalizer` against a Redis Testcontainer asserting grace-entry removal.

## Out of scope / guardrails

- **Rules only raise scores** — never subtract points for a passed challenge or any other reason; the challenge outcome enters ONLY as a CTX feature into re-scoring (RSK-01/RSK-07 own the math). No score arithmetic in this endpoint beyond threshold comparison.
- **<50 ms scoring budget**: both `ScoreAsync` calls are in-process (D3). Do not insert SQL, ClickHouse, or extra Redis work between scoring and response; finalization internals belong to API-06.
- **No enforcement here**: exclusion-queue writes, `EnforcementMode` (`AutoEnforce`/`ApprovalQueue`) handling, summary MERGEs are API-06/INT-02. The challenge band is always automatic in both modes (D21) — do not consult `EnforcementMode` in this endpoint.
- **Never leak scoring internals** to the caller — action string and Turnstile site key only.
- **Turnstile secret** stays server-side (INT-01); only the *site* key is returned.
- **Dapper not EF** for anything SQL (none expected here); `TenantId` never optional; Redis keys `t:{tid}:…`; no server-side Python/Node; no generic cross-engine query layer.
- Do not implement the Turnstile *widget* or SDK-side round-trip (SDK workstream) nor the verify HTTP call itself (INT-01).
