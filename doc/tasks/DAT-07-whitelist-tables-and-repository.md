---
id: DAT-07
title: Whitelist tables and repository
phase: 1
workstream: data
depends_on: [DAT-03, ANA-01]
size: M
spec_refs: [D19, D11, D5, "§6.3"]
detail_level: full
---

# DAT-07: Whitelist tables and repository

## Objective
Add migration `0004_whitelist.sql` creating `dbo.WhitelistEntries` (RLS-protected), implement `IWhitelistRepository` (add / remove / list / batch is-whitelisted), and define the Redis whitelist cache contract — set `t:{tenantId}:wl:{sourceType}`, invalidated and rebuilt on every write — that the scoring pipeline (RSK-07) reads. Per D19, adding an entry with `Source = 'review_screen'` also emits a *negative* training label through `ILabelSink` (ANA-01) so the override loop doubles as the labeling loop.

## Spec context (self-contained)
- **D19 — human oversight**: the system acts on scores autonomously; every challenged/blocked event is reviewable, and marking "this was a real customer" reverses the action by **whitelisting the source** *and* **writing a negative label to the training store**. Until the portal ships, a manual whitelist API (API-07) covers the override path — API-07 consumes this repository.
- **Whitelist semantics**: a whitelisted source must not be challenged/blocked. Scoring (RSK-07) consults the whitelist inside the **<50 ms budget**, therefore the check must be a Redis set-membership lookup, never a SQL query. SQL Server is the source of truth; Redis is a cache.
- **D5 / D11 — Redis**: accessed via `StackExchange.Redis` directly (no provider abstraction — a thin interface exists only for testability). Every key is prefixed `t:{tenantId}:…`.
- **Redis cache contract (defined by THIS task, binding on RSK-07):**
  - Key: `t:{tenantId:D}:wl:{sourceType}` (lower-case GUID with dashes; sourceType ∈ `ip`, `device_id`, `fingerprint`), a Redis **SET** whose members are the whitelisted `Value` strings (non-expired only).
  - Writes (`AddAsync`/`RemoveAsync`) invalidate then rebuild the set from SQL (`RebuildCacheAsync`), and put a 1-hour TTL on the key so entries past `ExpiresUtc` age out within an hour even without writes.
  - Readers (RSK-07) use `SISMEMBER`. A **missing key means "unknown"**: treat as *not whitelisted* for the current request (fail-open to scoring — never fail-closed into a SQL query on the hot path) and schedule `RebuildCacheAsync` off the request path.
- **D11**: `TenantId` on every row, PK leads with `TenantId`, RLS FILTER + BLOCK added in this migration, ambient tenant from `ITenantContext`, explicit `@TenantId` in every statement.
- **Label semantics**: a review-screen whitelist add is a **negative** (not-fraud) label. Missing-signal semantics elsewhere are NaN-not-zero; here the label is an explicit boolean fact, not a missing value.

