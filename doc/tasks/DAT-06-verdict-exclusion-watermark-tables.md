---
id: DAT-06
title: Verdict summary, exclusion queue and watermark tables
phase: 1
workstream: data
depends_on: [DAT-03]
size: M
spec_refs: [D23, D21, D20, "§6.3", D11]
detail_level: full
---

# DAT-06: Verdict summary, exclusion queue and watermark tables

## Objective
Add migration `0003_summaries_exclusions.sql` creating the SQL Server aggregate/state tables of the D23 data split — `VerdictDailySummaries`, `FlaggedSourcesDaily`, `ExclusionQueue`, `RollupWatermarks` — put all four under the RLS policy, and implement `IVerdictSummaryRepository` (idempotent MERGE upserts + reads for the admin API) and `IRollupWatermarkRepository` (used by the ANA-07 rollup job).

## Spec context (self-contained)
- **D23 — data split**: SQL Server holds *summaries and breakdowns*; raw click/event rows stay in ClickHouse. Scheduled rollup jobs (ANA-07) materialize per-tenant/per-campaign/per-day aggregates from ClickHouse into these tables so portals and APIs read small, fast, RLS-protected tables and **never query the event store directly**.
- **Scoring bands (§6.3)** determine the summary columns: 0–30 allow, 31–70 challenge, 71–100 block. Blocked (71–100) events are excluded from attribution and **queued for exclusion-list sync** — that queue is `ExclusionQueue`, written by verdict finalization (API-06) and consumed by the approval flow (INT-02) and the Google Ads sync (INT-03).
- **D21 — EnforcementMode** (per tenant): `AutoEnforce` pushes exclusions automatically; `ApprovalQueue` parks them as `pending` until approved (or rejected). Hence `Status` lifecycle `pending → approved | rejected` (INT-02), `approved → pushed | failed | unsupported` (INT-03/INT-04 sync workers; `unsupported` = the target platform cannot exclude that source type; `rejected` is terminal). Under `AutoEnforce`, API-06 may insert rows directly as `approved`. Each row carries a `Platform` so the per-platform sync workers (INT-03 `WHERE Platform='google'`, INT-04 `WHERE Platform='meta'`) can route it; workers record push results in `UpdatedUtc`/`LastError`.
- **D20**: summaries are aggregates, not behavioral telemetry — they may outlive the raw-event retention window. Exclusion entries retain IPs for as long as the exclusion is active. No TTL/cleanup in this task.
- **Idempotency**: rollups re-run (crash recovery, watermark replay), so summary writes must be idempotent **absolute-value** upserts (MERGE), never `+=` increments. `RollupWatermarks` records how far each named rollup has processed per tenant.
- **D11**: every table's PK leads with `TenantId`; `TenantId` is never optional; every new tenant table must be added to `rls.TenantIsolationPolicy` (FILTER + BLOCK) in its own migration — this migration does exactly that for all four tables.

## Prerequisites
- **DAT-03**: `rls.fn_tenantPredicate` + `rls.TenantIsolationPolicy` exist (migration 0002); `ITenantConnectionFactory`, `ISystemConnectionFactory` (with `OpenForTenantAsync` for background jobs), `AddTelemetryGuardData()`; Dapper/SqlClient packages; ambient `ITenantContext` (FND-04).
- **DAT-01/DAT-02**: migration runner + scripts `0001`, `0002` applied; `migrations/README.md` registry.
- Note: DAT-07 independently adds migration `0004`. The two scripts touch disjoint objects, so either apply order is safe; keep this file exactly at `0003`.

## Implementation steps

1. **Create `TelemetryGuard.Data/migrations/0003_summaries_exclusions.sql`:**

