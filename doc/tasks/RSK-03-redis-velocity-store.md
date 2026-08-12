---
id: RSK-03
title: "Redis velocity store and click-id dedupe"
phase: 1
workstream: risk
depends_on: [FND-02, FND-04]
size: M
spec_refs: [D5, D11, "§6.1", "§7"]
detail_level: full
---

# RSK-03: Redis velocity store and click-id dedupe

## Objective

Implement `IVelocityStore` in `TelemetryGuard.RiskEngine` over `StackExchange.Redis`: tenant-prefixed sliding-window counters and HyperLogLogs that produce the velocity features (`ip_clicks_last_min`, `ip_distinct_uas_last_hour`, `device_sessions_last_hour`, `device_ids_this_ip_hour`), gclid/fbclid replay dedupe (`SET NX EX 86400`), and the `fpz` zero-storage-age repeat counter consumed by RSK-04 for `storage_age_zero_repeat`. All capture operations for one request are issued through a single `IBatch` (one round trip — the whole scoring path has a < 50 ms budget); all reads for scoring likewise batch into one round trip. Bucket arithmetic comes from `IClock` so tests control time.

## Spec context (self-contained)

- D5: Redis is the cache/velocity store, accessed via `StackExchange.Redis` with NO provider abstraction — the Redis protocol is the seam (Redis/Garnet/Azure Cache are a connection-string change). A thin interface exists **only for testability**.
- D11: every Redis key is prefixed `t:{tenantId}:…` — this doubles as the seat of per-tenant isolation in Redis. Tenant id comes from the ambient `ITenantContext` (FND-04); it is NEVER an optional parameter and never omitted from a key.
- §6.1: the click tracker validates/dedupes `gclid` via Redis `SETNX` — a replayed or missing click id becomes the T1 feature `click_id_invalid`.
- §7 velocity features are plain numerics, **legitimately 0 when cold** (missing ≠ zero applies to SDK signals, not to velocity counters).
- §7 `storage_age_zero_repeat` (T2): "a fingerprint seen 40 times that presents brand-new storage every visit is a bot farm wiping state" — we count, per device fingerprint (visitor id), how many sessions in the last 7 days reported zero first-party-storage age.
- Scoring budget < 50 ms end-to-end (D3): hence single-RTT batching for both capture and read.

## Prerequisites

- FND-02: Docker Compose dev stack includes a `redis` service reachable at `localhost:6379` (see the FND-02 task file for the exact port/compose service name).
- FND-04: core primitives exist — `ITenantContext` (exposes the current tenant id, e.g. `TenantId TenantId { get; }` with a string/GUID value) and `IClock` (e.g. `DateTimeOffset UtcNow { get; }`). Read the FND-04 task file for the exact namespace/project (expected `TelemetryGuard.Core` or similar) and add a `ProjectReference` from `TelemetryGuard.RiskEngine` to it. Wherever this task's text says `{tid}`, substitute `tenantContext.TenantId` rendered as its canonical string form (use the `ToString()`/`Value` convention FND-04 established).
- `TelemetryGuard.RiskEngine` project exists (created by FND-01 via the FND-04 chain; create + `dotnet sln add` if absent).

## Key and operation table (normative)

All buckets derive from `IClock.UtcNow`: `minuteBucket = unixSeconds / 60`, `hourBucket = unixSeconds / 3600` (integer division, invariant culture decimal rendering).

| Feature / op | Key pattern | Capture op | TTL | Read op |
|---|---|---|---|---|
| `ip_clicks_last_min` | `t:{tid}:v:ipm:{ip}:{minuteBucket}` | `INCR` + `EXPIRE 180` | 180 s | `GET` current + previous minute bucket; weighted sliding sum (below) |
| `ip_distinct_uas_last_hour` | `t:{tid}:v:ipua:{ip}:{hourBucket}` | `PFADD <uaHash>` + `EXPIRE 7200` | 2 h | `PFCOUNT curKey prevKey` (union) |
| `device_sessions_last_hour` | `t:{tid}:v:dev:{visitorId}:{hourBucket}` | `PFADD <sessionId>` + `EXPIRE 7200` | 2 h | `PFCOUNT curKey prevKey` |
| `device_ids_this_ip_hour` | `t:{tid}:v:ipdev:{ip}:{hourBucket}` | `PFADD <visitorId>` + `EXPIRE 7200` | 2 h | `PFCOUNT curKey prevKey` |
| click-id dedupe | `t:{tid}:cid:{clickId}` | `SET "1" NX EX 86400` | 24 h | return value of the SET (true = fresh, false = replay) |
| storage-age-zero repeats | `t:{tid}:fpz:{visitorId}` | `INCR` + `EXPIRE 604800` | 7 d | `GET` |

Sliding-window read for `ip_clicks_last_min` (classic two-bucket approximation):