## Prerequisites
- **DAT-03**: `ITenantConnectionFactory`, `AddTelemetryGuardData()`, RLS policy `rls.TenantIsolationPolicy` + `rls.fn_tenantPredicate`, Dapper/SqlClient packages, FND-04 `ITenantContext`.
- **DAT-01/02**: migration runner; scripts `0001`/`0002` (DAT-06's `0003` may or may not exist yet — this script is independent; keep it at `0004`).
- **ANA-01 (declared dependency — `TelemetryGuard.Analytics.Abstractions` exists when this task runs)**: defines `ILabelSink` (see the ANA-01 task file). Actual contract:
  ```csharp
  public interface ILabelSink
  {
      ValueTask WriteAsync(LabelEvent label, CancellationToken ct);
  }
  public sealed record LabelEvent(
      TenantId TenantId, string SessionId, string Label, string LabelSource, DateTime CreatedAtUtc);
  public static class LabelValues  { public const string Fraud = "fraud"; public const string Legit = "legit"; }
  public static class LabelSources { /* ... */ public const string ReviewScreen = "review_screen"; }
  ```
  Labels join raw events **by session id** — which is why `NewWhitelistEntry` below carries an optional `SessionId` (the review screen knows which session was marked "real customer"; a label without a session cannot join to features and is not emitted). Add a project reference to `TelemetryGuard.Analytics.Abstractions` and implement emission as specified. The repository must tolerate ANA-01's types existing but no `ILabelSink` being *registered* (optional injection, null check) — the sink registration arrives with ANA-05.

## Implementation steps

1. **Create `TelemetryGuard.Data/migrations/0004_whitelist.sql`:**

```sql
------------------------------------------------------------------------------
-- 0004_whitelist.sql  (DAT-07)
-- Tenant whitelists (D19). Source of truth for the Redis cache set
-- t:{tenantId}:wl:{sourceType} read by scoring (RSK-07).
------------------------------------------------------------------------------
CREATE TABLE dbo.WhitelistEntries
(
    TenantId   uniqueidentifier NOT NULL,
    Id         bigint IDENTITY(1,1) NOT NULL,
    SourceType varchar(16)      NOT NULL,   -- 'ip' | 'device_id' | 'fingerprint'
    Value      varchar(256)     NOT NULL,   -- the IP / device id / fingerprint hash
    Reason     nvarchar(400)    NULL,
    Source     varchar(16)      NOT NULL CONSTRAINT DF_WL_Source DEFAULT ('manual'), -- 'manual' | 'review_screen'
    CreatedBy  nvarchar(200)    NULL,       -- operator/user identifier from API-07
    CreatedUtc datetime2(3)     NOT NULL CONSTRAINT DF_WL_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    ExpiresUtc datetime2(3)     NULL,       -- NULL = never expires
    CONSTRAINT PK_WhitelistEntries PRIMARY KEY CLUSTERED (TenantId, Id),
    CONSTRAINT UQ_WhitelistEntries UNIQUE NONCLUSTERED (TenantId, SourceType, Value),
    CONSTRAINT CK_WL_SourceType CHECK (SourceType IN ('ip', 'device_id', 'fingerprint')),
    CONSTRAINT CK_WL_Source CHECK (Source IN ('manual', 'review_screen'))
);
GO

-- D11 RULE: new tenant tables join the RLS policy in their own migration.
ALTER SECURITY POLICY rls.TenantIsolationPolicy
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.WhitelistEntries,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.WhitelistEntries;
GO
```
   Append registry row: `| 0004 | Whitelist entries + RLS | DAT-07 |`.

2. **NuGet**: `dotnet add TelemetryGuard.Data package StackExchange.Redis --version 2.8.16` (or the version RSK-03 pins, if that task landed first — keep the solution on one version).

3. **Model — add to `TelemetryGuard.Data/Models/` as `WhitelistModels.cs`:**
   ```csharp
   namespace TelemetryGuard.Data.Models;

   public sealed record WhitelistEntry(
       long Id, Guid TenantId, string SourceType, string Value, string? Reason,
       string Source, string? CreatedBy, DateTime CreatedUtc, DateTime? ExpiresUtc);

   /// <summary>Input for IWhitelistRepository.AddAsync (Id/CreatedUtc are DB-assigned).
   /// SessionId: the session the review screen marked as "real customer" — used only
   /// for the D19 negative training label (labels join raw events by session id);
   /// not persisted to dbo.WhitelistEntries.</summary>
   public sealed record NewWhitelistEntry(
       string SourceType, string Value, string? Reason, string Source,
       string? CreatedBy, DateTime? ExpiresUtc, string? SessionId = null);
   ```

4. **Interface — `TelemetryGuard.Data/Repositories/IWhitelistRepository.cs`:**
   ```csharp
   using TelemetryGuard.Data.Models;

   namespace TelemetryGuard.Data.Repositories;

   public interface IWhitelistRepository
   {
       /// <summary>Insert (idempotent on (SourceType, Value): re-adding an existing entry
       /// returns its existing Id and updates Reason/ExpiresUtc). Rebuilds the Redis cache set.
       /// Source='review_screen' with a SessionId additionally emits a negative
       /// (LabelValues.Legit) label via ILabelSink (D19).</summary>
       Task<long> AddAsync(NewWhitelistEntry entry, CancellationToken ct);

       /// <summary>Delete by natural key; true when a row was removed. Rebuilds the cache set.</summary>
       Task<bool> RemoveAsync(string sourceType, string value, CancellationToken ct);

       /// <summary>Single entry by Id, or null when it does not exist for this tenant
       /// (RLS makes cross-tenant ids look nonexistent). Used by API-07's DELETE route.</summary>
       Task<WhitelistEntry?> GetByIdAsync(long id, CancellationToken ct);

       /// <summary>Delete by Id; true when a row was removed. Looks up the row's SourceType
       /// first and rebuilds that cache set afterwards. Used by API-07's DELETE route.</summary>
       Task<bool> RemoveByIdAsync(long id, CancellationToken ct);

       /// <summary>Entries (optionally one sourceType), including expired; newest first;
       /// paged for API-07 (offset >= 0, limit clamped 1..200 by the caller).</summary>
       Task<IReadOnlyList<WhitelistEntry>> ListAsync(string? sourceType, int offset, int limit, CancellationToken ct);

       /// <summary>Batch membership from SQL truth (NOT the hot path — RSK-07 uses Redis).
       /// Returns value -> isWhitelisted (expired entries count as false).</summary>
       Task<IReadOnlyDictionary<string, bool>> AreWhitelistedAsync(
           string sourceType, IReadOnlyCollection<string> values, CancellationToken ct);

       /// <summary>Rewrites the Redis set t:{tenantId}:wl:{sourceType} from SQL
       /// (non-expired values only), TTL 1 hour. Safe to call concurrently. Called by
       /// writes here and, off the hot path, by RSK-07 on cache miss.</summary>
       Task RebuildCacheAsync(string sourceType, CancellationToken ct);
   }
   ```

5. **Implementation — `TelemetryGuard.Data/Repositories/WhitelistRepository.cs`** (`internal sealed`), constructor:
   ```csharp
   internal sealed class WhitelistRepository(
       ITenantConnectionFactory connections,
       ITenantContext tenant,
       IConnectionMultiplexer redis,
       ILogger<WhitelistRepository> logger,
       ILabelSink? labelSink = null) : IWhitelistRepository
   ```
   Validate `sourceType` ∈ {`ip`,`device_id`,`fingerprint`} and `Source` ∈ {`manual`,`review_screen`} up front (`ArgumentException`). Exact SQL:

   - `AddAsync` (upsert-by-natural-key so review-screen re-adds are idempotent):
     ```sql
     MERGE dbo.WhitelistEntries WITH (HOLDLOCK) AS t
     USING (SELECT @TenantId AS TenantId, @SourceType AS SourceType, @Value AS Value) AS s
         ON t.TenantId = s.TenantId AND t.SourceType = s.SourceType AND t.Value = s.Value
     WHEN MATCHED THEN UPDATE SET Reason = @Reason, ExpiresUtc = @ExpiresUtc
     WHEN NOT MATCHED THEN INSERT (TenantId, SourceType, Value, Reason, Source, CreatedBy, ExpiresUtc)
         VALUES (@TenantId, @SourceType, @Value, @Reason, @Source, @CreatedBy, @ExpiresUtc);
     SELECT Id FROM dbo.WhitelistEntries
     WHERE TenantId = @TenantId AND SourceType = @SourceType AND Value = @Value;
     ```
     (`ExecuteScalarAsync<long>` over the whole batch.) Then `await RebuildCacheAsync(sourceType, ct)`, then label emission (step 6).
   - `RemoveAsync`:
     ```sql
     DELETE FROM dbo.WhitelistEntries
     WHERE TenantId = @TenantId AND SourceType = @SourceType AND Value = @Value;
     ```
     (`rows > 0`; rebuild cache afterwards regardless.)
   - `GetByIdAsync`:
     ```sql
     SELECT Id, TenantId, SourceType, Value, Reason, Source, CreatedBy, CreatedUtc, ExpiresUtc
     FROM dbo.WhitelistEntries
     WHERE TenantId = @TenantId AND Id = @Id;
     ```
   - `RemoveByIdAsync` (fetch `SourceType` first so the right cache set is rebuilt):
     ```sql
     SELECT SourceType FROM dbo.WhitelistEntries WHERE TenantId = @TenantId AND Id = @Id;
     DELETE FROM dbo.WhitelistEntries WHERE TenantId = @TenantId AND Id = @Id;
     ```
     (`rows > 0`; when a row was removed, `await RebuildCacheAsync(thatSourceType, ct)`.)
   - `ListAsync` (guard `offset >= 0`, `limit is > 0 and <= 200` with `ArgumentOutOfRangeException`):
     ```sql
     SELECT Id, TenantId, SourceType, Value, Reason, Source, CreatedBy, CreatedUtc, ExpiresUtc
     FROM dbo.WhitelistEntries
     WHERE TenantId = @TenantId AND (@SourceType IS NULL OR SourceType = @SourceType)
     ORDER BY CreatedUtc DESC, Id DESC
     OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY;
     ```
   - `AreWhitelistedAsync` (return early with all-false for an empty input; Dapper expands `IN @Values`):
     ```sql
     SELECT Value FROM dbo.WhitelistEntries
     WHERE TenantId = @TenantId AND SourceType = @SourceType AND Value IN @Values
       AND (ExpiresUtc IS NULL OR ExpiresUtc > SYSUTCDATETIME());
     ```
     Build the dictionary: every requested value → membership in the returned set.
   - `RebuildCacheAsync`:
     ```sql
     SELECT Value FROM dbo.WhitelistEntries
     WHERE TenantId = @TenantId AND SourceType = @SourceType
       AND (ExpiresUtc IS NULL OR ExpiresUtc > SYSUTCDATETIME());
     ```
     then:
     ```csharp
     var key = $"t:{tenant.TenantId.Value:D}:wl:{sourceType}";
     var db = redis.GetDatabase();
     var tran = db.CreateTransaction();
     _ = tran.KeyDeleteAsync(key);
     if (values.Count > 0)
         _ = tran.SetAddAsync(key, values.Select(v => (RedisValue)v).ToArray());
     _ = tran.KeyExpireAsync(key, TimeSpan.FromHours(1));
     await tran.ExecuteAsync();
     ```
     Cache failures must not fail the SQL write: wrap the Redis portion in try/catch, log a warning (`logger.LogWarning(ex, ...)`), continue. SQL remains the source of truth.

6. **Label emission (D19)** — at the end of a successful `AddAsync` when `entry.Source == "review_screen"` (skip this step with a TODO if `TelemetryGuard.Analytics.Abstractions` does not exist yet — see Prerequisites):
   ```csharp
   if (entry.Source == "review_screen")
   {
       if (string.IsNullOrEmpty(entry.SessionId))
       {
           logger.LogWarning(
               "review_screen whitelist add for {SourceType} without SessionId; no training label emitted.",
               entry.SourceType);
       }
       else if (labelSink is not null)
       {
           try
           {
               await labelSink.WriteAsync(new LabelEvent(
                   tenant.TenantId, entry.SessionId,
                   LabelValues.Legit, LabelSources.ReviewScreen, DateTime.UtcNow), ct);
           }
           catch (Exception ex)
           {
               logger.LogWarning(ex, "Whitelist negative-label emission failed; whitelist add succeeded.");
           }
       }
   }
   ```
   The label lands in the `tg_labels` table (ANA-02, ClickHouse) via the sink — this repository never talks to ClickHouse itself.

7. **DI** in `AddTelemetryGuardData`:
   ```csharp
   services.TryAddScoped<Repositories.IWhitelistRepository, Repositories.WhitelistRepository>();
   ```
   Redis multiplexer (only if nothing else registered one — RSK-03 also registers it; `TryAdd` keeps them compatible):
   ```csharp
   services.TryAddSingleton<IConnectionMultiplexer>(sp =>
       ConnectionMultiplexer.Connect(
           sp.GetRequiredService<IConfiguration>().GetConnectionString("Redis") ?? "localhost:6379"));
   ```
   Config key: `ConnectionStrings:Redis`.

## Files to create or modify
- `TelemetryGuard.Data/migrations/0004_whitelist.sql` (new)
- `TelemetryGuard.Data/migrations/README.md` (modify: registry row)
- `TelemetryGuard.Data/Models/WhitelistModels.cs` (new)
- `TelemetryGuard.Data/Repositories/IWhitelistRepository.cs` (new)
- `TelemetryGuard.Data/Repositories/WhitelistRepository.cs` (new)
- `TelemetryGuard.Data/DataServiceCollectionExtensions.cs` (modify: registrations)
- `TelemetryGuard.Data/TelemetryGuard.Data.csproj` (modify: StackExchange.Redis, Microsoft.Extensions.Logging.Abstractions, and — if it exists — a project reference to `TelemetryGuard.Analytics.Abstractions` for `ILabelSink`)

## Acceptance criteria
- Fresh migration run applies through `0004`; `sys.security_predicates` lists `dbo.WhitelistEntries` twice (FILTER + BLOCK); re-run is a no-op.
- `AddAsync` twice with the same `(SourceType, Value)` yields one row and returns the same Id both times (MERGE idempotency); the second call may update `Reason`/`ExpiresUtc`.
- Duplicate `(TenantId, SourceType, Value)` cannot exist (`UQ_WhitelistEntries`).
- After `AddAsync("ip", "203.0.113.7", ...)`: Redis `SISMEMBER t:{tenantId}:wl:ip 203.0.113.7` returns 1 and `TTL` of the key is ≤ 3600 s and > 0.
- After `RemoveAsync`: the member is gone from the set.
- `GetByIdAsync` returns the inserted entry by its returned Id; a nonexistent id returns null. `RemoveByIdAsync` on an existing id returns true AND the value is gone from the `t:{tenantId}:wl:{sourceType}` set; on a nonexistent id returns false and touches no cache.
- `ListAsync("ip", offset: 1, limit: 2)` over 4 seeded ip rows returns rows 2–3 in `CreatedUtc DESC, Id DESC` order; `limit` 0 or > 200 throws `ArgumentOutOfRangeException` before I/O.
- An entry with `ExpiresUtc` in the past: `AreWhitelistedAsync` reports false, and `RebuildCacheAsync` omits it from the set.
- With Redis stopped, `AddAsync` still succeeds against SQL and logs a warning (cache failure is non-fatal).
- With no `ILabelSink` registered, `AddAsync(Source="review_screen", SessionId="s-1")` succeeds (no throw). With a fake `ILabelSink` registered, it receives exactly one `LabelEvent` with `Label == LabelValues.Legit`, `LabelSource == LabelSources.ReviewScreen`, matching tenant and `SessionId`; `Source="manual"` emits nothing; `Source="review_screen"` without a `SessionId` emits nothing and logs a warning.
- Invalid `sourceType`/`Source` values throw `ArgumentException` before any I/O.
- `dotnet build TelemetryGuard.sln` succeeds.

## Testing
DAT-08's harness (SQL + Redis Testcontainers) runs every method, the cache-contract assertions, the expiry case, and the fake-`ILabelSink` case above. In this task, verify the migration applies and the acceptance Redis checks pass once manually (`docker run -p 6379:6379 -d redis:7`, `redis-cli SISMEMBER ...`).

## Out of scope / guardrails
- Do NOT build the whitelist HTTP API (API-07), the review screen (P2-03), or the scoring-side read path (RSK-07) — this task only defines the contract they consume.
- Scoring must never fall back to SQL on cache miss — the <50 ms budget rules it out; missing Redis key = "not whitelisted this request" + background rebuild. Do not "improve" this into a synchronous SQL fallback.
- Whitelisting suppresses enforcement; it does not change scores. Rules only ever *raise* scores — never encode whitelist state as a score reduction anywhere.
- The negative label is data for the future trainer (RSK-08), not an instruction to mutate scores or verdicts retroactively.
- No Redis provider abstraction (D5) — `IConnectionMultiplexer` directly; key prefix `t:{tenantId}:…` is mandatory (D11); never write a Redis key without the tenant prefix.
- No EF Core (D9), no ClickHouse access from this project (D23/D7), no server-side Python/Node (D1). `TenantId` never optional; explicit `@TenantId` in every statement; PK leads with `TenantId` (D11).