```sql
------------------------------------------------------------------------------
-- 0003_summaries_exclusions.sql  (DAT-06)
-- D23 split: these are SQL Server AGGREGATE/STATE tables. Raw events live in
-- ClickHouse only. All four tables are tenant-scoped => RLS FILTER + BLOCK below.
------------------------------------------------------------------------------

-- Per-tenant/campaign/day verdict counts. Written idempotently (absolute values)
-- by the ANA-07 rollup via IVerdictSummaryRepository.UpsertDailySummaryAsync.
CREATE TABLE dbo.VerdictDailySummaries
(
    TenantId   uniqueidentifier NOT NULL,
    CampaignId uniqueidentifier NOT NULL,
    [Date]     date             NOT NULL,
    Allowed    int              NOT NULL CONSTRAINT DF_VDS_Allowed DEFAULT (0),      -- score 0-30
    Challenged int              NOT NULL CONSTRAINT DF_VDS_Challenged DEFAULT (0),   -- score 31-70
    Blocked    int              NOT NULL CONSTRAINT DF_VDS_Blocked DEFAULT (0),      -- score 71-100
    ScoreSum   bigint           NOT NULL CONSTRAINT DF_VDS_ScoreSum DEFAULT (0),     -- sum of scores (mean = ScoreSum/Events)
    Events     int              NOT NULL CONSTRAINT DF_VDS_Events DEFAULT (0),
    UpdatedUtc datetime2(3)     NOT NULL CONSTRAINT DF_VDS_UpdatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_VerdictDailySummaries PRIMARY KEY CLUSTERED (TenantId, CampaignId, [Date])
);
GO

-- Per-tenant/day flagged sources (top-N feeds for dashboards/API-07).
-- SourceType: 'ip' | 'placement' | 'device_id' | 'fingerprint'
CREATE TABLE dbo.FlaggedSourcesDaily
(
    TenantId     uniqueidentifier NOT NULL,
    [Date]       date             NOT NULL,
    SourceType   varchar(16)      NOT NULL,
    Value        varchar(256)     NOT NULL,
    FlaggedCount int              NOT NULL CONSTRAINT DF_FSD_FlaggedCount DEFAULT (0), -- challenged + blocked
    BlockedCount int              NOT NULL CONSTRAINT DF_FSD_BlockedCount DEFAULT (0),
    ScoreSum     bigint           NOT NULL CONSTRAINT DF_FSD_ScoreSum DEFAULT (0),
    UpdatedUtc   datetime2(3)     NOT NULL CONSTRAINT DF_FSD_UpdatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_FlaggedSourcesDaily PRIMARY KEY CLUSTERED (TenantId, [Date], SourceType, Value),
    CONSTRAINT CK_FSD_SourceType CHECK (SourceType IN ('ip', 'placement', 'device_id', 'fingerprint'))
);
GO

-- Exclusion-sync queue (D21). Written by API-06 (verdict finalization), consumed by
-- INT-02 (approval flow: pending -> approved | rejected) and the per-platform sync
-- workers INT-03 (Platform='google') / INT-04 (Platform='meta'): approved -> pushed | failed | unsupported.
-- AutoEnforce tenants may enqueue directly as 'approved'.
-- AUTHORITATIVE SHAPE NOTE (binding on INT-02/INT-03/INT-04, whose task files say
-- "adapt to DAT-06's actual names"): the key is (TenantId, Id bigint IDENTITY) —
-- there is NO ExclusionId uniqueidentifier — and Status is these varchar literals,
-- NOT a tinyint encoding. Platform routes each row to its sync worker; UpdatedUtc
-- and LastError are written by INT-02/03/04 on every transition/push attempt.
CREATE TABLE dbo.ExclusionQueue
(
    TenantId      uniqueidentifier NOT NULL,
    Id            bigint IDENTITY(1,1) NOT NULL,
    Platform      varchar(16)      NOT NULL,               -- 'google' | 'meta' | 'tiktok' | 'other' (from the campaign's Platform; 'other' when campaign-less — API-06)
    SourceType    varchar(16)      NOT NULL,               -- 'ip' | 'placement'
    Value         varchar(256)     NOT NULL,               -- the IP or placement id
    Reason        nvarchar(400)    NOT NULL,               -- e.g. N'score=87 rule=ip_datacenter_asn'
    Status        varchar(16)      NOT NULL CONSTRAINT DF_EQ_Status DEFAULT ('pending'),
    CampaignScope uniqueidentifier NULL,                   -- NULL = tenant-wide, else a CampaignId
    CreatedUtc    datetime2(3)     NOT NULL CONSTRAINT DF_EQ_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    UpdatedUtc    datetime2(3)     NULL,                   -- set by INT-02 transitions and INT-03/04 pushes
    PushedUtc     datetime2(3)     NULL,
    LastError     nvarchar(2000)   NULL,                   -- push failure detail (INT-03/INT-04)
    CONSTRAINT PK_ExclusionQueue PRIMARY KEY CLUSTERED (TenantId, Id),
    CONSTRAINT CK_EQ_Platform CHECK (Platform IN ('google', 'meta', 'tiktok', 'other')),
    CONSTRAINT CK_EQ_SourceType CHECK (SourceType IN ('ip', 'placement')),
    CONSTRAINT CK_EQ_Status CHECK (Status IN ('pending', 'approved', 'rejected', 'pushed', 'failed', 'unsupported'))
);
GO
CREATE NONCLUSTERED INDEX IX_ExclusionQueue_TenantPlatformStatus
    ON dbo.ExclusionQueue (TenantId, Platform, Status)
    INCLUDE (SourceType, Value, CampaignScope, CreatedUtc);
GO

-- Rollup progress per tenant and named rollup (ANA-07). WatermarkUtc = exclusive
-- upper bound of raw-event time already materialized into summaries.
CREATE TABLE dbo.RollupWatermarks
(
    TenantId     uniqueidentifier NOT NULL,
    RollupName   varchar(64)      NOT NULL,   -- e.g. 'verdict_daily', 'flagged_sources_daily'
    WatermarkUtc datetime2(3)     NOT NULL,
    UpdatedUtc   datetime2(3)     NOT NULL CONSTRAINT DF_RW_UpdatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_RollupWatermarks PRIMARY KEY CLUSTERED (TenantId, RollupName)
);
GO

-- D11 RULE: new tenant tables join the RLS policy in their own migration.
ALTER SECURITY POLICY rls.TenantIsolationPolicy
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.VerdictDailySummaries,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.VerdictDailySummaries,
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.FlaggedSourcesDaily,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.FlaggedSourcesDaily,
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.ExclusionQueue,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.ExclusionQueue,
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.RollupWatermarks,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.RollupWatermarks;
GO
```
   Append registry row: `| 0003 | Verdict summaries, flagged sources, exclusion queue, rollup watermarks | DAT-06 |`.

