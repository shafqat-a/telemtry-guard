---
id: API-03
title: Pixel endpoint /p.gif
phase: 1
workstream: api
depends_on: [API-01, ANA-03, DAT-05]
size: S
spec_refs: [D22, "§6.1 step 4", "§7 null semantics"]
detail_level: full
---

# API-03: Pixel endpoint /p.gif

## Objective

Implement `GET /p.gif` — web-pixel mode for tenants who cannot add script tags (D22). Serves a constant 43-byte transparent GIF, captures the same HTTP-layer signals as the tracker (minus click IDs), increments velocity counters, maintains a session id, emits a `ClickEvent` of kind `pixel`, and registers a grace entry so pixel-only sites still receive verdicts (scored on HTTP + velocity features; all SDK features stay `NaN`, `has_js_beacon` stays 0).

## Spec context (self-contained)

- D22: tenants integrate via **(a)** the JS snippet or **(b)** a web pixel `<img src="https://px.../p.gif?k=…">`. Pixel mode degrades gracefully **by existing design**: all SDK-derived features are `NaN` (never 0 — spec §7 null semantics), scoring proceeds on network + velocity tiers, and `has_js_beacon` is itself a feature that stays 0. Nothing in this endpoint fabricates SDK signals.
- Pixel-only sessions must still be finalized: register the same grace-period entry the tracker does (`ZADD t:{tid}:grace`), so API-06 scores them after ~10 s.
- No click IDs: `<img>` tags on landing pages don't carry `gclid` — click-ID capture/dedupe belongs to `/c` (API-02) only. Do not record `click_id_invalid` from the pixel (absence here proves nothing).
- The response must never be cached by browsers or proxies (each page view must hit the server): `Cache-Control: no-store`.
- Multi-tenancy: tenant resolved from `k` by DAT-04 middleware; all Redis keys `t:{tid}:…`.

## Prerequisites

- **API-01**: host, middleware, `Tracker`/`Retention` options, registration point `// app.MapPixelEndpoints();`. `ITenantContext`, `IClock`, `IConnectionMultiplexer` registered.
- **ANA-03**: registered non-blocking `IEventSink` (`ValueTask WriteBatchAsync(ReadOnlyMemory<ClickEvent>, CancellationToken)`) and the `ClickEvent` DTO from `TelemetryGuard.Analytics.Abstractions` (ANA-01).
- If API-02 is already merged, reuse its `TrackerOptions`/`RetentionOptions` and tenant-config-cache helper; otherwise create the options classes exactly as specified in API-02's task file (same section names) so the two tasks converge.

## Implementation steps

1. **Create `TelemetryGuard.Api/Endpoints/PixelEndpoints.cs`**:
   ```csharp
   public static class PixelEndpoints
   {
       // 43-byte transparent 1x1 GIF89a
       private static readonly byte[] Gif =
       {
           0x47,0x49,0x46,0x38,0x39,0x61,             // "GIF89a"
           0x01,0x00,0x01,0x00,0x80,0x00,0x00,        // logical screen 1x1, GCT of 2
           0x00,0x00,0x00,0xFF,0xFF,0xFF,             // palette: black, white
           0x21,0xF9,0x04,0x01,0x00,0x00,0x00,0x00,   // GCE: transparency on index 0
           0x2C,0x00,0x00,0x00,0x00,0x01,0x00,0x01,0x00,0x00, // image descriptor
           0x02,0x02,0x44,0x01,0x00,                  // LZW min code size + data
           0x3B                                        // trailer
       };

       public static IEndpointRouteBuilder MapPixelEndpoints(this IEndpointRouteBuilder app)
       {
           app.MapGet("/p.gif", HandleAsync);
           return app;
       }
   }
   ```
   Uncomment `app.MapPixelEndpoints();` in `Program.cs`.

