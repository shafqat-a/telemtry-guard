---
id: ANA-07
title: Rollup job ClickHouse to SQL summaries
phase: 1
workstream: analytics
depends_on: [ANA-04, DAT-06]
size: M
spec_refs: [D23, D11, D9, D20, "section 6.1 step 5"]
detail_level: full
---

# ANA-07: Rollup job ClickHouse to SQL summaries

## Objective

Implement `RollupService`, a `BackgroundService` in `TelemetryGuard.Api` that every 15 minutes materializes per-tenant/per-campaign/per-day verdict aggregates and top flagged sources from ClickHouse (via `IAnalyticsQueries`) into the SQL Server summary tables (`dbo.VerdictDailySummaries`, `dbo.FlaggedSourcesDaily`) through DAT-06's idempotent MERGE repositories, tracked per tenant by `dbo.RollupWatermarks`, with per-tenant failure isolation and OpenTelemetry metrics. This is the D23 bridge: portals and APIs read small RLS-protected SQL aggregates and NEVER query the event store directly.

## Spec context (self-contained)

- **D23:** SQL Server = summaries & breakdowns; ClickHouse = raw click/event rows. Scheduled rollup jobs materialize per-tenant/per-campaign/per-day breakdowns from ClickHouse into SQL Server. The rollup is the only writer of these aggregate tables (besides migrations).
- **D11 (RLS + tenant-bound connections):** all tenant-scoped SQL runs on connections stamped with `SESSION_CONTEXT(N'TenantId')`. Enumerating tenants (a cross-tenant read of `dbo.Tenants`) uses DAT-03's `ISystemConnectionFactory.OpenSystemAsync` — the SYSTEM sentinel (`00000000-0000-0000-0000-000000000001`) is the ONLY sanctioned cross-tenant path and exists precisely for jobs like this. Per-tenant work then runs inside a DI scope whose `TenantContext` is resolved to that tenant, so DAT-06's repositories work unchanged. Explicit `TenantId` still appears in every statement for index seeks; correctness comes from RLS.
- **D9:** SQL access is Dapper — no EF Core. This task mostly consumes DAT-06 repositories, which are already Dapper; its one direct query (campaign enumeration) is Dapper too.
- **D20:** SQL aggregates may outlive the raw ClickHouse window (raw retention 30–180 days) — never delete summary rows because raw data expired.
- **§7 missing ≠ zero:** the summary tables store `ScoreSum` + event counts (mergeable absolute values), not averages — readers compute `avg = ScoreSum / Events` and get "no data" from `Events = 0` rather than a fake 0 average. Never fabricate an average of 0.
- **Idempotency (DAT-06 contract):** rollups re-run (crash recovery, watermark replay); all writes are absolute-value MERGE upserts, never `+=` increments. Running the same window twice must converge to identical rows.
- Aggregation windows are UTC days; the job runs every 15 min and re-aggregates recent days idempotently, so late-arriving events and verdicts finalized after the grace period (API-06) are folded in on the next run.

## Prerequisites

- **ANA-01/ANA-04:** `IAnalyticsQueries.GetCampaignReportAsync(string campaignId, DateRange, ct)` → `CampaignFraudReport` with `Days: IReadOnlyList<CampaignDailyCounts>` where `CampaignDailyCounts = (DateOnly Day, long TotalEvents, long ScoredEvents, long Allowed, long Challenged, long Blocked, long ScoreSum, double AvgScore, long NoJsBeaconCount)`; `GetTopFlaggedSourcesAsync(DateRange, int limit, ct)` → `FlaggedSource("ip", SourceValue, FlaggedEvents, BlockedEvents, TotalEvents, ScoreSum, AvgScore, FirstSeenUtc, LastSeenUtc)`. `DateRange` is half-open UTC. Registered scoped by ANA-05 (consumes scoped `ITenantContext`).
- **DAT-06** (`TelemetryGuard.Data`, namespace `TelemetryGuard.Data.Repositories` / `TelemetryGuard.Data.Models`) — consume by name, do not re-implement:
  ```csharp
  public sealed record VerdictDailySummaryRow(
      Guid TenantId, Guid CampaignId, DateOnly Date,
      int Allowed, int Challenged, int Blocked, long ScoreSum, int Events);

  public sealed record FlaggedSourceDailyRow(
      Guid TenantId, DateOnly Date, string SourceType, string Value,
      int FlaggedCount, int BlockedCount, long ScoreSum);

  public interface IVerdictSummaryRepository
  {
      Task UpsertDailySummaryAsync(VerdictDailySummaryRow row, CancellationToken ct);
      Task UpsertFlaggedSourceAsync(FlaggedSourceDailyRow row, CancellationToken ct);
      // + reads used by API-07, not needed here
  }

  public interface IRollupWatermarkRepository
  {
      Task<DateTime?> GetAsync(string rollupName, CancellationToken ct);   // null = never ran
      Task SetAsync(string rollupName, DateTime watermarkUtc, CancellationToken ct);
  }
  ```
  Both are registered scoped and guard `row.TenantId == ambient tenant`. Backing tables: `dbo.VerdictDailySummaries` (PK `TenantId, CampaignId, [Date]`), `dbo.FlaggedSourcesDaily` (PK `TenantId, [Date], SourceType, Value`; `SourceType` CHECK-limited to `ip|placement|device_id|fingerprint`), `dbo.RollupWatermarks` (PK `TenantId, RollupName`).