2. **Records — `TelemetryGuard.Data/Models/SummaryRecords.cs`:**
   ```csharp
   namespace TelemetryGuard.Data.Models;

   public sealed record VerdictDailySummaryRow(
       Guid TenantId, Guid CampaignId, DateOnly Date,
       int Allowed, int Challenged, int Blocked, long ScoreSum, int Events);

   public sealed record FlaggedSourceDailyRow(
       Guid TenantId, DateOnly Date, string SourceType, string Value,
       int FlaggedCount, int BlockedCount, long ScoreSum);
   ```

3. **Interfaces — `TelemetryGuard.Data/Repositories/ISummaryRepositories.cs`:**
   ```csharp
   using TelemetryGuard.Data.Models;

   namespace TelemetryGuard.Data.Repositories;

   public interface IVerdictSummaryRepository
   {
       /// <summary>Idempotent absolute-value MERGE upsert (rollup re-runs must converge,
       /// never double-count). Row.TenantId must equal the ambient tenant.</summary>
       Task UpsertDailySummaryAsync(VerdictDailySummaryRow row, CancellationToken ct);

       /// <summary>Idempotent absolute-value MERGE upsert.</summary>
       Task UpsertFlaggedSourceAsync(FlaggedSourceDailyRow row, CancellationToken ct);

       /// <summary>Read for API-07/dashboards: inclusive date range, one campaign.</summary>
       Task<IReadOnlyList<VerdictDailySummaryRow>> GetDailySummariesAsync(
           Guid campaignId, DateOnly from, DateOnly to, CancellationToken ct);

       /// <summary>Top flagged sources over an inclusive date range, ordered by
       /// BlockedCount desc then FlaggedCount desc.</summary>
       Task<IReadOnlyList<FlaggedSourceDailyRow>> GetTopFlaggedSourcesAsync(
           DateOnly from, DateOnly to, int limit, CancellationToken ct);
   }

   public interface IRollupWatermarkRepository
   {
       /// <summary>Null when the named rollup has never run for this tenant.</summary>
       Task<DateTime?> GetAsync(string rollupName, CancellationToken ct);
       Task SetAsync(string rollupName, DateTime watermarkUtc, CancellationToken ct);
   }
   ```