2. **Handler** (`HandleAsync(HttpContext ctx, ITenantContext tenant, IEventSink sink, IConnectionMultiplexer redis, IClock clock, IMemoryCache cache, IOptions<TrackerOptions> opts, IOptions<RetentionOptions> retention, CancellationToken ct)`), with `tid = tenant.TenantId.Value.ToString("D")`:
   1. **sid**: `tg_sid` query param if it matches `^[0-9a-f]{32}$`; else `tg_sid` cookie if valid; else generate 16 random bytes → 32 lowercase hex, and append `Set-Cookie: tg_sid=<sid>; HttpOnly; Secure; SameSite=Lax; Path=/; Max-Age=<Tracker:SessionTtlSeconds>`.
   2. **Capture HTTP signals** exactly as API-02 step 3.4: ip (post-ForwardedHeaders), ordered header names joined with `,`, UA, `Sec-CH-UA`/`-Mobile`/`-Platform`, `Accept-Language`, `Referer`. **No click-id params are read.**
   3. **Velocity**: if RSK-03's `IVelocityStore` (`TelemetryGuard.RiskEngine.Velocity` — scoped, tenant-ambient) is registered in DI, call `RecordClickAsync(ip, ua, clickId: null, ct)` (returns `null` — pixels carry no click ids; that is correct, not an error); resolve it optionally (`ctx.RequestServices.GetService<IVelocityStore>()`) so this task does not hard-depend on RSK-03 being merged.
   4. **Click-context hash** (same contract as API-02 step 3.7, consumed by RSK-04): `HSET t:{tid}:click:{sid}` with `kind=pixel, ts=<unix ms>, ip, ua, ch_ua, ch_mobile, ch_platform, accept_language, referrer, header_order, site_key`; no click-id fields. `EXPIRE` = `Tracker:SessionTtlSeconds`. If the hash already exists (tracker hit came first), only fill fields that are absent — use `HSETNX` per field or skip when `kind` field already present; never overwrite `kind=tracker` with `pixel`.
   5. **Grace entry** (so pixel-only sites get verdicts): `ZADD t:{tid}:grace <nowUnixSeconds + Tracker:GraceSeconds> <sid>` with `NX` semantics (`SortedSetAddAsync(key, sid, deadline, When.NotExists)`) so a tracker-created entry's deadline is not reset; plus `SADD grace:tenants <tid>`.
   6. **ClickEvent kind `pixel`** with all captured fields, `RetentionDays` from cached tenant config (same `tenantcfg:{tid}` cache as API-02, default `Retention:DefaultDays`); `await sink.WriteBatchAsync(new[] { evt }, ct);`.
   7. **Respond**: status 200, `Content-Type: image/gif`, headers `Cache-Control: no-store, no-cache, must-revalidate`, `Pragma: no-cache`, `Expires: 0`; body = the 43 bytes. Wrap steps 3–6 in try/catch: the GIF must always be served even if Redis/sink fail.

## Files to create or modify

- `TelemetryGuard.Api/Endpoints/PixelEndpoints.cs`
- `TelemetryGuard.Api/Program.cs` (`app.MapPixelEndpoints();`)
- `tests/TelemetryGuard.Tests.Unit/Api/PixelEndpointTests.cs`

## Acceptance criteria

- `curl -s http://localhost:<port>/p.gif?k=<valid> | wc -c` → exactly **43**; `Content-Type: image/gif`; `Cache-Control` contains `no-store`; body bytes begin `GIF89a` and end `0x3B`.
- Response includes `Set-Cookie: tg_sid=` when no sid supplied; reuses the sid when `?sid`/cookie present (no new cookie).
- One `ClickEvent` of kind `pixel` per request reaches the sink; it contains **no** click-id fields and no SDK/behavioral fields (left null — NaN semantics happen downstream).
- Redis after a request: `t:{tid}:grace` contains sid with deadline ≈ now+10 s; `grace:tenants` contains tid; `t:{tid}:click:{sid}` has `kind=pixel` — unless a tracker hit preceded it, in which case `kind` remains `tracker` and the grace deadline is unchanged.
- With Redis stopped, the GIF is still served with status 200.
- Unknown `k` → DAT-04's middleware itself answers with the SAME 43-byte GIF (anti-probing success-shaped drop — the handler never runs and nothing is written). The bytes served by the handler and by the middleware must be identical so known/unknown keys are indistinguishable to a prober.

## Testing

- `WebApplicationFactory<Program>` unit tests with capturing fake `IEventSink` and Redis fake/Testcontainer: byte-exact GIF assertion (compare full 43-byte array), header assertions, cookie issuance/reuse, grace `NX` behavior (pre-seed a grace entry, assert score unchanged), click-context non-clobbering (pre-seed `kind=tracker`, assert preserved), sink event shape.

## Out of scope / guardrails

- **NaN-not-zero**: do not write zeros/placeholders for SDK features anywhere — pixel sessions simply lack those fields; `has_js_beacon` stays 0 because no beacon ever arrives (D22, §7). Never emit a fake beacon or synthesize behavioral fields here.
- **No click-id logic** — no dedupe calls, no `click_id_invalid` from this endpoint (that signal is tracker-only, API-02).
- **No scoring, enrichment, or SQL** on this path; the only SQL-adjacent call is the cached tenant-config read via DAT-05's Dapper repo (tenant-stamped connection, RLS enforced) — no EF Core, no raw connections.
- **`TenantId` never optional**; all keys `t:{tid}:…` (bookkeeping set `grace:tenants` excepted, IDs only).
- **Do not block on analytics** — sink enqueue only (D12: no broker; ANA-03 owns batching). Do not query ClickHouse.
- No CORS headers needed (plain `<img>` requests are not CORS-gated); do not add an OPTIONS handler.
