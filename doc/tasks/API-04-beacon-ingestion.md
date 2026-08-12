---
id: API-04
title: Beacon ingestion /i and /i/init
phase: 1
workstream: api
depends_on: [API-01, ANA-03, RSK-03, DAT-05]
size: L
spec_refs: ["§4 components 1+3", "§7", D2, D11, D20, D22, "§9 SDK"]
detail_level: full
---

# API-04: Beacon ingestion /i and /i/init

## Objective

Implement the beacon ingestion pair: `GET /i/init` (session bootstrap that hands the SDK a server nonce and a signed storage timestamp — required because `navigator.sendBeacon` cannot read responses) and `POST /i` (accepts SDK event batches as `text/plain` or `application/json`, validates them, and **incrementally aggregates** per-session behavioral statistics into a Redis hash so feature extraction (RSK-04) never needs raw event points). Periodically emits `ClickEvent` kind `beacon` snapshots to the analytics sink. Always responds 204.

## Spec context (self-contained)

- **Transport fact (drives the whole design)**: `navigator.sendBeacon()` is fire-and-forget — the page can NEVER read its response. Any server-issued value the SDK needs (nonce for beacon integrity, signed storage timestamp for storage-age) must come from a separate readable bootstrap request. That is `GET /i/init`.
- `sendBeacon` with a `text/plain` body is a "simple request": **no CORS preflight** is sent and none may be required. `POST /i` must therefore accept `text/plain` (and `application/json` for the fetch fallback) and never demand custom headers.
- **SDK ships raw events; the server computes derived features** (spec §7 "Architectural implication"). The snippet stays dumb; features evolve server-side without redeploying tenant pages. This task computes *running aggregates only* (Welford mean/variance of inter-event gaps, accumulated path length + endpoints for linearity, counters, flags) — RSK-04 turns them into `FraudFeatureVector` fields (`mean/std_inter_event_ms`, `mouse_path_linearity`, `first_interaction_delay_ms`, etc.).
- **Keystroke content is never captured** — timing metadata only (spec §3 non-goal, compliance line). A `key` event carries a timestamp and nothing about which key.
- **Beacon integrity (`beacon_integrity_ok`, T1 ≥85 when false)**: server-issued nonce must match, sequence numbers must be sane (monotonic from 0, gaps recorded), client clock skew bounded, and the SDK-05 envelope checksum (`c`, FNV-1a) must verify. Failures are *recorded* (the rule engine scores them); the endpoint still returns 204 — never give bots a validation oracle.
- **Storage age (`storage_age_zero_repeat`, T2)**: "cookie age means our own cookie's age". `/i/init` issues `storageTs` + HMAC signature; the SDK persists `{ts, sig}` in a first-party cookie (`tg_fp`) + localStorage and reports it back in the `fp` event; the server verifies the HMAC and computes the age. A fingerprint seen repeatedly with brand-new storage is a bot farm wiping state.
- Multi-tenancy — **hybrid resolution (DAT-04's documented design)**: `TenantResolutionMiddleware` deliberately passes `/i` and `/i/init` through UNRESOLVED (it must not buffer request bodies); these endpoints resolve the tenant themselves via DAT-04's `ITenantResolver.ResolveSiteKeyAsync(k)` and stamp the scoped `TenantContext` so downstream scoped services (velocity store, repositories) see it. Unknown site keys get a success-shaped drop (204 — anti-probing, per DAT-04). Every Redis key `t:{tid}:…` (D11). Retention: `retention_days` stamped on every analytics row from tenant config (D20).
- Body cap: 64 KB. Session hash TTL 1800 s. Nonce TTL 900 s. Max accepted clock skew 120 000 ms (all in the `Beacon` options section from API-01).
- D22 dashboards need per-site integration level: this endpoint records "last beacon seen" per site key (`t:{tid}:site:{siteKey}:lastbeacon`) which API-07 exposes.

## Prerequisites

- **API-01**: host + middleware; `Beacon`, `Tracker`, `Retention` appsettings sections; registration point `// app.MapBeaconEndpoints();`; `ITenantContext`, `IClock`, `IConnectionMultiplexer`, `IMemoryCache` registered.
- **ANA-03**: non-blocking `IEventSink.WriteBatchAsync(ReadOnlyMemory<ClickEvent>, CancellationToken)` registered; `ClickEvent` DTO in `TelemetryGuard.Analytics.Abstractions` (consult ANA-01 for exact field names — beacon snapshots map onto its behavioral/aggregate fields; unmapped extras stay in the Redis hash only).
- **RSK-03** (`TelemetryGuard.RiskEngine.Velocity`): scoped, tenant-ambient `IVelocityStore` with the session-capture method this task must call once per accepted beacon batch:
  ```csharp
  Task RecordSessionAsync(string ip, string? userAgent, string? visitorId, string sessionId,
                          bool storageAgeZero, CancellationToken ct);
  ```
  (`storageAgeZero` = verified `storage_age_sec == 0` — feeds the `fpz` zero-storage-age repeat counter for `storage_age_zero_repeat`.) Resolve it AFTER stamping the tenant context (it is scoped and reads the ambient tenant). Sequence tracking and session hashes in this task use plain `IDatabase` calls.
- **DAT-04**: `ITenantResolver` (`TelemetryGuard.Data.Tenancy`) returning `ResolvedTenant(Guid TenantId, string[] Scopes, string? SiteKey, byte? IntegrationMode)` or null for unknown keys, and FND-04's mutable `TenantContext` (`TelemetryGuard.Core.Tenancy`) with `Set(TenantId)` — both registered by `AddTelemetryGuardData()`/FND-04's extension. Consult those task files for exact members.
- **SDK-02/03/04/05 own the wire contract** — SDK-02's task file is the single canonical authority for the envelope shape, key order, `seq` semantics, and the `/i/init` response; SDK-03 defines the behavioral event vocabulary; SDK-04 the `fp` event; SDK-05 the trailing `c` checksum. This task implements the SERVER side of those contracts; SDK-06's zod schemas pin the same shapes. On any conflict, the SDK task files win — the restatement below must not diverge from them.

## Wire contract (canonical authority: SDK-02/03/04/05 — this endpoint implements the server side)

### GET /i/init?k={siteKey}&sid={sid}

Response `200 application/json`:
```json
{ "nonce": "a1b2c3d4e5f60718293a4b5c6d7e8f90", "storageTs": 1765432100123, "storageSig": "hex-hmac-sha256" }
```
- `nonce`: 16 random bytes, lowercase hex; stored `SETEX t:{tid}:nonce:{sid} <Beacon:NonceTtlSeconds> <nonce>`.
- `storageTs`: server unix ms (`IClock`); `storageSig` = lowercase-hex `HMACSHA256(key: UTF8(Beacon:HmacSecret), data: UTF8($"{tid}:{storageTs}"))` where `tid` = tenant Guid "D" lowercase. NOT sid-bound — storage outlives sessions.
- **No cookies, no credentialed CORS.** SDK-02 calls this endpoint with `fetch(..., {credentials:'omit'})`, so any `Set-Cookie` issued here would never be stored or replayed — do not issue one. Persistence of the `{storageTs, storageSig}` pair is ENTIRELY client-side: SDK-04 writes its own first-party `tg_fp` cookie + localStorage on the landing-page domain and never overwrites an existing valid pair, so age accumulates without any server echo. This endpoint mints a fresh pair on every call; the server's job is HMAC *verification* of whatever pair the `fp` event later reports.
- CORS: plain `Access-Control-Allow-Origin: *` (no credentials are used, so `*` is correct and simplest). No preflight handling needed.
- `sid` must match `^[A-Za-z0-9_-]{8,64}$` → else 204 empty (drop silently). This accepts BOTH sid shapes that exist by design: the 32-lowercase-hex ids minted by the `/c` tracker (API-02) and the `crypto.randomUUID()` fallback (36 chars incl. dashes) SDK-02 generates for organic sessions that never passed through the tracker (the §6.2 lead-form case). API-05's `/decide` uses the same pattern.

### POST /i?k={siteKey}

Body (UTF-8 JSON regardless of declared content type — `text/plain`, `application/json`, or absent), exactly as SDK-02/SDK-05 serialize it (single line, canonical key order `k, sid, seq, nonce, sent_at, events, c`):
```json
{
  "k": "site_abc123",
  "sid": "9f8e7d6c5b4a39281706f5e4d3c2b1a0",
  "seq": 0,
  "nonce": "a1b2c3d4e5f60718293a4b5c6d7e8f90",
  "sent_at": 1765432100123,
  "events": [ { "e": "pv", "t": 12 } ],
  "c": "a1b2c3d4"
}
```
- `seq` starts at **0** and increments once per envelope send attempt (SDK-02).
- Event timestamps: every event's `t` is **integer ms since `performance.timeOrigin`** (SDK-02 step 10) — NOT epoch ms; the envelope's `sent_at` is client epoch ms. Absolute event time when needed ≈ `serverReceiveMs − (sent_at − t)`; the aggregation below mostly uses `t` deltas, which need no conversion.
- `"c"`: 8 lowercase hex chars, FNV-1a 32-bit checksum appended by SDK-05 as the FINAL envelope field, computed over the serialized envelope WITHOUT the `,"c":"…"` suffix. Optional until SDK-05 lands (see step 6 integrity checks).

Event union (parse as `JsonElement`, switch on discriminator `"e"`; unknown types are counted and skipped, never rejected). This is SDK-02/03/04's vocabulary verbatim:

| e | payload (beyond `e`, `t`) | meaning / powers |
|---|---------------------------|------------------|
| `pv` | — | pageview, first-envelope marker (SDK-02); counted into `n_pv` → `pages_viewed` |
| `pm` | `s: [[t,x,y],…]` ≤200 samples, ≥50 ms apart | batched pointer-move samples → Welford inter-sample stats + path-linearity ingredients |
| `pd` | `tr:0\|1`, `sn:int`, `pt:'m'\|'t'\|'p'\|'u'` | pointerdown; `tr=0` ⇒ pointer_untrusted; `pt` counters feed input_modality_mismatch |
| `cl` | `tr`, `sn`, `pt` | click; `sn` = the event's own timeStamp (ms since navigation start) — click-before-render ingredient |
| `sc` | `y:int` | scroll position sample |
| `ky` | `d:0\|1` — **exactly the keys {e,t,d}** | keystroke TIMING ONLY; no key-identity fields exist in the contract |
| `ff` / `fb` | `fh:hex8`, `ft:string` | form field focus/blur (hashed field identity only) |
| `fs` | `fh` | form submit |
| `pa` | `fk:'identity'\|'other'` | paste classification |
| `af` | `fh` | autofill heuristic fired |
| `hp` | `kind:'focus'\|'input'\|'submit_filled'` | honeypot interaction — NO field name on the wire (SDK-03's privacy design: hashed/typed identities only) |
| `fi` | `it:string` | first trusted interaction; its `t` IS the `first_interaction_delay_ms` raw material |
| `fp` | `vid`, `conf`, `scr:[w,h,colorDepth,dpr]`, `tz`, `langs:[…]`, `canvasBlocked`, `touch`, `mob`, `wd`, `botd:{bot,kind?}`, `storage:{ck:{present,ts?,sig?,ageMs?}, ls:{…}, fresh, cookiesDisabled}` | fingerprint/Botd payload, once per session (SDK-04) |

**Deleted from earlier drafts, deliberately**: there is no `nav`/`mm`/`click`/`key`/`scroll`/`touch`/`vis`/`form` union, and no client-computed `br`, `fill_ms`, `modality`, `headless`, or `emulator` fields. §7's rule is "SDK ships raw events; the engine derives" — click-before-render, form fill time, modality mismatch, and headless/emulator tiers are derived server-side (aggregator ingredients in step 6 + RSK-04 math), never trusted from the client.

Response: **204 No Content** in every accepted/dropped case; **413** only when the body exceeds 64 KB (`Beacon:MaxBodyBytes`). No CORS needed (simple request; SDK never reads the response).

## Implementation steps

1. **Create `TelemetryGuard.Api/Options/BeaconOptions.cs`** — `HmacSecret` (string), `MaxBodyBytes` (int, 65536), `NonceTtlSeconds` (int, 900), `MaxClockSkewMs` (long, 120000), `SessionTtlSeconds` (int, 1800), `SinkEveryNthBeacon` (int, 10); bind from section `Beacon` in `Program.cs` with **startup validation**: fail startup (`.Validate(o => !string.IsNullOrEmpty(o.HmacSecret) && o.HmacSecret.Length >= 32, ...).ValidateOnStart()`) when `HmacSecret` is null/empty or shorter than 32 characters — an empty key would silently HMAC with nothing and make `storage_sig_ok` meaningless. The dev secret (≥32 chars) ships in `appsettings.Development.json` only; if API-01's base `appsettings.json` still carries a short placeholder, blank it there and move the value.

2. **Create `TelemetryGuard.Api/Endpoints/BeaconEndpoints.cs`** with `MapBeaconEndpoints(this IEndpointRouteBuilder)` mapping both routes; uncomment registration in `Program.cs`.

3. **Tenant resolution (BOTH handlers, first thing)** — the middleware passed these routes through unresolved:
   ```csharp
   var k = ctx.Request.Query["k"].ToString();
   var resolved = string.IsNullOrEmpty(k) ? null
       : await resolver.ResolveSiteKeyAsync(k, ct);          // DAT-04 ITenantResolver (cached)
   if (resolved is null) return Results.NoContent();          // success-shaped drop, anti-probing
   tenantContext.Set(new TenantId(resolved.TenantId));        // FND-04 mutable TenantContext
   var tid = resolved.TenantId.ToString("D");
   ```
   (For `/i/init` an unknown key also returns a bare 204 — the SDK treats a non-200 bootstrap as "disabled".)

4. **`/i/init` handler**: after resolution, implement the contract above. Validate `sid` (`^[A-Za-z0-9_-]{8,64}$`); generate/persist nonce; mint a fresh storage ts+sig; add `Access-Control-Allow-Origin: *`; return the JSON. **No `Set-Cookie`, no Origin echo, no `Allow-Credentials`** — client-side persistence of the pair is SDK-04's job (first-party storage on the landing-page domain).

5. **`/i` handler skeleton**:
   1. Cap body: `ctx.Features.Get<IHttpMaxRequestBodySizeFeature>()` → if `!IsReadOnly`, set `MaxRequestBodySize = opts.MaxBodyBytes`; catch `BadHttpRequestException` → 413. Also short-circuit 413 when `Content-Length > MaxBodyBytes`.
   2. Read the whole body as string, `JsonSerializer.Deserialize<JsonElement>`; any parse failure → increment a metric/log, return 204 (drop).
   3. Validate: `sid` matches `^[A-Za-z0-9_-]{8,64}$` (both tracker-hex and randomUUID shapes — see wire contract); body `k` (when present) equals query `k`; `seq >= 0` (SDK-02 starts at 0 — the first envelope of every session is `seq:0` and MUST be accepted); `events` is an array. Failures → drop, 204.
   4. Session key `sessKey = $"t:{tid}:sess:{sid}"`.

6. **Load-modify-store aggregation** (single-instance MVP: one `HashGetAllAsync(sessKey)` in, process all events in memory, one `HashSetAsync` + `KeyExpireAsync(sessKey, SessionTtlSeconds)` out; beacons for one session arrive serially from one browser, so read-modify-write is acceptable — note this assumption in a code comment; a Lua script is the multi-instance upgrade path, out of scope):

   **Integrity checks** (recorded, never rejected):
   - `checksum_ok`: only when the raw body ends in a `"c"` field — locate the LAST occurrence of `,"c":"` in the raw body string; `prefix = body[0..idx) + "}"`; recompute FNV-1a 32-bit (offset basis `0x811c9dc5`, prime `0x01000193`) over the UTF-8 bytes of `prefix`; ordinal-compare to the embedded 8-hex value → store `checksum_ok` 1/0. SDK-05 step 2 owns this algorithm — mirror it exactly in C#. When no `c` field is present (pre-SDK-05 bundles), leave the hash field ABSENT (missing ≠ zero) and do not count a failure.
   - `nonce_ok`: `GET t:{tid}:nonce:{sid}` equals body `nonce` → 1 else 0.
   - Sequence: hash field `last_seq`; when ABSENT treat as **-1** (so the SDK's first envelope, `seq:0`, is `last_seq + 1` — in sequence, not a replay). `seq <= last_seq` → replay: increment `seq_replays`, drop the batch's events but still update `last_beacon_ts`, respond 204. `seq > last_seq + 1` → `seq_gaps += seq - last_seq - 1`. Store `last_seq = seq`.
   - Skew: `skew = |clock.UtcNow.ToUnixTimeMilliseconds() - sent_at|`; keep `skew_max_ms = max(existing, skew)`; `skew_bad = 1` when over `MaxClockSkewMs`.
   - `integrity_fails` is a STORED hash counter (never recomputed at read time), incremented **at most once per POST for each condition observed in that POST**: nonce mismatch +1, seq replay +1, skew over limit +1, checksum mismatch +1. RSK-04 derives `beacon_integrity_ok = (integrity_fails == 0)`.

   **Per-event aggregation** — hash fields (all numeric fields stored as invariant-culture strings):

   | field(s) | update rule |
   |----------|-------------|
   | `n_beacons` | +1 per POST |
   | `n_pv` | +1 per `pv` event — the `pages_viewed` source that RSK-07's prefetch maps to RSK-04's `BeaconData.PagesViewed` (MVP: one `pv` per full page load; SPA soft navigations count only if a later SDK task re-emits `pv` on History API navigation) |
   | `page_url` | first non-empty `Referer` REQUEST HEADER value (first wins) — URL/referrer are HTTP-layer signals; no event carries them (SDK-02 step 9) |
   | `mm_n`, `mm_prev_ts`, `mm_mean_ms`, `mm_m2` | Welford over inter-SAMPLE gaps of `pm` batches: iterate each sample `[t,x,y]` in `s`; for each with a prior sample timestamp (`mm_prev_ts`, carried across batches and POSTs): `delta = t - prev_t; n++; d = delta - mean; mean += d / n; m2 += d * (delta - mean);` (std later = `sqrt(m2/(n-1))`, computed by RSK-04) |
   | `mm_path_len`, `mm_first_x`, `mm_first_y`, `mm_prev_x`, `mm_prev_y` | `path_len += sqrt(dx²+dy²)` from the previous `pm` sample point; first point stored once (linearity later = straightline(first→prev)/path_len, RSK-04) |
   | `n_click`, `n_key`, `n_scroll`, `n_events_total`, `n_unknown` | counters: `cl` events; `ky` events with `d==1` (keydowns); `sc` events; all events; unknown `e` values |
   | `pt_mouse`, `pt_touch`, `pt_pen` | counters over `pd` events by `pt` (`'m'`/`'t'`/`'p'`) — raw modality material; RSK-04 derives `input_modality_mismatch` (there is no client `modality` field) |
   | `hp_touched` | =1 on any `hp` event (any `kind`) |
   | `pointer_untrusted` | =1 on any `pd` or `cl` with `tr == 0` |
   | `cl_min_sn` | min `sn` over `cl` events — the click-before-render INGREDIENT (RSK-04 owns the threshold against render/paint timing; no client `br` flag exists) |
   | `first_interaction_delay_ms` | = `t` of the first `fi` event (its `t` is ms since `performance.timeOrigin`, i.e. already the delay — SDK-03 step 6); first wins |
   | `visitor_id`, `conf`, `webdriver`, `botd_bot`, `botd_kind`, `screen_w`, `screen_h`, `color_depth`, `dpr`, `canvas_blocked`, `touch`, `is_mobile`, `tz`, `langs`, `cookies_disabled` | from the `fp` event: `vid`, `conf`, `wd` (0/1), `botd.bot` (0/1), `botd.kind`, `scr[0]..scr[3]`, `canvasBlocked`, `touch`, `mob`, `tz`, `langs` joined with `,`, `storage.cookiesDisabled` (all bools as 0/1). There are NO `headless`/`emulator` wire fields — RSK-04 derives those tiers from `wd`/`botd_*`. `scr` carries no viewport dims; the viewport clauses of `screen_res_anomalous` have no data source (RSK-04's concern, not this task's) |
   | `storage_age_sec`, `storage_sig_ok` | from `fp.storage`: take the `ck` pair when `ck.present` with `ts`+`sig`, else the `ls` pair; verify `sig == HMACSHA256(secret, $"{tid}:{ts}")` (constant-time compare) → `storage_sig_ok` 0/1; `storage_age_sec = max(0, (nowMs - ts)/1000)` only when sig ok, else field absent (missing ≠ zero!). Client-computed `ageMs` is convenience data — never trust it |
   | `form_started`, `form_submitted`, `form_fill_ms`, `autofill`, `paste_identity` | `ff` → `form_started=1` + store `first_ff_ts` (first wins); `fs` → `form_submitted=1` and, when `first_ff_ts` is known, `form_fill_ms = fs.t - first_ff_ts` (server-derived — there is no client `fill_ms`); `af` → `autofill=1`; `pa` with `fk=='identity'` → `paste_identity=1` |
   | `has_beacon` | =1 (existence of the hash itself also signals it) |
   | `last_beacon_ts` | server unix ms |

7. **Velocity session capture (RSK-03)**: once per accepted batch, after the tenant context is stamped, resolve `IVelocityStore` from `ctx.RequestServices` and call
   `await velocity.RecordSessionAsync(ip, ua, visitorId /* null until an fp event arrived */, sid, storageAgeZero: storageSigOk && storageAgeSec == 0, ct);`

8. **Site liveness for D22 / API-07**: `SET t:{tid}:site:{siteKey}:lastbeacon <unix seconds> EX 604800` on every accepted POST (`siteKey` = query `k`).

9. **Periodic sink snapshot**: when `n_beacons == 1`, OR the batch contained an `fp` event, OR an `fs` (form submit), OR `n_beacons % SinkEveryNthBeacon == 0` → build a `ClickEvent` kind `beacon` from the updated aggregates (map onto ANA-01's fields; include sid, site key, ip = `ctx.Connection.RemoteIpAddress`, ua, `RetentionDays` from cached tenant config as in API-02 step 3.9) and `await sink.WriteBatchAsync(new[]{evt}, ct)`.

10. **Synthetic-bot labeling (SDK-06's contract, D18 "guaranteed positives")**: bind `Synthetic:Enabled` (bool, default `false`; set `true` only in `appsettings.Development.json`). When it is true AND the request carries header `X-TG-Synthetic`, write — once per session, guarded by `SET t:{tid}:synth:{sid} 1 EX 3600 NX` — a label via ANA-01's `ILabelSink` (resolve optionally: `ctx.RequestServices.GetService<ILabelSink>()`, skip silently when unregistered): `new LabelEvent(tenantId, sid, LabelValues.Fraud, LabelSources.SyntheticBot, clock.UtcNow.UtcDateTime)`. In any non-Development config the flag stays false and the header is IGNORED (never trusted from the wild). API-02 honors the same header on `/c` for tracker-driven generator runs. This header + `tg_labels source='synthetic_bot'` path (defined here and in SDK-06) is the ONE synthetic-labeling mechanism — RSK-08's LabelBuilder reads these rows; there is no dedicated-test-tenant-id scheme.

11. **Respond 204.** Wrap everything after body-read in try/catch (log; still 204).

12. **Helper class** `TelemetryGuard.Api/Services/SessionAggregator.cs`: pure static function `Apply(Dictionary<string,string> hash, JsonElement body, string rawBody, long nowMs, BeaconOptions opts, string tid)` returning the mutated field dictionary — keeps the Welford/path/checksum math unit-testable without Redis.

13. **Downstream contract note**: the session hash of step 6 is the AUTHORITATIVE per-session behavioral state. The exact reader is RSK-07's `RedisSessionStateStore.GetAsync`, which prefetches THIS hash (together with API-02's click-context hash `t:{tid}:click:{sid}` and the velocity snapshot) in one batch to build RSK-04's `RawSessionData` — it must consume the step 6 field names verbatim. Ingestion endpoints (API-02/API-04) own ALL writes to these keys; the scoring side writes only the `challenge` hash field (API-05 via `SetChallengeOutcomeAsync`) and never stores raw beacon/event lists (this task's "no raw event points in Redis" guardrail binds the reader too). RSK-04's file describes raw `InputEvent` lists; since this task deliberately stores only running aggregates (per its mandate), RSK-07's prefetch derives the final statistics from THESE fields (`std = sqrt(mm_m2 / (mm_n - 1))` when `mm_n >= 2`; `linearity = Dist(first, prev) / mm_path_len` when point count ≥ 5 and `mm_path_len > 0`; else NaN — same NaN gates RSK-04 specifies), and maps `n_pv` → `PagesViewed`. Do not change hash field names without updating RSK-07.

## Files to create or modify

- `TelemetryGuard.Api/Options/BeaconOptions.cs`
- `TelemetryGuard.Api/Endpoints/BeaconEndpoints.cs`
- `TelemetryGuard.Api/Services/SessionAggregator.cs`
- `TelemetryGuard.Api/Program.cs` (bind `Beacon` options; `app.MapBeaconEndpoints();`)
- `tests/TelemetryGuard.Tests.Unit/Api/SessionAggregatorTests.cs`
- `tests/TelemetryGuard.Tests.Unit/Api/BeaconEndpointTests.cs`

## Acceptance criteria

- `GET /i/init?k=<valid>&sid=<32hex>` → 200 JSON with `nonce` (32 hex), `storageTs` (ms), `storageSig` (64 hex); response carries `Access-Control-Allow-Origin: *`; **NO `Set-Cookie` header of any kind** (grep the handler: no `Cookies.Append`, no `Allow-Credentials`).
- Both sid shapes accepted on `/i/init` AND `/i`: `sid=9f8e7d6c5b4a39281706f5e4d3c2b1a0` (tracker 32-hex) and `sid=e58ed763-928c-4155-bee9-fdbaaadc15f3` (SDK `crypto.randomUUID()` fallback); `sid=ab` (too short) or one containing `%`/`.` → 204 drop.
- `POST /i?k=<valid>` with `Content-Type: text/plain` and a valid JSON body → **204**, no CORS preflight required (verify no `Access-Control-Request-*` handling exists), session hash `t:{tid}:sess:{sid}` populated with `mm_n`, `mm_mean_ms`, `mm_m2`, `mm_path_len`, counters, `last_seq`, TTL ≈ 1800 s.
- Same POST with `Content-Type: application/json` behaves identically.
- Welford correctness: for a `pm` batch with samples at t 0,100,300 (gaps 100,200): `mm_n=2`, `mm_mean_ms=150`, `mm_m2=5000` (asserted in `SessionAggregatorTests`).
- Path: `pm` samples (0,0)→(3,4)→(6,8) give `mm_path_len=10`, first (0,0), prev (6,8).
- First envelope with `seq:0` on a fresh session is ACCEPTED and aggregated (`last_seq` absent ⇒ treated as -1). `seq` replay (same seq twice) → second batch's events NOT aggregated, `seq_replays=1`, still 204. Gap (`seq` 0 then 3) → `seq_gaps=2`.
- Wrong/missing nonce → `nonce_ok=0`, 204. `sent_at` skewed by >120 s → `skew_bad=1`.
- Checksum: a body sealed exactly like SDK-05 (`…,"c":"<fnv1a-8hex>"}`) → `checksum_ok=1`; the same body with one flipped byte → `checksum_ok=0` and `integrity_fails` incremented; a body with no `c` field → no `checksum_ok` hash field and no `integrity_fails` increment.
- `integrity_fails` is a stored counter: one POST failing nonce AND checksum increments it by exactly 2; a subsequent clean POST leaves it unchanged.
- Two POSTs each carrying one `pv` event → `n_pv=2`.
- Body of 70 000 bytes → **413**; body of 60 000 → accepted.
- Valid `fp.storage.ck` sig → `storage_sig_ok=1` and plausible `storage_age_sec`; tampered sig → `storage_sig_ok=0` and **no** `storage_age_sec` field (missing ≠ zero); cookie pair absent but valid `ls` pair present → verified from `ls`.
- `ky` events carry no key-value data end-to-end (grep the contract structs/parsing: no field for key codes exists; parser touches only `{e,t,d}`).
- `t:{tid}:site:{k}:lastbeacon` set after a POST.
- First beacon and every 10th produce exactly one kind-`beacon` `ClickEvent` at the sink.
- With `Synthetic:Enabled=true` and header `X-TG-Synthetic: <runId>`, accepted POSTs write exactly ONE `LabelEvent(LabelValues.Fraud, LabelSources.SyntheticBot)` per session to `ILabelSink`; with the flag false (the default), the header is ignored and no label is written.
- Invalid sid / unparsable JSON / mismatched body-vs-query `k` → 204, nothing written except logs/metrics.
- Unknown site key on `/i` or `/i/init` → bare 204 (success-shaped drop; the DAT-04 middleware passed the route through and the endpoint's own `ITenantResolver` lookup missed), indistinguishable in status from an accepted beacon.
- `RecordSessionAsync` called once per accepted batch with `storageAgeZero=true` only when the storage sig verified AND age is 0.

## Testing

- **`SessionAggregatorTests`** (pure, no I/O): Welford numbers above (from `pm` sample batches, including gap continuity across two batches); path length; first-interaction delay (`fi` with `t=450` → `first_interaction_delay_ms=450`); integrity counters incl. FNV-1a checksum (valid / tampered / absent-`c`); fp-field mapping (`scr` array → `screen_w/screen_h/color_depth/dpr`; no `headless`/`emulator`/`modality` fields anywhere); storage HMAC verify (ck valid, ck tampered, ck absent + ls valid, both absent); `n_pv` counting; unknown event type counted in `n_unknown`.
- **`BeaconEndpointTests`** (`WebApplicationFactory<Program>` + Redis Testcontainer or in-memory `IDatabase` fake, capturing `IEventSink`/`ILabelSink` fakes): both content types; both sid shapes; 413 cap; seq 0 acceptance + replay/gap handling across sequential POSTs; nonce round-trip with a real `/i/init` call first; no-Set-Cookie assertion; lastbeacon key; sink cadence (1st, fp-bearing, 10th); `X-TG-Synthetic` × `Synthetic:Enabled` matrix (on/on → one label; on/off, off/on → none).

## Out of scope / guardrails

- **Never store raw event points** in Redis or SQL — running aggregates only; raw-ish snapshots go to the analytics sink as `ClickEvent`s. RSK-04 must be able to extract every behavioral feature from the hash alone.
- **Keystroke timing only** — rejecting is not enough: the parser must have no code path that reads key values, key codes, or field contents (compliance line, spec §3).
- **Missing ≠ zero (NaN semantics)**: absent signals leave hash fields absent. Do not initialize behavioral fields to 0. `has_js_beacon`/`beacon_integrity_ok` are derived downstream from presence + `integrity_fails`.
- **No scoring, no rules, no enrichment** here — <50 ms scoring budget belongs to RSK-07; this endpoint only aggregates. Do not compute `mouse_path_linearity` or `std_inter_event_ms` finals here (RSK-04 owns feature math; this task stores the ingredients).
- **Never require CORS preflight on `/i`** — no custom-header requirements, no auth headers; site key rides the query string. Do not "harden" it with an Origin allowlist (sendBeacon is no-cors; you'd break ingestion, not bots).
- **204 always** (except 413) — validation failures must be silent to the client; bots get no oracle.
- **Dapper not EF** for the tenant-config read (via DAT-05 repo, tenant-stamped/RLS connection); `TenantId` never optional; every key `t:{tid}:…`; no server-side Python/Node; no broker (D12); sink enqueue only — never block the response on ClickHouse.