4. **Implementations** (`TelemetryGuard.Data/Repositories/VerdictSummaryRepository.cs`, `RollupWatermarkRepository.cs`) — `internal sealed`, constructor `(ITenantConnectionFactory connections, ITenantContext tenant)`, connections via `OpenAsync` only, explicit `@TenantId` everywhere. Pass `DateOnly` parameters as `Date = row.Date.ToDateTime(TimeOnly.MinValue)` and read them back as `DateTime` → `DateOnly.FromDateTime(...)` (avoids Dapper/SqlClient `DateOnly` version pitfalls; map through a private row class with `DateTime Date`). Exact SQL:

   - `UpsertDailySummaryAsync` (guard `row.TenantId == tenant.TenantId.Value` else `InvalidOperationException`):
     ```sql
     MERGE dbo.VerdictDailySummaries WITH (HOLDLOCK) AS t
     USING (SELECT @TenantId AS TenantId, @CampaignId AS CampaignId, @Date AS [Date]) AS s
         ON t.TenantId = s.TenantId AND t.CampaignId = s.CampaignId AND t.[Date] = s.[Date]
     WHEN MATCHED THEN UPDATE SET
         Allowed = @Allowed, Challenged = @Challenged, Blocked = @Blocked,
         ScoreSum = @ScoreSum, Events = @Events, UpdatedUtc = SYSUTCDATETIME()
     WHEN NOT MATCHED THEN INSERT
         (TenantId, CampaignId, [Date], Allowed, Challenged, Blocked, ScoreSum, Events)
         VALUES (@TenantId, @CampaignId, @Date, @Allowed, @Challenged, @Blocked, @ScoreSum, @Events);
     ```
   - `UpsertFlaggedSourceAsync` (same guard; validate SourceType against the four allowed values first):
     ```sql
     MERGE dbo.FlaggedSourcesDaily WITH (HOLDLOCK) AS t
     USING (SELECT @TenantId AS TenantId, @Date AS [Date], @SourceType AS SourceType, @Value AS Value) AS s
         ON t.TenantId = s.TenantId AND t.[Date] = s.[Date]
        AND t.SourceType = s.SourceType AND t.Value = s.Value
     WHEN MATCHED THEN UPDATE SET
         FlaggedCount = @FlaggedCount, BlockedCount = @BlockedCount,
         ScoreSum = @ScoreSum, UpdatedUtc = SYSUTCDATETIME()
     WHEN NOT MATCHED THEN INSERT
         (TenantId, [Date], SourceType, Value, FlaggedCount, BlockedCount, ScoreSum)
         VALUES (@TenantId, @Date, @SourceType, @Value, @FlaggedCount, @BlockedCount, @ScoreSum);
     ```
   - `GetDailySummariesAsync`:
     ```sql
     SELECT TenantId, CampaignId, [Date], Allowed, Challenged, Blocked, ScoreSum, Events
     FROM dbo.VerdictDailySummaries
     WHERE TenantId = @TenantId AND CampaignId = @CampaignId AND [Date] BETWEEN @From AND @To
     ORDER BY [Date];
     ```
   - `GetTopFlaggedSourcesAsync` (guard `limit is > 0 and <= 1000`):
     ```sql
     SELECT TOP (@Limit) TenantId, [Date], SourceType, Value, FlaggedCount, BlockedCount, ScoreSum
     FROM dbo.FlaggedSourcesDaily
     WHERE TenantId = @TenantId AND [Date] BETWEEN @From AND @To
     ORDER BY BlockedCount DESC, FlaggedCount DESC;
     ```
   - `IRollupWatermarkRepository.GetAsync`:
     ```sql
     SELECT WatermarkUtc FROM dbo.RollupWatermarks
     WHERE TenantId = @TenantId AND RollupName = @RollupName;
     ```
   - `SetAsync`:
     ```sql
     MERGE dbo.RollupWatermarks WITH (HOLDLOCK) AS t
     USING (SELECT @TenantId AS TenantId, @RollupName AS RollupName) AS s
         ON t.TenantId = s.TenantId AND t.RollupName = s.RollupName
     WHEN MATCHED THEN UPDATE SET WatermarkUtc = @WatermarkUtc, UpdatedUtc = SYSUTCDATETIME()
     WHEN NOT MATCHED THEN INSERT (TenantId, RollupName, WatermarkUtc)
         VALUES (@TenantId, @RollupName, @WatermarkUtc);
     ```