```
secondsIntoCurrentMinute = unixSeconds % 60
count = current + round(previous * (60 - secondsIntoCurrentMinute) / 60.0)
```

`uaHash` = lowercase hex of the first 8 bytes of SHA-256 of the raw User-Agent string (bounds HLL element size); empty/null UA → skip the PFADD.

## Implementation steps

1. Add NuGet package `StackExchange.Redis` (2.8.x) to `TelemetryGuard.RiskEngine.csproj`.

2. Create `TelemetryGuard.RiskEngine/Velocity/VelocitySnapshot.cs`:

```csharp
namespace TelemetryGuard.RiskEngine.Velocity;

/// <summary>One batched read of all velocity features for scoring. All values are
/// legitimately 0 when cold — never NaN here (NaN mapping for StorageAgeZeroRepeat
/// happens in feature extraction when visitorId is absent).</summary>
public sealed record VelocitySnapshot(
    int IpClicksLastMin,
    long IpDistinctUasLastHour,
    long DeviceSessionsLastHour,
    long DeviceIdsThisIpHour,
    long StorageAgeZeroRepeat);
```

3. Create `TelemetryGuard.RiskEngine/Velocity/IVelocityStore.cs`:

```csharp
namespace TelemetryGuard.RiskEngine.Velocity;

/// <summary>Thin testability seam over Redis (D5 — no provider abstraction beyond this).
/// All keys are tenant-prefixed t:{tenantId}: from the ambient ITenantContext.</summary>
public interface IVelocityStore
{
    /// <summary>Capture for a tracker click (/c). Single IBatch round trip:
    /// INCR minute bucket, PFADD ip→ua HLL, SET NX click id.
    /// Returns: true = clickId seen first time; false = replay; null = clickId was null.</summary>
    Task<bool?> RecordClickAsync(string ip, string? userAgent, string? clickId, CancellationToken ct);

    /// <summary>Capture for a beacon/session observation (/i). Single IBatch round trip:
    /// PFADD device→session HLL, PFADD ip→device HLL, PFADD ip→ua HLL,
    /// and INCR fpz:{visitorId} when storageAgeZero is true.</summary>
    Task RecordSessionAsync(string ip, string? userAgent, string? visitorId, string sessionId,
                            bool storageAgeZero, CancellationToken ct);

    /// <summary>Batched read of every velocity feature for scoring — single round trip.
    /// visitorId null → device-keyed values return 0 (extraction maps to NaN where required).</summary>
    Task<VelocitySnapshot> ReadAsync(string ip, string? visitorId, CancellationToken ct);
}
```

4. Create `TelemetryGuard.RiskEngine/Velocity/RedisVelocityStore.cs`:
   - Constructor: `RedisVelocityStore(IConnectionMultiplexer mux, ITenantContext tenant, IClock clock)`. Register as **scoped** (tenant context is scoped per request).
   - Private helpers: `string K(string suffix) => $"t:{_tenant.TenantId}:{suffix}";`, `long MinuteBucket()`, `long HourBucket()`, `static string UaHash(string ua)`.
   - `RecordClickAsync`: `var db = _mux.GetDatabase(); var batch = db.CreateBatch();` queue:
     - `batch.StringIncrementAsync(K($"v:ipm:{ip}:{minB}"))` and `batch.KeyExpireAsync(same, TimeSpan.FromSeconds(180))`
     - if UA present: `batch.HyperLogLogAddAsync(K($"v:ipua:{ip}:{hourB}"), uaHash)` + expire 7200 s
     - if clickId present: `batch.StringSetAsync(K($"cid:{clickId}"), "1", TimeSpan.FromSeconds(86400), When.NotExists)`
     - `batch.Execute(); await Task.WhenAll(...queued tasks...)`; return the `StringSetAsync` result (`bool?`).
   - `RecordSessionAsync`: same batch pattern; only queue visitor-keyed ops when `visitorId` non-null; `fpz` = `StringIncrementAsync(K($"fpz:{visitorId}"))` + `KeyExpireAsync(…, TimeSpan.FromDays(7))` only when `storageAgeZero && visitorId != null`.
   - `ReadAsync`: one batch queuing `StringGetAsync` (current + previous minute buckets), `HyperLogLogLengthAsync(new RedisKey[]{cur, prev})` for each of the three HLL families, and `StringGetAsync(fpz)` when visitorId present. Apply the weighted sliding formula; parse null RedisValues as 0. Return `VelocitySnapshot`.
   - No Lua, no transactions, no `KEYS`/`SCAN` anywhere.

5. Create `TelemetryGuard.RiskEngine/Velocity/VelocityServiceCollectionExtensions.cs`:

```csharp
public static IServiceCollection AddVelocityStore(this IServiceCollection services, IConfiguration config)
{
    // Reuse an existing multiplexer registration if the host already added one.
    services.TryAddSingleton<IConnectionMultiplexer>(_ =>
        ConnectionMultiplexer.Connect(config.GetConnectionString("Redis")
            ?? throw new InvalidOperationException("ConnectionStrings:Redis is required")));
    services.AddScoped<IVelocityStore, RedisVelocityStore>();
    return services;
}
```

Config key: `"ConnectionStrings": { "Redis": "localhost:6379" }` (matches the FND-02 compose stack).

6. Guard rails in code: throw `ArgumentException` from all methods when `ip` is null/empty; treat whitespace UA as null; never write a key without the `t:{tid}:` prefix (assert in one place — the `K` helper is the only key constructor).

## Files to create or modify

- `TelemetryGuard.RiskEngine/TelemetryGuard.RiskEngine.csproj` (add `StackExchange.Redis`, project ref to the FND-04 core project)
- `TelemetryGuard.RiskEngine/Velocity/VelocitySnapshot.cs`
- `TelemetryGuard.RiskEngine/Velocity/IVelocityStore.cs`
- `TelemetryGuard.RiskEngine/Velocity/RedisVelocityStore.cs`
- `TelemetryGuard.RiskEngine/Velocity/VelocityServiceCollectionExtensions.cs`
- `tests/TelemetryGuard.Tests.Integration/Velocity/RedisVelocityStoreTests.cs`
- `tests/TelemetryGuard.Tests.Integration/TelemetryGuard.Tests.Integration.csproj` (add `Testcontainers.Redis` if absent, project ref to RiskEngine)

## Acceptance criteria

- `dotnet build` passes.
- Integration tests (Testcontainers Redis) prove, with a controllable fake `IClock` and fake `ITenantContext` (`tenant-a`):
  1. Three `RecordClickAsync` calls in the same minute → `ReadAsync(...).IpClicksLastMin == 3`.
  2. Sliding window: 10 clicks at minute M, clock advanced to M+1 at second 30 → `IpClicksLastMin == 5` (10 × 30/60, rounded).
  3. Clock advanced ≥ 2 minutes → count returns to 0 (bucket TTL/exclusion).
  4. `RecordClickAsync` with clickId "g123" returns `true` first call, `false` second call (replay); `null` clickId returns `null`.
  5. Sessions with 3 distinct visitorIds from one IP in the hour → `DeviceIdsThisIpHour == 3` (HLL exactness at tiny cardinalities); 3 sessions for one visitor → `DeviceSessionsLastHour == 3`; 2 distinct UAs → `IpDistinctUasLastHour == 2`.
  6. `RecordSessionAsync(storageAgeZero: true)` twice for visitor V → `StorageAgeZeroRepeat == 2`; the `fpz` key's TTL is ≤ 7 days and > 6 days (assert via `KeyTimeToLive`).
  7. Key isolation: after running the above as `tenant-a`, a store bound to `tenant-b` reads all zeros, and every key in Redis (`SCAN` from the TEST only) starts with `t:tenant-a:` or `t:tenant-b:`.
  8. `ReadAsync` issues exactly one network round trip: assert via the multiplexer's `GetCounters()` or, simpler, assert wall time of 100 sequential `ReadAsync` calls < 2 s against the container.
- No production code path calls Redis more than once per `IVelocityStore` method (code review criterion: one `CreateBatch`/`Execute` pair per method).

## Testing

- Integration: `Testcontainers.Redis` fixture (`redis:7-alpine`) shared per test class (`IClassFixture`). Fake `IClock` = mutable `DateTimeOffset` property; fake `ITenantContext` = fixed tenant string. Cover the 8 criteria above.
- Unit: `UaHash` determinism + bucket math (`MinuteBucket`/`HourBucket` boundaries at :00 and :59) with a fake clock — pure, no Redis.

## Out of scope / guardrails

- NO provider abstraction over Redis beyond `IVelocityStore` (D5) — no `IRedisProvider`, no cache-engine switch; switching engines is a connection-string change by design.
- Every key MUST carry the `t:{tenantId}:` prefix from `ITenantContext` — tenant id is never a method parameter, never optional, never defaulted (D11). Do not add overloads accepting a tenant id.
- Do not store session state, challenge tokens, whitelists, or rate limits here — this store is velocity + dedupe + fpz only (other keys belong to RSK-07/DAT-07/API tasks).
- Velocity values are plain numerics that are legitimately 0 when cold — do NOT return NaN/null from `ReadAsync`; the NaN mapping for `storage_age_zero_repeat` (no fingerprint case) is RSK-04's job.
- Single-RTT batching is a hard requirement (< 50 ms budget, D3): no sequential awaited Redis calls inside a method; no Lua scripts; no `KEYS`/`SCAN` in production code.
- No server-side Python/Node (D1); no EF (D9 — irrelevant here but no SQL access belongs in this class at all).
