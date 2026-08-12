---
id: API-02
title: Click tracker endpoint /c
phase: 1
workstream: api
depends_on: [API-01, RSK-03, ANA-03, DAT-05]
size: L
spec_refs: ["§4 component 2", "§6.1", "§7 click_id_invalid", D11, D20, D22]
detail_level: full
---

# API-02: Click tracker endpoint /c

## Objective

Implement `GET /c` — the server-side click/redirect tracker that ad destination URLs point at. It logs HTTP-layer signals for every click (including bots that never execute JavaScript), validates/dedupes ad click IDs, registers a grace-period entry so the session gets a verdict even if no beacon ever arrives, emits a `ClickEvent` of kind `tracker`, and 302-redirects to the campaign's configured landing page. No scoring happens inline; server processing target is <10 ms.

## Spec context (self-contained)

- The tracker exists because a large share of click fraud never executes JS (spec §4 component 2, §6.1). It must capture everything scoring needs at the HTTP layer: IP, full ordered header-name list, User-Agent, Client Hints (`Sec-CH-UA*`), `Accept-Language`, referrer, click IDs (`gclid`/`fbclid`/`msclkid`/`ttclid`).
- **Click-ID validation (spec §6.1 step 1, §7 T1 `click_id_invalid`)**: the click ID is deduped via Redis `SETNX`. A **missing** click ID on a paid tracker click, or a **replayed** one (SETNX already claimed), sets `click_id_invalid = true`. This is a T1 rule-eligible signal consumed later by the rule engine — the tracker only records the fact.
- **Grace period (spec §6.1 step 4)**: if no beacon arrives within ~10 s, the session is scored on HTTP + velocity features alone (`has_js_beacon=0`). The tracker therefore registers a deadline entry that the verdict finalizer (API-06) consumes: `ZADD t:{tid}:grace <deadline> <sid>`.
- **Open-redirect guardrail**: the redirect destination is `Campaign.LandingUrl` from SQL config **only**. It must NEVER be read from a query parameter, header, or any request-supplied value. This is a hard security requirement stated in the acceptance criteria.
- Session ID (`sid`): 32 lowercase hex chars (16 random bytes). Passed to the landing page via `tg_sid` query param on the redirect (the SDK reads it there — the tracker domain's cookie is not visible to the landing-page domain) and also set as a `tg_sid` cookie on the tracker domain for repeat-click continuity.
- **Per-tenant retention (D20)**: `retention_days` is denormalized onto every analytics row at ingest from tenant config (30–180, default 90).
- Multi-tenancy: tenant comes from the `k` site-key query param via DAT-04's middleware; every Redis key is prefixed `t:{tenantId}:`; SQL access goes through tenant-stamped connections (RLS enforced).
- Scoring is <50 ms and happens elsewhere (API-05/API-06); the tracker does zero scoring — its budget is <10 ms because it sits directly in the ad-click redirect path.

## Prerequisites

- **API-01**: running host, middleware order, `Tracker` + `Retention` options sections in appsettings, commented registration point `// app.MapTrackerEndpoints();` in `Program.cs`. `ITenantContext`/`IClock` (FND-04) are registered.
- **RSK-03** (`TelemetryGuard.RiskEngine.Velocity`, see `doc/tasks/RSK-03-redis-velocity-store.md`) — click-id dedupe is FOLDED INTO the velocity call; there is no separate dedupe interface:
  ```csharp
  public interface IVelocityStore   // scoped; tenant comes from ambient ITenantContext (keys t:{tid}:…)
  {
      /// Single IBatch round trip: INCR minute bucket, PFADD ip→ua HLL, SET NX click id.
      /// Returns: true = clickId first seen; false = replay; null = clickId was null.
      Task<bool?> RecordClickAsync(string ip, string? userAgent, string? clickId, CancellationToken ct);
      // ... RecordSessionAsync / ReadAsync used by API-04 / RSK-07
  }
  ```
  Pass the RAW click-id value (RSK-03 keys it `t:{tid}:cid:{clickId}`); the click-id *type* is recorded separately in the click-context hash.
- **ANA-03**: `IEventSink` (from `TelemetryGuard.Analytics.Abstractions`, ANA-01) is registered and non-blocking (internally buffered/batched):
  ```csharp
  public interface IEventSink { ValueTask WriteBatchAsync(ReadOnlyMemory<ClickEvent> events, CancellationToken ct); }
  ```
  `ClickEvent` is ANA-01's DTO — consult that project for exact field names; it carries: tenant id, kind, timestamp, session id, site key, campaign id, ip, user agent, client hints, accept-language, referrer, ordered header names, retention days, and — for click ids — **dedicated `Gclid` and `Fbclid` string fields plus a `ClickIdInvalid` bool? flag ONLY**. There is no `ClickIdType`/`ClickIdValue` pair and no `msclkid`/`ttclid` field: the 66-column ClickHouse contract (ANA-02/ANA-03) mirrors this DTO 1:1 and is frozen — do NOT extend it.
- **DAT-05** (`TelemetryGuard.Data`, see `doc/tasks/DAT-05-config-repositories-dapper.md`): Dapper config repositories on tenant-stamped connections. The `/c` hot path uses the purpose-built single-seek method (campaign ids are **Guids**):
  ```csharp
  public sealed record CampaignRedirect(string LandingUrl, byte Status);   // Status: active gate
  public interface ICampaignRepository
  { Task<CampaignRedirect?> GetRedirectAsync(Guid campaignId, CancellationToken ct); /* + CRUD */ }
  public sealed record TenantRecord(Guid TenantId, string Name, byte Status,
      int RetentionDays, byte EnforcementMode, DateTime CreatedUtc);
  public interface ITenantRepository { Task<TenantRecord?> GetCurrentAsync(CancellationToken ct); /* + updates */ }
  ```
  DAT-05 explicitly designates `GetRedirectAsync` as the ONLY reader API-02 may use for the landing URL.

## Implementation steps

1. **Create `TelemetryGuard.Api/Endpoints/TrackerEndpoints.cs`** with:
   ```csharp
   public static class TrackerEndpoints
   {
       private static readonly string[] ClickIdParams = ["gclid", "fbclid", "msclkid", "ttclid"];

       public static IEndpointRouteBuilder MapTrackerEndpoints(this IEndpointRouteBuilder app)
       {
           app.MapGet("/c", HandleAsync);
           return app;
       }

       private static async Task<IResult> HandleAsync(
           HttpContext ctx, ITenantContext tenant, ICampaignRepository campaigns,
           ITenantRepository tenants, IMemoryCache cache, IVelocityStore velocity,
           IEventSink sink, IConnectionMultiplexer redis, IClock clock,
           IOptions<TrackerOptions> trackerOpts, IOptions<RetentionOptions> retentionOpts,
           CancellationToken ct) { ... }
   }
   ```
   Uncomment `app.MapTrackerEndpoints();` in `Program.cs`.

2. **Create options classes** `TelemetryGuard.Api/Options/TrackerOptions.cs` (`GraceSeconds` int = 10, `SessionCookieName` string = "tg_sid", `CampaignCacheSeconds` int = 60, `SessionTtlSeconds` int = 1800) and `Options/RetentionOptions.cs` (`DefaultDays` int = 90); bind in `Program.cs`: `builder.Services.Configure<TrackerOptions>(builder.Configuration.GetSection("Tracker"));` etc.

3. **Handler logic, in order** (`tid` below = `tenant.TenantId.Value.ToString("D")`):
   1. Read `cid` query param; missing/empty or not `Guid.TryParse`-able → `Results.NotFound()` (plain 404, no problem body — this endpoint is bot-facing, leak nothing).
   2. **Campaign lookup with 60 s cache**: `cache.GetOrCreateAsync($"campaign:{tid}:{cid}", e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(opts.CampaignCacheSeconds); return campaigns.GetRedirectAsync(campaignGuid, ct); })`. Null or inactive `Status` → 404. Negative results may be cached with the same TTL. (`GetRedirectAsync` is a single clustered-PK seek per DAT-05 — do not add another campaign read.)
   3. **Session id**: read cookie `tg_sid`; if it matches `^[0-9a-f]{32}$` reuse it, else generate: `Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()`.
   4. **Capture HTTP-layer signals**:
      - `ip` = `ctx.Connection.RemoteIpAddress?.ToString()` (already proxy-corrected by API-01's ForwardedHeaders).
      - `headerOrder` = `string.Join(",", ctx.Request.Headers.Select(h => h.Key))` (Kestrel preserves wire order).
      - `ua` = `User-Agent`; client hints = raw values of `Sec-CH-UA`, `Sec-CH-UA-Mobile`, `Sec-CH-UA-Platform` (join as `chUa`, `chMobile`, `chPlatform`); `acceptLanguage` = `Accept-Language`; `referrer` = `Referer`.
   5. **Click-ID extraction**: find the first present param among `ClickIdParams` → `(clickIdType, clickIdValue)`; both null when absent.
   6. **Velocity + dedupe in ONE call** (RSK-03 batches everything into a single Redis round trip):
      ```csharp
      bool? firstUse = await velocity.RecordClickAsync(ip, ua, clickIdValue, ct);
      bool clickIdInvalid = firstUse is null   // missing click id on a paid tracker click
                         || firstUse == false; // replayed click id
      ```
   7. **Click-context hash for feature extraction** (read at scoring time by RSK-07's `RedisSessionStateStore.GetAsync`, which batches it with API-04's session hash to build RSK-04's `RawSessionData` — the original HTTP request is long gone by then; these field names are a binding contract, and ingestion endpoints own ALL writes to this key). `HSET t:{tid}:click:{sid}` with fields, then `EXPIRE` to `Tracker:SessionTtlSeconds`:
      `kind=tracker, ts=<unix ms>, ip, ua, ch_ua, ch_mobile, ch_platform, accept_language, referrer, header_order, site_key, campaign_id=<cid>, click_id_type, click_id, click_id_invalid=<0|1>`
      (write empty string for absent values; use one `HashEntry[]` round trip via `IDatabase.HashSetAsync` + `KeyExpireAsync`).
   8. **Grace entry** (consumed by API-06): with `deadline = clock.UtcNow.ToUnixTimeSeconds() + opts.GraceSeconds`:
      ```csharp
      var db = redis.GetDatabase();
      await db.SortedSetAddAsync($"t:{tid}:grace", sid, deadline);
      await db.SetAddAsync("grace:tenants", tid);   // worker discovers tenants with pending entries
      ```
   9. **Emit ClickEvent** kind `tracker` (`EventKind.Tracker` from ANA-01): populate the ANA-01 `ClickEvent` with the captured fields, `RetentionDays` = `TenantRecord.RetentionDays` from `tenants.GetCurrentAsync` (cached in `IMemoryCache` 60 s under `tenantcfg:{tid}`; fall back to `Retention:DefaultDays`), `TimestampUtc` from `IClock` (must be `DateTimeKind.Utc`). **Click-id mapping**: `ClickEvent` has dedicated fields only for `Gclid` and `Fbclid` — populate whichever matches `clickIdType` and leave the other `""`; `msclkid`/`ttclid` values are captured ONLY in the Redis click-context hash (step 3.7 `click_id_type`/`click_id`) — do NOT add fields to `ClickEvent` or the ClickHouse schema (66 columns, frozen by ANA-02/ANA-03). `ClickIdInvalid` = the flag computed in step 3.6. Send: `await sink.WriteBatchAsync(new[] { evt }, ct);` — ANA-03 guarantees this only enqueues (non-blocking).
   10. **Redirect**: build target from `campaign.LandingUrl` ONLY:
       ```csharp
       var url = campaign.LandingUrl;
       url = QueryHelpers.AddQueryString(url, "tg_sid", sid);
       if (clickIdValue is not null)
           url = QueryHelpers.AddQueryString(url, clickIdType!, clickIdValue); // click-id passthrough
       ```
       Append `Set-Cookie`: name `tg_sid`, value `sid`, `HttpOnly=true, Secure=true, SameSite=Lax, Path=/, Max-Age=<SessionTtlSeconds>`. Add `Cache-Control: no-store`. Return `Results.Redirect(url, permanent: false)` (302).

3b. **Synthetic-bot labeling (SDK-06's contract, D18 — same mechanism as API-04 step 10)**: when config `Synthetic:Enabled` is `true` (Development only, default `false`) AND the request carries header `X-TG-Synthetic`, write once per session (guard `SET t:{tid}:synth:{sid} 1 EX 3600 NX`) a `LabelEvent(tenantId, sid, LabelValues.Fraud, LabelSources.SyntheticBot, clock.UtcNow.UtcDateTime)` via an optionally-resolved `ILabelSink` (`ctx.RequestServices.GetService<ILabelSink>()`). Ignored entirely outside Development config — this covers the generator's `--click-url` tracker-driven runs.

4. **Error behavior**: unknown site key is already rejected by DAT-04's middleware before the handler runs. Any exception inside the handler must not leave the ad click hanging — wrap steps 5–9 in try/catch: log, continue to the redirect (the redirect must succeed even if Redis or the sink hiccups; the click is lost to analytics, not to the advertiser).

5. **Performance**: no SQL on the hot path except the (cached) campaign + tenant-config lookups; all Redis operations are single-digit-ms local calls; the sink call is an in-memory enqueue. Do not add scoring, enrichment, GeoIP, or ClickHouse queries here.

## Files to create or modify

- `TelemetryGuard.Api/Endpoints/TrackerEndpoints.cs`
- `TelemetryGuard.Api/Options/TrackerOptions.cs`
- `TelemetryGuard.Api/Options/RetentionOptions.cs`
- `TelemetryGuard.Api/Program.cs` (options binding + `app.MapTrackerEndpoints();`)
- `tests/TelemetryGuard.Tests.Unit/Api/TrackerEndpointTests.cs`

## Acceptance criteria

- `GET /c?k=<valid>&cid=<valid>&gclid=abc123` returns **302**; `Location` host+path equal the campaign's `LandingUrl` with `tg_sid=<32hex>` and `gclid=abc123` appended; `Set-Cookie: tg_sid=...` present with `HttpOnly; Secure; SameSite=Lax`.
- **Open-redirect proof**: `GET /c?k=..&cid=..&redirect=https://evil.example&url=https://evil.example&dest=https://evil.example&next=https://evil.example` → `Location` still points at the campaign `LandingUrl`; `grep -rn "redirect\|url\|dest\|next" TelemetryGuard.Api/Endpoints/TrackerEndpoints.cs` shows no query parameter feeding the redirect target.
- Unknown or inactive `cid` → **404**; unknown `k` → rejected by tenant middleware (its status).
- Second request with the same `gclid` produces a `ClickEvent`/click-context with `click_id_invalid=1`; a request with no click-id param also records `click_id_invalid=1`; first-use valid click id records `0`.
- After a request: Redis contains `t:{tid}:grace` with the sid scored ~10 s in the future, `grace:tenants` contains the tenant id, and `t:{tid}:click:{sid}` hash has `kind=tracker`, non-empty `ip`, `header_order`, TTL ≈ 1800 s.
- Exactly one `ClickEvent` with kind `tracker` and `RetentionDays` between 30 and 180 reaches `IEventSink` per request.
- With Redis stopped, `/c` still returns 302 to the landing page.
- Repeat click with the existing `tg_sid` cookie reuses the sid (no new cookie value).
- With `Synthetic:Enabled=true` and header `X-TG-Synthetic: <runId>`, one request writes exactly one `LabelEvent(Fraud, SyntheticBot)` to `ILabelSink`; with the flag false (default) the header is ignored (no label). The redirect behaves identically either way.
- **Perf (env-gated, mirroring RSK-07's pattern)**: an `[RUN_PERF_TESTS=1]`-gated test (SkippableFact) issues 200 warm requests through `WebApplicationFactory` with all fakes in-memory and asserts p50 of the handler `Activity` duration < 10 ms; without the env var the test reports skipped. CI does not run it — the <10 ms budget is thereby mechanically checkable without being CI-flaky.

## Testing

- Unit tests with `WebApplicationFactory<Program>` and fakes: fake `ICampaignRepository` (fixed `CampaignRedirect`), fake `ITenantRepository`, fake `IVelocityStore` whose `RecordClickAsync` scripts `true`/`false`/`null` returns (in-memory claimed-set), capturing fake `IEventSink`, real `IMemoryCache`, Redis faked behind `IConnectionMultiplexer` mock or a Testcontainers Redis (preferred if the RSK-03 test helpers already provide one — check `tests/` for an existing fixture).
- Cases: happy path 302 + cookie + passthrough; open-redirect attempt; missing cid → 404; replayed gclid → invalid flag; missing gclid → invalid flag; Redis down → still 302; cookie reuse.
- Assert the exact `ClickEvent` field population by capturing the sink argument.

## Out of scope / guardrails

- **NEVER derive the redirect target from the request** — no `redirect=`, `url=`, `returnTo=` handling of any kind. `Campaign.LandingUrl` is the only source. This is the task's #1 guardrail.
- **No inline scoring or enrichment** — no `IScorer`, no GeoIP, no rule evaluation here. Scoring is API-05/API-06 within the separate <50 ms budget; the tracker's own budget is <10 ms.
- **Dapper only, via tenant-stamped connections** (DAT-03/DAT-05); no EF Core; never open a raw `SqlConnection`. RLS keyed on `SESSION_CONTEXT('TenantId')` is the primary isolation; keep explicit `TenantId` in WHERE clauses anyway (that's inside DAT-05's repos — do not bypass them).
- **`TenantId` is never optional** — every Redis key here is `t:{tid}:…`; never write an un-prefixed session or click key (exception: `grace:tenants`, which holds only tenant IDs — bookkeeping, not tenant data).
- **Missing signal ≠ zero**: absent headers/click IDs are recorded as absent (empty/null), not fabricated defaults; `click_id_invalid` is an explicit boolean fact, distinct from "no click id param exists for organic traffic" — record both the type and the flag so the rule engine can condition on paid vs organic.
- **No broker/queue** — events go straight to `IEventSink` (batched inserts, D12); do not add Kafka/streams.
- Do not write to ClickHouse or SQL summary tables directly — sink only (D7: no generic query layer; providers own their dialects).