- **DAT-03** (`TelemetryGuard.Data`): `ISystemConnectionFactory` with `Task<SqlConnection> OpenSystemAsync(ct)` (SYSTEM sentinel — sees all tenants; enumeration only) and `Task<SqlConnection> OpenForTenantAsync(Guid tenantId, ct)` (per-tenant stamped; fallback path). Registered singleton via `AddTelemetryGuardData`.
- **DAT-02 (transitive):** `dbo.Tenants` (`TenantId uniqueidentifier`, `Status tinyint` — 0 = Active) and `dbo.Campaigns` (`TenantId`, `CampaignId uniqueidentifier`, `Status tinyint` — 0 = Active).
- **FND-04** (`TelemetryGuard.Core`): `TelemetryGuard.Core.Tenancy.TenantId(Guid Value)`, scoped set-once `TenantContext` with `void Resolve(TenantId, string? siteKey = null)` (registered scoped by the composition root; injectable as concrete `TenantContext` — DAT-04's middleware consumes it the same way), `TelemetryGuard.Core.Time.IClock` (`DateTimeOffset UtcNow`).
- `TelemetryGuard.Api` exists (FND-01 scaffold) and references Data, Analytics.Abstractions, Analytics.ClickHouse. NuGet `Dapper` comes via `TelemetryGuard.Data`; add directly to Api if the campaign query needs it.

## Implementation steps

1. **Options** — `TelemetryGuard.Api/Workers/RollupOptions.cs`, bound to config section `"Rollup"`:
   ```csharp
   namespace TelemetryGuard.Api.Workers;

   public sealed class RollupOptions
   {
       public double IntervalMinutes { get; init; } = 15;
       public int LookbackDays { get; init; } = 3;        // first-run / no-watermark backfill window
       public int TopFlaggedLimit { get; init; } = 100;   // flagged sources per tenant per day
       public string RollupName { get; init; } = "verdict_daily"; // key into dbo.RollupWatermarks
   }
   ```
   Add to `TelemetryGuard.Api/appsettings.json`: `"Rollup": { "IntervalMinutes": 15, "LookbackDays": 3, "TopFlaggedLimit": 100 }`.

2. **Service skeleton** — `TelemetryGuard.Api/Workers/RollupService.cs`:
   ```csharp
   using System.Diagnostics;
   using System.Diagnostics.Metrics;
   using Dapper;
   using Microsoft.Extensions.Options;
   using TelemetryGuard.Analytics.Abstractions;
   using TelemetryGuard.Core.Tenancy;
   using TelemetryGuard.Core.Time;
   using TelemetryGuard.Data;
   using TelemetryGuard.Data.Models;
   using TelemetryGuard.Data.Repositories;

   namespace TelemetryGuard.Api.Workers;

   /// <summary>
   /// D23 rollup: ClickHouse aggregates -> SQL summary tables, every 15 min.
   /// The ONLY bridge between the event store and the relational tier.
   /// </summary>
   public sealed class RollupService(
       IServiceScopeFactory scopeFactory,
       ISystemConnectionFactory systemConnections, // DAT-03: tenant enumeration ONLY
       IOptions<RollupOptions> options,
       IClock clock,
       ILogger<RollupService> log) : BackgroundService
   {
       private static readonly Meter Meter = new("TelemetryGuard.Rollup");
       private static readonly Counter<long> Runs           = Meter.CreateCounter<long>("tg.rollup.runs");
       private static readonly Counter<long> TenantsOk      = Meter.CreateCounter<long>("tg.rollup.tenants_processed");
       private static readonly Counter<long> TenantFailures = Meter.CreateCounter<long>("tg.rollup.tenant_failures");
       private static readonly Counter<long> RowsUpserted   = Meter.CreateCounter<long>("tg.rollup.rows_upserted");
       private static readonly Histogram<double> RunSeconds = Meter.CreateHistogram<double>("tg.rollup.run_seconds");

       protected override async Task ExecuteAsync(CancellationToken stoppingToken)
       {
           using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Value.IntervalMinutes));
           do { await RunOnceAsync(stoppingToken); }
           while (await timer.WaitForNextTickAsync(stoppingToken));
       }

       public async Task RunOnceAsync(CancellationToken ct) { /* steps 3–6; public for tests */ }
   }
   ```
   Wrap `RunOnceAsync`'s body in try/catch: any unexpected exception is logged and swallowed (a failed run must never kill the host loop; `OperationCanceledException` on shutdown may propagate). Time the run into `RunSeconds`, count `Runs`.

3. **Enumerate tenants (SYSTEM sentinel).** Inside `RunOnceAsync`:
   ```csharp
   IReadOnlyList<Guid> tenantIds;
   await using (var sys = await systemConnections.OpenSystemAsync(ct))
   {
       tenantIds = (await sys.QueryAsync<Guid>(
           "SELECT TenantId FROM dbo.Tenants WHERE Status = 0")).AsList(); // 0 = Active (DAT-02)
   }
   ```
   Never open a raw `new SqlConnection(...)` in this class — the DAT-03 factories are the only sanctioned paths.

4. **Per-tenant loop with failure isolation.**
   ```csharp
   foreach (var tid in tenantIds)
   {
       ct.ThrowIfCancellationRequested();
       try { await ProcessTenantAsync(tid, ct); TenantsOk.Add(1); }
       catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
       catch (Exception ex)
       {
           TenantFailures.Add(1);
           log.LogError(ex, "Rollup failed for tenant {TenantId}; continuing with remaining tenants", tid);
       }
   }
   ```

5. **`ProcessTenantAsync(Guid tid, CancellationToken ct)` — a DI scope resolved to the tenant.** This reuses DAT-06's repositories and ANA-05's scoped `IAnalyticsQueries` unchanged (the pattern DAT-06 documents as preferred for background jobs):
   ```csharp
   using var scope = scopeFactory.CreateScope();
   var sp = scope.ServiceProvider;

   // FND-04's set-once scoped TenantContext — same call DAT-04's middleware makes per request.
   sp.GetRequiredService<TenantContext>().Resolve(new TenantId(tid));

   var queries    = sp.GetRequiredService<IAnalyticsQueries>();          // ANA-05, scoped
   var summaries  = sp.GetRequiredService<IVerdictSummaryRepository>();  // DAT-06, scoped
   var watermarks = sp.GetRequiredService<IRollupWatermarkRepository>(); // DAT-06, scoped
   var connFactory = sp.GetRequiredService<ITenantConnectionFactory>();  // DAT-03, for the campaigns query
   ```
   (If the composition root registered only `ITenantContext` and not the concrete `TenantContext`, resolve `ITenantContext` and cast: `((TenantContext)sp.GetRequiredService<ITenantContext>()).Resolve(...)` — check `Program.cs`.)

6. **Window math, aggregation, upserts, watermark.** All UTC. The window re-covers the last closed day plus today so late events converge:
   ```csharp
   var now = clock.UtcNow.UtcDateTime;
   var rollupName = options.Value.RollupName;
   var watermark = await watermarks.GetAsync(rollupName, ct);   // null on first run
   var (fromDay, range) = ComputeWindow(watermark, now, options.Value.LookbackDays);

   // enumerate this tenant's campaigns (RLS-scoped stamped connection)
   IReadOnlyList<Guid> campaignIds;
   await using (var conn = await connFactory.OpenAsync(ct))
   {
       campaignIds = (await conn.QueryAsync<Guid>(
           "SELECT CampaignId FROM dbo.Campaigns WHERE TenantId = @TenantId",
           new { TenantId = tid })).AsList();
   }

   long rows = 0;
   foreach (var campaignId in campaignIds)
   {
       // ClickHouse stores campaign_id as the Guid in "D" format (lowercase, hyphenated) —
       // the same string API-02 stamps onto ClickEvent.CampaignId from the /c?cid= parameter.
       var report = await queries.GetCampaignReportAsync(campaignId.ToString("D"), range, ct);
       foreach (var d in report.Days)
       {
           await summaries.UpsertDailySummaryAsync(new VerdictDailySummaryRow(
               TenantId: tid, CampaignId: campaignId, Date: d.Day,
               Allowed: checked((int)d.Allowed), Challenged: checked((int)d.Challenged),
               Blocked: checked((int)d.Blocked), ScoreSum: d.ScoreSum,
               Events: checked((int)d.ScoredEvents)), ct);   // Events = scored (verdict) events; avg = ScoreSum/Events
           rows++;
       }
   }

   for (var day = fromDay; day <= now.Date; day = day.AddDays(1))
   {
       var dayEnd = day.AddDays(1) < now ? day.AddDays(1) : now;
       var dayRange = new DateRange(
           DateTime.SpecifyKind(day, DateTimeKind.Utc),
           DateTime.SpecifyKind(dayEnd, DateTimeKind.Utc));
       var sources = await queries.GetTopFlaggedSourcesAsync(dayRange, options.Value.TopFlaggedLimit, ct);
       foreach (var s in sources)
       {
           await summaries.UpsertFlaggedSourceAsync(new FlaggedSourceDailyRow(
               TenantId: tid, Date: DateOnly.FromDateTime(day),
               SourceType: s.SourceType,                  // "ip" — allowed by the DAT-06 CHECK constraint
               Value: s.SourceValue,
               FlaggedCount: checked((int)s.FlaggedEvents),
               BlockedCount: checked((int)s.BlockedEvents),
               ScoreSum: s.ScoreSum), ct);
           rows++;
       }
   }

   await watermarks.SetAsync(rollupName, now, ct);  // advance ONLY after all upserts succeeded
   RowsUpserted.Add(rows);
   ```
   Extract the window math as a pure function for unit tests:
   ```csharp
   internal static (DateTime fromDay, DateRange range) ComputeWindow(
       DateTime? watermarkUtc, DateTime nowUtc, int lookbackDays)
   {
       var fromDay = watermarkUtc is null
           ? nowUtc.Date.AddDays(-lookbackDays)
           : watermarkUtc.Value.Date.AddDays(-1);   // re-cover the last closed day for late arrivals
       return (fromDay, new DateRange(
           DateTime.SpecifyKind(fromDay, DateTimeKind.Utc),
           DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)));
   }
   ```
   A failed tenant keeps its old watermark and re-covers the same window next run — DAT-06's absolute-value MERGEs make that convergent, never double-counting.

7. **Registration** — in `TelemetryGuard.Api/Program.cs`:
   ```csharp
   builder.Services.Configure<RollupOptions>(builder.Configuration.GetSection("Rollup"));
   builder.Services.AddHostedService<RollupService>();
   ```
   (`AddTelemetryGuardData` / `AddTelemetryGuardAnalytics` are already called per DAT-03/ANA-05.)

## Files to create or modify

- `TelemetryGuard.Api/Workers/RollupOptions.cs`
- `TelemetryGuard.Api/Workers/RollupService.cs`
- `TelemetryGuard.Api/Program.cs` (options + hosted-service registration)
- `TelemetryGuard.Api/appsettings.json` (`Rollup` section)
- `TelemetryGuard.Api/TelemetryGuard.Api.csproj` (project references / Dapper package if missing)

## Acceptance criteria

- `dotnet build` passes; `RollupService` starts with the host and stops cleanly on Ctrl-C (no unobserved exceptions).
- With seeded data (see Testing), one `RunOnceAsync`:
  - upserts `dbo.VerdictDailySummaries` rows keyed `(TenantId, CampaignId, Date)` whose `Allowed/Challenged/Blocked/ScoreSum/Events` exactly match the seeded ClickHouse verdicts per UTC day;
  - upserts `dbo.FlaggedSourcesDaily` rows (`SourceType = 'ip'`) only for IPs with challenge/block verdicts, at most `TopFlaggedLimit` per day;
  - writes one `dbo.RollupWatermarks` row per processed tenant with `RollupName = 'verdict_daily'`.
- Idempotence: calling `RunOnceAsync` twice back-to-back leaves identical summary row counts and values (absolute-value MERGE convergence), with only `UpdatedUtc`/watermark advancing.
- A campaign day with zero verdicts produces either no row or `Events = 0` with `ScoreSum = 0` — never a fabricated average; no code path computes `ScoreSum/Events` without guarding `Events > 0`.
- Failure isolation: with tenant A healthy and tenant B forced to fail (e.g., drop tenant B's campaigns table access in the test or point its scope at a dead ClickHouse), tenant A's rows land, `tg.rollup.tenant_failures` increments by 1, and tenant B's watermark does NOT advance.
- Cross-tenant safety: tenant A's summary rows contain only tenant A's numbers even when tenant B has events for the same campaign ids/IPs.
- `grep -n "new SqlConnection" TelemetryGuard.Api/Workers/RollupService.cs` returns nothing (factories only).

## Testing

- Unit (`tests/TelemetryGuard.Tests.Unit`): `ComputeWindow` — no watermark → `[today-LookbackDays, now)`; watermark yesterday → from two days ago (`watermark.Date.AddDays(-1)`); watermark today → from yesterday; all bounds `DateTimeKind.Utc`. Plus a mapping test: a `CampaignDailyCounts` with `AvgScore = double.NaN`, `ScoreSum = 0`, `ScoredEvents = 0` maps to a `VerdictDailySummaryRow` with `Events = 0, ScoreSum = 0`.
- Integration (`tests/TelemetryGuard.Tests.Integration`, `[Trait("requires","docker")]`): Testcontainers MsSql (DbUp migrations 0001–0003 via DAT-01 runner or DbUp directly) + Testcontainers ClickHouse (ANA-02 `SchemaMigrator`); seed 2 tenants + campaigns in SQL and matching `tg_events` verdicts across 2 UTC days in ClickHouse; build the service graph (real repositories, real `ClickHouseAnalyticsQueries`, `FixedClock`) in a `ServiceCollection` mirroring `Program.cs`; call `RunOnceAsync` and assert the acceptance bullets with Dapper SELECTs on stamped connections; call it again for the idempotence proof.

## Out of scope / guardrails

- **Portals/APIs never query the event store (D23):** do not add endpoints or repositories that read ClickHouse for reporting — they read these SQL tables (API-07 does). This job is the only bridge.
- **Dapper, not EF Core (D9).** And prefer DAT-06's repositories over inline SQL — the only inline query here is campaign/tenant enumeration.
- **RLS + tenant-bound connections (D11):** never `new SqlConnection`; SYSTEM sentinel only for tenant enumeration via `ISystemConnectionFactory`; per-tenant work happens inside a scope resolved to that tenant; every inline statement still carries explicit `@TenantId`.
- **Tenant is never optional:** no cross-tenant aggregate statements; one tenant per scope, always.
- **Missing ≠ zero:** store `ScoreSum` + `Events`, never a defaulted 0.0 average; guard divisions.
- **Absolute values, never increments** — incrementing MERGEs would break replay idempotency (DAT-06 contract).
- **Do not delete or expire summary rows** (they may outlive raw retention, D20). No Phase-2 publisher aggregates (P2-01).
- **Hot path untouched:** nothing here runs in request handling; do not wire rollup logic into any endpoint (<50 ms scoring budget stays unaffected).
- **No new `IAnalyticsQueries` methods (D7)** — the three intent methods suffice; no generic cross-engine query layer, no direct ClickHouse SQL in the Api project.
- No broker/queue (D12) — `PeriodicTimer` in-process is the design. No server-side Python/Node (D1). Rules-only-raise and NaN feature semantics are upstream concerns (RSK/ANA-03) — this job must not reinterpret scores or bands, only count them.