5. **DI registration** in `AddTelemetryGuardData`:
   ```csharp
   services.TryAddScoped<Repositories.IVerdictSummaryRepository, Repositories.VerdictSummaryRepository>();
   services.TryAddScoped<Repositories.IRollupWatermarkRepository, Repositories.RollupWatermarkRepository>();
   ```

6. **Background-job usage note** (document as XML doc on `IVerdictSummaryRepository`): ANA-07 runs per tenant — it enumerates tenants via `ISystemConnectionFactory.OpenSystemAsync`, then for each tenant either (a) creates a DI scope, sets the scoped `TenantContext` to that tenant, and resolves this repository inside the scope (preferred — reuses this code path unchanged), or (b) uses `OpenForTenantAsync` directly for raw SQL. This repository itself needs no changes for job use.

## Files to create or modify
- `TelemetryGuard.Data/migrations/0003_summaries_exclusions.sql` (new)
- `TelemetryGuard.Data/migrations/README.md` (modify: registry row)
- `TelemetryGuard.Data/Models/SummaryRecords.cs` (new)
- `TelemetryGuard.Data/Repositories/ISummaryRepositories.cs` (new)
- `TelemetryGuard.Data/Repositories/VerdictSummaryRepository.cs` (new)
- `TelemetryGuard.Data/Repositories/RollupWatermarkRepository.cs` (new)
- `TelemetryGuard.Data/DataServiceCollectionExtensions.cs` (modify: registrations)

## Acceptance criteria
- Fresh migration run applies `0001`–`0003` in order, exit 0; re-run is a no-op.
- All four new tables are in the RLS policy: 
  ```sql
  SELECT OBJECT_NAME(target_object_id) FROM sys.security_predicates;
  ```
  lists each of the four tables twice (FILTER + BLOCK) in addition to DAT-03's tables.
- Calling `UpsertDailySummaryAsync` twice with the same row leaves exactly one row with those absolute values (idempotency — no doubling).
- `UpsertDailySummaryAsync` with `row.TenantId` ≠ ambient tenant throws `InvalidOperationException` before SQL; a hand-forged mismatched INSERT on a stamped connection throws `SqlException` (BLOCK predicate).
- `GetTopFlaggedSourcesAsync` returns rows ordered `BlockedCount DESC, FlaggedCount DESC`, at most `limit`.
- `IRollupWatermarkRepository.GetAsync("verdict_daily")` returns null before any `SetAsync`, then the exact stored value after; `SetAsync` twice keeps one row.
- `ExclusionQueue` accepts only the six status values (`pending`, `approved`, `rejected`, `pushed`, `failed`, `unsupported`), the four platforms (`google`, `meta`, `tiktok`, `other`), and two source types (CHECK-verified by attempted bad inserts); an INSERT omitting `Platform` fails (NOT NULL).
- `dotnet build TelemetryGuard.sln` succeeds.

## Testing
DAT-08 executes every method here against Testcontainers SQL Server (including the double-upsert idempotency case and RLS proofs). This task's own verification: the acceptance SQL snippets run manually once against a disposable container. Keep the interface/record names exact — ANA-07, API-06, API-07, INT-02 consume them by name.

## Out of scope / guardrails
- **No repository for `ExclusionQueue` in this task** — the table + documented contract above is the deliverable; API-06 (writer) and INT-02/INT-03 (consumers) own their access paths. If a shared repository is wanted, that is a separate task — do not sneak it in here.
- Do NOT implement the rollup job (ANA-07), verdict finalization (API-06), or any ClickHouse query — this project never touches the event store (D23; D7 forbids a cross-engine layer).
- Upserts are absolute-value MERGE, never increments — increments break rollup idempotency.
- No EF Core (D9); Dapper + explicit SQL only. No server-side Python/Node (D1).
- `TenantId` never optional; every statement carries explicit `@TenantId` even though RLS enforces (D11). All PKs lead with `TenantId`.
- No TTL/cleanup jobs here (D20 allows summaries to outlive raw retention; exclusions live while active).
- These tables are not written from the request path — nothing here is in the <50 ms scoring budget; do not add hot-path shortcuts.
