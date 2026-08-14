---
id: P2-01
title: Publisher aggregates
phase: 2
workstream: analytics
depends_on: [ANA-04, ANA-06, ANA-07, DAT-06, API-07]
size: L
spec_refs: [D3, D7, D9, D10, D11, D23, "§7 T2 publisher aggregates", "§10 Phase 2"]
detail_level: full
---

# P2-01: Publisher aggregates

## Objective

Add two new **intent-named** ClickHouse reads over `tg_events` — per-day **placement** (publisher) counts and per-day **site** (non-campaign) counts — materialize both into two new RLS-protected SQL Server summary tables via migration `0008`, wire them into the existing ANA-07 `RollupService` under their own `dbo.RollupWatermarks` name, and surface them as two read-only reports on the existing `/admin` group.

"Placement" is not a column on `tg_events`. It is **derived**: a verdict row is attributed to the publisher host of its own session's earliest capture row (`kind IN ('tracker','pixel')`, whose `referrer` header the API does record). That derivation, its normalization, and its "no referrer ⇒ no row" rule are the heart of this task and are specified exactly below.

This task ships the **aggregate + report** half of the outline. The T2 model-feature half (`publisher_fraud_ratio` on `FraudFeatureVector`, `FeatureSetVersion` 1 → 2, score-time cached lookup) is deliberately **deferred to a later, not-yet-written task** — **not** to P2-02, whose own *Out of scope* section explicitly excludes publisher-aggregate features and whose `depends_on` does not include P2-01. *Out of scope* below writes down the exact smoothing contract that future task must implement so nothing is lost.

## Spec context (self-contained)

- **D23 — data split**: aggregates live in SQL Server; raw events stay in ClickHouse; portals/APIs read only the small RLS-protected SQL summary tables and **never** query the event store directly. `RollupService` is the only bridge.
- **D7 — abstract by intent, not by query**: every new analytics read is a new *intent-named* method on `IAnalyticsQueries`, implemented per provider in that engine's native dialect. Never a generic cross-engine query layer, never LINQ-over-both. Adding a method here obliges every provider (P2-05's Kusto) to implement it and to pass the shared ANA-06 contract suite.
- **§7 — publisher aggregates are T2** (strong model feature, never a rule) and **missing ≠ zero**: a verdict whose session has no resolvable publisher host produces **no placement row at all** — never a row keyed on `''` and never a fabricated 0. Empty-scope averages surface as `NaN`/`null`, never `0`.
- **D3 — <50 ms scoring budget**: nothing in this task runs on the request path. `RollupService` is a `BackgroundService`; the two new `/admin` endpoints read SQL only.
- **D11 — tenancy**: `RollupService` already uses `ISystemConnectionFactory.OpenSystemAsync` for the tenant enumeration only, then a per-tenant DI scope with `TenantContext.Resolve(...)`; repositories obtain connections only from `ITenantConnectionFactory`. Both new tables join `rls.TenantIsolationPolicy` in migration `0008`. Explicit `WHERE TenantId = @TenantId` stays in every statement.
- **D9/D10**: Dapper + `CommandDefinition` with the `CancellationToken`; DbUp versioned SQL scripts; no EF Core.
- **D1**: no server-side Node/Python.

## Parallel-execution seams (read before you touch a shared file)

P2-01..P2-05 are written to be implemented **in parallel by separate agents**. These are the only files this task shares with a sibling, and the rule for each:

| Shared file | Also touched by | Rule |
|---|---|---|
| `TelemetryGuard.Data/migrations/` | P2-02 | **P2-01 owns `0008`.** P2-02 owns `0009_model_registry.sql`. No other P2 task adds a SQL migration (P2-03/P2-04/P2-05 add none). Never renumber; never edit `0001`–`0007`. |
| `TelemetryGuard.Data/migrations/README.md` | P2-02 | Both append **one row each** to the number registry (`0008` here, `0009` there). Append-only — never rewrite existing rows. |
| `TelemetryGuard.Api/Endpoints/AdminEndpoints.cs` | P2-03 | Both add routes to the **same existing** `MapAdminEndpoints` group and both append private handlers. This task adds `/reports/publishers` + `/reports/sites`; P2-03 adds `/campaigns` + `/reports/flagged-sources`. Purely additive — add your two `MapGet` lines at the end of the existing list and your handlers at the end of the file so the two diffs do not overlap. |
| `TelemetryGuard.Api/Endpoints/AdminModels.cs` | P2-03 | Both **append** records at the end of the file. Do not reorder or edit existing records. |
| `TelemetryGuard.Api/appsettings.json` | P2-02, P2-05 | This task edits **only** the `"Rollup"` line. P2-02 edits only `"Scoring"`, P2-05 only `"Analytics"`. Stay inside your own section. |
| `TelemetryGuard.Analytics.Abstractions/IAnalyticsQueries.cs` | P2-05 (consumer) | This task **adds** the two intent methods. P2-05's Kusto provider must then implement them — its file carries a "P2-01 interlock" section with the KQL. This task writes no KQL and creates no Kusto file. |
| `tests/TelemetryGuard.Tests.Contracts/AnalyticsContractTests.cs`, `TestEvents.cs` | P2-05 (inheritor) | This task extends both. P2-05 does **not** modify them; it inherits whatever `[Fact]`s exist when it lands. |

Nothing in this task depends on P2-02/P2-03/P2-04/P2-05 having landed, and nothing here blocks them beyond the migration-number reservation above.

## Prerequisites

Read these files before writing code — every name below is copied from them and is binding.

### ANA-01 / ANA-04 — the analytics seam

`TelemetryGuard.Analytics.Abstractions/IAnalyticsQueries.cs` today:

```csharp
public interface IAnalyticsQueries
{
    Task<IpVelocityStats> GetIpVelocityAsync(string ip, TimeSpan window, CancellationToken ct);
    Task<CampaignFraudReport> GetCampaignReportAsync(string campaignId, DateRange range, CancellationToken ct);
    Task<IReadOnlyList<FlaggedSource>> GetTopFlaggedSourcesAsync(DateRange range, int limit, CancellationToken ct);
}
```

There is deliberately **no tenant parameter**: `ClickHouseAnalyticsQueries` (registered **scoped** by ANA-05) takes the tenant from the scoped `ITenantContext` and binds it as `{tenantId:UUID}`.

`DateRange` (`TelemetryGuard.Analytics.Abstractions/DateRange.cs`) is a half-open `[FromUtc, ToUtc)` and **throws** unless both bounds are `DateTimeKind.Utc`.

The existing implementation's null-semantics helpers — reuse them, do not re-invent:

```csharp
/// <summary>Nullable ClickHouse sums come back as DBNull over an empty scope; 0 is
/// the correct mergeable value for a sum (unlike averages, which stay NaN).</summary>
private static long ToInt64OrZero(object value) => value is DBNull ? 0L : Convert.ToInt64(value);

/// <summary>Missing != zero (§7): empty-scope averages surface as NaN, never 0.</summary>
private static double ToDoubleOrNaN(object value) => value is DBNull ? double.NaN : Convert.ToDouble(value);
```

### ANA-02 — the ClickHouse schema (NOT modified by this task)

`TelemetryGuard.Analytics.ClickHouse/schema/0001_events.sql`. The columns this task reads:

| column | type | note |
|---|---|---|
| `tenant_id` | `UUID` | first in `ORDER BY (tenant_id, timestamp)` |
| `site_key` | `LowCardinality(String)` | on **every** row (tracker/pixel/beacon/verdict) |
| `session_id` | `String` | the join key between a verdict and its capture row |
| `kind` | `LowCardinality(String)` | `'tracker'｜'pixel'｜'beacon'｜'verdict'` |
| `referrer` | `Nullable(String)` | **only** set on `tracker` (API-02) and `pixel` (API-03) rows |
| `has_js_beacon` | `UInt8` | |
| `score` | `Nullable(Int16)` | |
| `band` | `LowCardinality(Nullable(String))` | `VerdictBands.Allow/Challenge/Block` = `'allow'｜'challenge'｜'block'` |
| `timestamp` | `DateTime64(3, 'UTC')` | |

**Load-bearing fact, verified in code**: `TelemetryGuard.Api/Services/VerdictFinalizer.cs` builds the `kind='verdict'` `ClickEvent` **without** setting `Referrer` (it sets `TenantId, SiteKey, SessionId, Kind, CampaignId, Ip, HasJsBeacon, Score, Band, RuleHits, ScorerVersion, FeatureSetVersion, RetentionDays, TimestampUtc, Features, ShadowScore, ShadowScorerVersion`). Verdict rows therefore carry `referrer = NULL`. A placement query that filters `kind='verdict' AND referrer IS NOT NULL` returns **zero rows** — the session join below is mandatory, not an optimization. Do **not** "fix" this by adding `Referrer` to the verdict event; that changes ingest semantics and is out of scope.

`site_key` *is* on the verdict row (`VerdictFinalizer` resolves it from the Redis click hash, falling back to `ITenantContext.SiteKey`, then `""`), so the site aggregate needs no join.

### ANA-07 — the rollup host

`TelemetryGuard.Api/Workers/RollupService.cs`. Structure you are extending:

- `ExecuteAsync` → `PeriodicTimer(TimeSpan.FromMinutes(options.Value.IntervalMinutes))` → `RunOnceAsync(ct)`.
- `RunOnceAsync` — `Meter "TelemetryGuard.Rollup"`, counters `tg.rollup.runs`, `tg.rollup.tenants_processed`, `tg.rollup.tenant_failures`, `tg.rollup.rows_upserted`, histogram `tg.rollup.run_seconds`. Enumerates `SELECT TenantId FROM dbo.Tenants WHERE Status = 0` on `OpenSystemAsync`, then per tenant `try { await ProcessTenantAsync(tid, ct); ... } catch (Exception ex) { TenantFailures.Add(1); log.LogError(...); }`.
- `ProcessTenantAsync(Guid tid, CancellationToken ct)` — creates a DI scope, calls `sp.GetRequiredService<TenantContext>().Resolve(new TenantId(tid))`, then resolves `IAnalyticsQueries`, `IVerdictSummaryRepository`, `IRollupWatermarkRepository`, `ITenantConnectionFactory`.
- `internal static (DateTime fromDay, DateRange range) ComputeWindow(DateTime? watermarkUtc, DateTime nowUtc, int lookbackDays)` — pure; first run backfills `lookbackDays`, otherwise starts at `watermark.Date.AddDays(-1)`; normalizes every bound to `DateTimeKind.Utc`. **Reuse it as-is** for the new window.
- The existing tail: `await watermarks.SetAsync(rollupName, now, ct);  // advance ONLY after all upserts succeeded` then `RowsUpserted.Add(rows);`.

`TelemetryGuard.Api/Workers/RollupOptions.cs`:

```csharp
public sealed class RollupOptions
{
    public double IntervalMinutes { get; init; } = 15;
    public int LookbackDays { get; init; } = 3;
    public int TopFlaggedLimit { get; init; } = 100;
    public string RollupName { get; init; } = "verdict_daily";
}
```

### DAT-06 — the summary/watermark pattern to copy

`TelemetryGuard.Data/migrations/0003_summaries_exclusions.sql` — `dbo.FlaggedSourcesDaily` is the closest analog and the style to imitate (TenantId-leading clustered PK, `DF_<abbrev>_<Col>` default constraint names, `UpdatedUtc datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME())`, `CHECK` constraints, and the trailing `ALTER SECURITY POLICY rls.TenantIsolationPolicy ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON <table>, ADD BLOCK PREDICATE ... ON <table>;` block).

`dbo.RollupWatermarks` is keyed `(TenantId, RollupName varchar(64))` with `WatermarkUtc datetime2(3)`; `RollupName` examples in its own comment are `'verdict_daily'`, `'flagged_sources_daily'`. `IRollupWatermarkRepository` is `GetAsync(string rollupName, ct)` (null when never run) / `SetAsync(string rollupName, DateTime watermarkUtc, ct)`.

`TelemetryGuard.Data/Repositories/VerdictSummaryRepository.cs` is the Dapper template: `internal sealed class X(ITenantConnectionFactory connections, ITenantContext tenant) : IX`, `await using var conn = await connections.OpenAsync(ct);`, `await conn.ExecuteAsync(new CommandDefinition("""MERGE ... WITH (HOLDLOCK) ...""", new { ... }, cancellationToken: ct));`, an ambient-tenant guard that throws `InvalidOperationException` on `row.TenantId != tenant.TenantId.Value`, and private `…DbRow` materialization classes mapping SQL `date` → `DateTime` → `DateOnly`.

Registration lives in `TelemetryGuard.Data/DataServiceCollectionExtensions.cs` as `services.TryAddScoped<Repositories.IX, Repositories.X>();`.

### API-07 — the admin group

`TelemetryGuard.Api/Endpoints/AdminEndpoints.cs`:

```csharp
var admin = app.MapGroup("/admin").AddEndpointFilter<AdminScopeFilter>();
admin.MapGet("/reports/summary", GetSummaryAsync);
```

with `private const int MaxSummaryRangeDays = 366;`, `TryParseDate` (`"yyyy-MM-dd"`, `CultureInfo.InvariantCulture`), `ValidationProblem(field, message)`, and the **house precedent for missing ≠ zero on a report**:

```csharp
private static SummaryDayResponse ToSummaryDay(VerdictDailySummaryRow r)
    => new(r.Date, r.Events, r.Allowed, r.Challenged, r.Blocked,
           r.Events == 0 ? null : Math.Round((double)r.ScoreSum / r.Events, 1));
```

DTOs are `public sealed record`s in `TelemetryGuard.Api/Endpoints/AdminModels.cs` (camelCase JSON by default).

---

## Implementation steps

### 1. Two new DTOs in `TelemetryGuard.Analytics.Abstractions`

`PlacementDailyCounts.cs`:

```csharp
namespace TelemetryGuard.Analytics.Abstractions;

/// <summary>
/// One (day, publisher placement) bucket of VERDICT rows, as answered by
/// <see cref="IAnalyticsQueries.GetTopPlacementsDailyAsync"/>.
///
/// PLACEMENT IDENTITY (P2-01): tg_events has no placement column. A verdict is
/// attributed to the normalized publisher host of its OWN session's earliest
/// capture row (kind 'tracker' or 'pixel' — the only kinds that carry a
/// referrer). Sessions with no capture row, or no referrer on it, produce NO
/// bucket at all (§7: missing != zero — never a '' bucket, never a 0 row).
/// </summary>
public sealed record PlacementDailyCounts(
    DateOnly Day,
    string Placement,          // lowercase host, leading "www." stripped, <= 253 chars
    long ScoredEvents,         // verdict rows attributed to this placement on this day
    long Allowed,
    long Challenged,
    long Blocked,
    long ScoreSum,             // sum of scores; 0 when none (mergeable — never an average)
    double AvgScore,           // NaN when ScoredEvents == 0 (missing != zero)
    long NoJsBeaconCount);     // verdicts with has_js_beacon = 0
```

`SiteDailyCounts.cs`:

```csharp
namespace TelemetryGuard.Analytics.Abstractions;

/// <summary>
/// One (day, site_key) bucket, as answered by
/// <see cref="IAnalyticsQueries.GetSiteDailyCountsAsync"/>. This is the
/// NON-CAMPAIGN view that dbo.VerdictDailySummaries (keyed on CampaignId)
/// cannot express: pixel-mode and organic sessions carry a site key but no
/// campaign. TotalEvents counts EVERY kind, so it is also the honest
/// denominator for "how much of this site's traffic has no resolvable
/// placement" (site TotalEvents minus the placement rows' ScoredEvents).
/// </summary>
public sealed record SiteDailyCounts(
    DateOnly Day,
    string SiteKey,
    long TotalEvents,          // all kinds: tracker + pixel + beacon + verdict
    long ScoredEvents,         // kind = 'verdict'
    long Allowed,
    long Challenged,
    long Blocked,
    long ScoreSum,             // 0 when none (mergeable)
    double AvgScore,           // NaN when ScoredEvents == 0
    long NoJsBeaconCount);
```

### 2. Extend `IAnalyticsQueries` — by intent (D7)

Append to `TelemetryGuard.Analytics.Abstractions/IAnalyticsQueries.cs` (keep the existing three methods and the interface doc comment untouched):

```csharp
    /// <summary>
    /// Top publisher placements per UTC day inside <paramref name="range"/>,
    /// ranked by ScoredEvents descending, capped at <paramref name="limitPerDay"/>
    /// buckets PER DAY (not per range).
    ///
    /// PROVIDER CONTRACT (binding on every implementation, incl. P2-05's Kusto):
    ///  1. Only kind = 'verdict' rows are counted.
    ///  2. A verdict's placement is the normalized publisher host of the EARLIEST
    ///     capture row (kind 'tracker' or 'pixel') sharing its tenant + session_id.
    ///     Normalized = host only, lowercased, leading "www." stripped.
    ///  3. The capture-row lookup must look back at least ONE HOUR before
    ///     range.FromUtc, because a session's tracker hit precedes its verdict by
    ///     the API-02 grace period and may fall on the previous day.
    ///  4. Verdicts with no such capture row, an empty/unparseable referrer, or a
    ///     host longer than 253 chars are OMITTED entirely (§7: missing != zero).
    /// Throws ArgumentOutOfRangeException when limitPerDay &lt;= 0.
    /// </summary>
    Task<IReadOnlyList<PlacementDailyCounts>> GetTopPlacementsDailyAsync(
        DateRange range, int limitPerDay, CancellationToken ct);

    /// <summary>
    /// Per-site (site_key), per-UTC-day counts over every event kind inside
    /// <paramref name="range"/>, ordered by day then site key. Rows with an empty
    /// site_key are omitted. This is the non-campaign traffic view (D23).
    /// </summary>
    Task<IReadOnlyList<SiteDailyCounts>> GetSiteDailyCountsAsync(
        DateRange range, CancellationToken ct);
```

### 3. ClickHouse implementation

In `TelemetryGuard.Analytics.ClickHouse/ClickHouseAnalyticsQueries.cs`, add a constant next to the field `_cs` and the two methods below. Follow the existing shape exactly: `new ClickHouseConnection(_cs)` → `OpenAsync(ct)` → `conn.CreateCommand()` → raw-string `CommandText` → `cmd.AddParameter(...)` → `ExecuteReaderAsync(ct)`.

```csharp
    /// <summary>P2-01: a session's tracker/pixel hit precedes its verdict by the
    /// API-02 grace period (~10 s) and can land on the previous UTC day. One hour
    /// of lookbehind on the capture side is generous and keeps the scan bounded.</summary>
    private static readonly TimeSpan SessionJoinLookbehind = TimeSpan.FromHours(1);
```

```csharp
    public async Task<IReadOnlyList<PlacementDailyCounts>> GetTopPlacementsDailyAsync(
        DateRange range, int limitPerDay, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limitPerDay);

        await using var conn = new ClickHouseConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // The inner subquery resolves ONE placement per session (argMin by timestamp
        // = the earliest capture row's referrer). ifNull keeps the expression a plain
        // String so domainWithoutWWW/lower never propagate Nullable into HAVING.
        // BOTH sides filter tenant_id, so the join can never cross tenants (D11).
        cmd.CommandText =
            """
            SELECT
                toDate(v.timestamp)                    AS day,
                p.placement                            AS placement,
                count()                                AS scored_events,
                countIf(v.band = 'allow')              AS allowed,
                countIf(v.band = 'challenge')          AS challenged,
                countIf(v.band = 'block')              AS blocked,
                sumIf(v.score, isNotNull(v.score))     AS score_sum,
                avgIf(v.score, isNotNull(v.score))     AS avg_score,
                countIf(v.has_js_beacon = 0)           AS no_js_beacon
            FROM tg_events AS v
            INNER JOIN
            (
                SELECT
                    session_id,
                    lower(domainWithoutWWW(argMin(ifNull(referrer, ''), timestamp))) AS placement
                FROM tg_events
                WHERE tenant_id = {tenantId:UUID}
                  AND kind IN ('tracker', 'pixel')
                  AND referrer IS NOT NULL
                  AND referrer != ''
                  AND timestamp >= {captureFromTs:DateTime64(3)}
                  AND timestamp <  {toTs:DateTime64(3)}
                GROUP BY session_id
                HAVING placement != '' AND length(placement) <= 253
            ) AS p ON v.session_id = p.session_id
            WHERE v.tenant_id = {tenantId:UUID}
              AND v.kind = 'verdict'
              AND v.timestamp >= {fromTs:DateTime64(3)}
              AND v.timestamp <  {toTs:DateTime64(3)}
            GROUP BY day, placement
            ORDER BY day ASC, scored_events DESC, placement ASC
            LIMIT {limitPerDay:Int32} BY day
            """;
        cmd.AddParameter("tenantId", tenant.TenantId.Value);
        cmd.AddParameter("captureFromTs", range.FromUtc - SessionJoinLookbehind);
        cmd.AddParameter("fromTs", range.FromUtc);
        cmd.AddParameter("toTs", range.ToUtc);
        cmd.AddParameter("limitPerDay", limitPerDay);

        var rows = new List<PlacementDailyCounts>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            rows.Add(new PlacementDailyCounts(
                Day: DateOnly.FromDateTime(Convert.ToDateTime(r["day"])),
                Placement: Convert.ToString(r["placement"]) ?? "",
                ScoredEvents: Convert.ToInt64(r["scored_events"]),
                Allowed: Convert.ToInt64(r["allowed"]),
                Challenged: Convert.ToInt64(r["challenged"]),
                Blocked: Convert.ToInt64(r["blocked"]),
                ScoreSum: ToInt64OrZero(r["score_sum"]),
                AvgScore: ToDoubleOrNaN(r["avg_score"]),
                NoJsBeaconCount: Convert.ToInt64(r["no_js_beacon"])));
        }
        return rows;
    }

    public async Task<IReadOnlyList<SiteDailyCounts>> GetSiteDailyCountsAsync(
        DateRange range, CancellationToken ct)
    {
        await using var conn = new ClickHouseConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // Same conditional-aggregate shape as GetCampaignReportAsync, grouped on
        // site_key instead of campaign_id: TotalEvents spans every kind.
        cmd.CommandText =
            """
            SELECT
                toDate(timestamp)                                   AS day,
                site_key                                            AS site_key,
                count()                                             AS total_events,
                countIf(kind = 'verdict')                           AS scored_events,
                countIf(kind = 'verdict' AND band = 'allow')        AS allowed,
                countIf(kind = 'verdict' AND band = 'challenge')    AS challenged,
                countIf(kind = 'verdict' AND band = 'block')        AS blocked,
                sumIf(score, kind = 'verdict' AND isNotNull(score)) AS score_sum,
                avgIf(score, kind = 'verdict' AND isNotNull(score)) AS avg_score,
                countIf(kind = 'verdict' AND has_js_beacon = 0)     AS no_js_beacon
            FROM tg_events
            WHERE tenant_id = {tenantId:UUID}
              AND site_key != ''
              AND timestamp >= {fromTs:DateTime64(3)}
              AND timestamp <  {toTs:DateTime64(3)}
            GROUP BY day, site_key
            ORDER BY day ASC, site_key ASC
            """;
        cmd.AddParameter("tenantId", tenant.TenantId.Value);
        cmd.AddParameter("fromTs", range.FromUtc);
        cmd.AddParameter("toTs", range.ToUtc);

        var rows = new List<SiteDailyCounts>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            rows.Add(new SiteDailyCounts(
                Day: DateOnly.FromDateTime(Convert.ToDateTime(r["day"])),
                SiteKey: Convert.ToString(r["site_key"]) ?? "",
                TotalEvents: Convert.ToInt64(r["total_events"]),
                ScoredEvents: Convert.ToInt64(r["scored_events"]),
                Allowed: Convert.ToInt64(r["allowed"]),
                Challenged: Convert.ToInt64(r["challenged"]),
                Blocked: Convert.ToInt64(r["blocked"]),
                ScoreSum: ToInt64OrZero(r["score_sum"]),
                AvgScore: ToDoubleOrNaN(r["avg_score"]),
                NoJsBeaconCount: Convert.ToInt64(r["no_js_beacon"])));
        }
        return rows;
    }
```

Notes for the implementer:
- `domainWithoutWWW` and `LIMIT n BY expr` are stock ClickHouse (the test image is `clickhouse/clickhouse-server:24.8`). `LIMIT n BY day` must follow the `ORDER BY`.
- No ClickHouse schema change: **do not** add a migration under `TelemetryGuard.Analytics.ClickHouse/schema/`.

### 4. Migration `0008_publisher_site_summaries.sql`

New file `TelemetryGuard.Data/migrations/0008_publisher_site_summaries.sql` (this task owns number `0008`; `0007_meta_sync.sql` is the current highest, and P2-02 has reserved `0009` — do not take it):

```sql
------------------------------------------------------------------------------
-- 0008_publisher_site_summaries.sql  (P2-01)
-- D23 split: two more SQL Server AGGREGATE tables materialized from ClickHouse
-- by the ANA-07 RollupService. Raw events stay in ClickHouse. Both tables are
-- tenant-scoped => RLS FILTER + BLOCK at the bottom (the DAT-03 rule).
--
-- MERGEABILITY CONTRACT: every numeric column here is summable across rows.
-- Averages/ratios are NEVER stored (readers compute ScoreSum/Events); distinct
-- counts are NEVER stored (they cannot be summed across days without lying).
------------------------------------------------------------------------------

-- Per-tenant/publisher-placement/day verdict counts. Placement = the normalized
-- publisher host (lowercase, leading "www." stripped) of the session's earliest
-- tracker/pixel referrer -- see IAnalyticsQueries.GetTopPlacementsDailyAsync.
-- A verdict with no resolvable placement produces NO ROW (spec §7: missing != zero);
-- the unattributed remainder is derivable from dbo.SiteDailySummaries.TotalEvents.
CREATE TABLE dbo.PublisherDailySummaries
(
    TenantId        uniqueidentifier NOT NULL,
    [Date]          date             NOT NULL,
    Placement       varchar(256)     NOT NULL,   -- host only; max DNS name is 253 chars
    Events          int              NOT NULL CONSTRAINT DF_PDS_Events DEFAULT (0),      -- scored (verdict) events
    Allowed         int              NOT NULL CONSTRAINT DF_PDS_Allowed DEFAULT (0),     -- score 0-30
    Challenged      int              NOT NULL CONSTRAINT DF_PDS_Challenged DEFAULT (0),  -- score 31-70
    Blocked         int              NOT NULL CONSTRAINT DF_PDS_Blocked DEFAULT (0),     -- score 71-100
    ScoreSum        bigint           NOT NULL CONSTRAINT DF_PDS_ScoreSum DEFAULT (0),    -- mean = ScoreSum/Events
    NoJsBeaconCount int              NOT NULL CONSTRAINT DF_PDS_NoJsBeacon DEFAULT (0),
    UpdatedUtc      datetime2(3)     NOT NULL CONSTRAINT DF_PDS_UpdatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_PublisherDailySummaries PRIMARY KEY CLUSTERED (TenantId, [Date], Placement),
    CONSTRAINT CK_PDS_Placement CHECK (LEN(Placement) > 0)
);
GO

-- Per-tenant/site/day counts: the NON-CAMPAIGN view (dbo.VerdictDailySummaries is
-- keyed on CampaignId and cannot express pixel-mode/organic traffic).
-- TotalEvents spans EVERY event kind; Events counts verdicts only.
CREATE TABLE dbo.SiteDailySummaries
(
    TenantId        uniqueidentifier NOT NULL,
    [Date]          date             NOT NULL,
    SiteKey         varchar(64)      NOT NULL,   -- matches dbo.Sites.SiteKey
    TotalEvents     int              NOT NULL CONSTRAINT DF_SDS_TotalEvents DEFAULT (0),
    Events          int              NOT NULL CONSTRAINT DF_SDS_Events DEFAULT (0),
    Allowed         int              NOT NULL CONSTRAINT DF_SDS_Allowed DEFAULT (0),
    Challenged      int              NOT NULL CONSTRAINT DF_SDS_Challenged DEFAULT (0),
    Blocked         int              NOT NULL CONSTRAINT DF_SDS_Blocked DEFAULT (0),
    ScoreSum        bigint           NOT NULL CONSTRAINT DF_SDS_ScoreSum DEFAULT (0),
    NoJsBeaconCount int              NOT NULL CONSTRAINT DF_SDS_NoJsBeacon DEFAULT (0),
    UpdatedUtc      datetime2(3)     NOT NULL CONSTRAINT DF_SDS_UpdatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_SiteDailySummaries PRIMARY KEY CLUSTERED (TenantId, [Date], SiteKey)
);
GO

-- D11 RULE: new tenant tables join the RLS policy in their own migration.
-- (dbo.Sites is RLS-EXEMPT as a resolution table -- that exemption does NOT
-- extend to these aggregates.)
ALTER SECURITY POLICY rls.TenantIsolationPolicy
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.PublisherDailySummaries,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.PublisherDailySummaries,
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.SiteDailySummaries,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.SiteDailySummaries;
GO
```

Append to the registry table in `TelemetryGuard.Data/migrations/README.md`:

```
| 0008 | PublisherDailySummaries + SiteDailySummaries (publisher/site aggregates) + RLS | P2-01 |
```

Both are `EmbeddedResource` via the existing `TelemetryGuard.Data.csproj` glob — no csproj edit is required; verify with `grep -n "migrations" TelemetryGuard.Data/TelemetryGuard.Data.csproj` before assuming otherwise.

### 5. Row records

Append to `TelemetryGuard.Data/Models/SummaryRecords.cs`:

```csharp
/// <summary>One dbo.PublisherDailySummaries row. Every numeric field is summable
/// across rows (absolute values, never averages) so rollup replays converge.</summary>
public sealed record PublisherDailySummaryRow(
    Guid TenantId, DateOnly Date, string Placement,
    int Events, int Allowed, int Challenged, int Blocked, long ScoreSum, int NoJsBeaconCount);

/// <summary>One dbo.SiteDailySummaries row. TotalEvents = all kinds; Events = verdicts.</summary>
public sealed record SiteDailySummaryRow(
    Guid TenantId, DateOnly Date, string SiteKey,
    int TotalEvents, int Events, int Allowed, int Challenged, int Blocked,
    long ScoreSum, int NoJsBeaconCount);

/// <summary>Read model for GET /admin/reports/publishers: one placement's totals
/// summed over an inclusive date range. FirstDay/LastDay bound the observed
/// activity so a reader can tell a 1-day spike from steady traffic.</summary>
public sealed record PlacementRangeTotalsRow(
    string Placement, int Events, int Allowed, int Challenged, int Blocked,
    long ScoreSum, int NoJsBeaconCount, DateOnly FirstDay, DateOnly LastDay);
```

### 6. Repository `IPublisherSummaryRepository` / `PublisherSummaryRepository`

New file `TelemetryGuard.Data/Repositories/IPublisherSummaryRepository.cs`:

```csharp
using TelemetryGuard.Data.Models;

namespace TelemetryGuard.Data.Repositories;

/// <summary>
/// Writes/reads the P2-01 aggregate tables dbo.PublisherDailySummaries and
/// dbo.SiteDailySummaries. Upserts are idempotent ABSOLUTE-VALUE MERGEs — never
/// increments — so ANA-07 rollup replays converge instead of double-counting
/// (same contract as DAT-06's IVerdictSummaryRepository). There is deliberately
/// no live-path increment counterpart: nothing on the request path writes here.
/// </summary>
public interface IPublisherSummaryRepository
{
    /// <summary>Idempotent absolute-value MERGE. Row.TenantId must equal the ambient tenant.</summary>
    Task UpsertPlacementDailyAsync(PublisherDailySummaryRow row, CancellationToken ct);

    /// <summary>Idempotent absolute-value MERGE. Row.TenantId must equal the ambient tenant.</summary>
    Task UpsertSiteDailyAsync(SiteDailySummaryRow row, CancellationToken ct);

    /// <summary>Top placements over an INCLUSIVE date range, totals summed per
    /// placement, ordered by Blocked desc then (Challenged + Blocked) desc.
    /// limit must be 1..1000.</summary>
    Task<IReadOnlyList<PlacementRangeTotalsRow>> GetTopPlacementsAsync(
        DateOnly from, DateOnly to, int limit, CancellationToken ct);

    /// <summary>Per-site daily rows over an INCLUSIVE date range, ordered by date then site key.</summary>
    Task<IReadOnlyList<SiteDailySummaryRow>> GetSiteDailyAsync(
        DateOnly from, DateOnly to, CancellationToken ct);
}
```

New file `TelemetryGuard.Data/Repositories/PublisherSummaryRepository.cs` — mirror `VerdictSummaryRepository` exactly (`internal sealed class`, ctor `(ITenantConnectionFactory connections, ITenantContext tenant)`, `CommandDefinition` with `cancellationToken: ct`, ambient-tenant guard throwing `InvalidOperationException`, private `…DbRow` classes for the reads). The two upserts:

```csharp
            MERGE dbo.PublisherDailySummaries WITH (HOLDLOCK) AS t
            USING (SELECT @TenantId AS TenantId, @Date AS [Date], @Placement AS Placement) AS s
                ON t.TenantId = s.TenantId AND t.[Date] = s.[Date] AND t.Placement = s.Placement
            WHEN MATCHED THEN UPDATE SET
                Events = @Events, Allowed = @Allowed, Challenged = @Challenged, Blocked = @Blocked,
                ScoreSum = @ScoreSum, NoJsBeaconCount = @NoJsBeaconCount, UpdatedUtc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT
                (TenantId, [Date], Placement, Events, Allowed, Challenged, Blocked, ScoreSum, NoJsBeaconCount)
                VALUES (@TenantId, @Date, @Placement, @Events, @Allowed, @Challenged, @Blocked, @ScoreSum, @NoJsBeaconCount);
```

```csharp
            MERGE dbo.SiteDailySummaries WITH (HOLDLOCK) AS t
            USING (SELECT @TenantId AS TenantId, @Date AS [Date], @SiteKey AS SiteKey) AS s
                ON t.TenantId = s.TenantId AND t.[Date] = s.[Date] AND t.SiteKey = s.SiteKey
            WHEN MATCHED THEN UPDATE SET
                TotalEvents = @TotalEvents, Events = @Events, Allowed = @Allowed,
                Challenged = @Challenged, Blocked = @Blocked, ScoreSum = @ScoreSum,
                NoJsBeaconCount = @NoJsBeaconCount, UpdatedUtc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT
                (TenantId, [Date], SiteKey, TotalEvents, Events, Allowed, Challenged, Blocked, ScoreSum, NoJsBeaconCount)
                VALUES (@TenantId, @Date, @SiteKey, @TotalEvents, @Events, @Allowed, @Challenged, @Blocked, @ScoreSum, @NoJsBeaconCount);
```

The range read (validate `limit is not (> 0 and <= 1000)` → `ArgumentOutOfRangeException`, same as `GetTopFlaggedSourcesAsync`):

```csharp
            SELECT TOP (@Limit)
                Placement,
                SUM(Events)          AS Events,
                SUM(Allowed)         AS Allowed,
                SUM(Challenged)      AS Challenged,
                SUM(Blocked)         AS Blocked,
                SUM(ScoreSum)        AS ScoreSum,
                SUM(NoJsBeaconCount) AS NoJsBeaconCount,
                MIN([Date])          AS FirstDay,
                MAX([Date])          AS LastDay
            FROM dbo.PublisherDailySummaries
            WHERE TenantId = @TenantId AND [Date] BETWEEN @From AND @To
            GROUP BY Placement
            ORDER BY SUM(Blocked) DESC, SUM(Challenged) + SUM(Blocked) DESC, Placement ASC;
```

```csharp
            SELECT TenantId, [Date], SiteKey, TotalEvents, Events, Allowed, Challenged,
                   Blocked, ScoreSum, NoJsBeaconCount
            FROM dbo.SiteDailySummaries
            WHERE TenantId = @TenantId AND [Date] BETWEEN @From AND @To
            ORDER BY [Date], SiteKey;
```

`SUM(int)` returns `int` in SQL Server (overflow throws) — map `Events/Allowed/Challenged/Blocked/NoJsBeaconCount` to `int` and `SUM(ScoreSum)` (bigint) to `long` on the `DbRow`. Map `date` columns through `DateTime` then `DateOnly.FromDateTime(...)`, exactly as `VerdictSummaryRepository` does.

Register in `TelemetryGuard.Data/DataServiceCollectionExtensions.cs`, next to the DAT-06 block:

```csharp
        // P2-01 publisher/site aggregate repository — scoped like the DAT-06 pair above.
        services.TryAddScoped<Repositories.IPublisherSummaryRepository, Repositories.PublisherSummaryRepository>();
```

### 7. Rollup wiring

**`RollupOptions.cs`** gains two members (keep the existing four unchanged):

```csharp
    public int TopPlacementsLimit { get; init; } = 100;             // placements per tenant per DAY
    public string PublisherRollupName { get; init; } = "publisher_daily"; // second key into dbo.RollupWatermarks
```

`TelemetryGuard.Api/appsettings.json` — the `Rollup` line becomes:

```json
  "Rollup": { "IntervalMinutes": 15, "LookbackDays": 3, "TopFlaggedLimit": 100, "TopPlacementsLimit": 100 },
```

(`RollupName` is already absent from appsettings; leave `PublisherRollupName` a code-only default for the same reason.)

**`RollupService.ProcessTenantAsync`** — insert the new section **before** the existing `await watermarks.SetAsync(rollupName, now, ct);` line, so each rollup owns its own watermark and neither blocks the other. Resolve the new repository alongside the existing ones:

```csharp
        var publishers  = sp.GetRequiredService<IPublisherSummaryRepository>(); // P2-01, scoped
```

Then, after the existing flagged-sources `for` loop:

```csharp
        // ---- P2-01: publisher/placement + site (non-campaign) aggregates ----
        // Its OWN watermark: on first deploy this rollup must backfill LookbackDays
        // independently of the already-advanced 'verdict_daily' watermark, otherwise
        // the new tables would silently start at "now" with no history.
        var pubName = options.Value.PublisherRollupName;
        var pubWatermark = await watermarks.GetAsync(pubName, ct);
        var (_, pubRange) = ComputeWindow(pubWatermark, now, options.Value.LookbackDays);

        // Self-referral filter: a session's first capture row on a tenant's OWN
        // landing page yields that tenant's own domain as a "placement", which would
        // top every report. dbo.Sites is RLS-EXEMPT (a resolution table), so the
        // explicit TenantId predicate here is load-bearing, not just an index hint.
        HashSet<string> ownDomains;
        await using (var conn = await connFactory.OpenAsync(ct))
        {
            var domains = await conn.QueryAsync<string>(new CommandDefinition(
                "SELECT Domain FROM dbo.Sites WHERE TenantId = @TenantId",
                new { TenantId = tid }, cancellationToken: ct));
            ownDomains = domains.Select(NormalizeHost)
                                .Where(d => d.Length > 0)
                                .ToHashSet(StringComparer.Ordinal);
        }

        var placements = await queries.GetTopPlacementsDailyAsync(
            pubRange, options.Value.TopPlacementsLimit, ct);
        foreach (var p in placements)
        {
            if (ownDomains.Contains(p.Placement)) continue;   // tenant's own site, not a publisher
            await publishers.UpsertPlacementDailyAsync(new PublisherDailySummaryRow(
                TenantId: tid, Date: p.Day, Placement: p.Placement,
                Events: checked((int)p.ScoredEvents),
                Allowed: checked((int)p.Allowed),
                Challenged: checked((int)p.Challenged),
                Blocked: checked((int)p.Blocked),
                ScoreSum: p.ScoreSum,                          // AvgScore (NaN included) is never stored
                NoJsBeaconCount: checked((int)p.NoJsBeaconCount)), ct);
            rows++;
        }

        var sites = await queries.GetSiteDailyCountsAsync(pubRange, ct);
        foreach (var s in sites)
        {
            await publishers.UpsertSiteDailyAsync(new SiteDailySummaryRow(
                TenantId: tid, Date: s.Day, SiteKey: s.SiteKey,
                TotalEvents: checked((int)s.TotalEvents),
                Events: checked((int)s.ScoredEvents),
                Allowed: checked((int)s.Allowed),
                Challenged: checked((int)s.Challenged),
                Blocked: checked((int)s.Blocked),
                ScoreSum: s.ScoreSum,
                NoJsBeaconCount: checked((int)s.NoJsBeaconCount)), ct);
            rows++;
        }

        await watermarks.SetAsync(pubName, now, ct);  // advance ONLY after these upserts succeeded
```

Add the pure helper next to `ComputeWindow` (unit-tested — see Testing):

```csharp
    /// <summary>
    /// P2-01 host normalization, identical on both sides of the self-referral
    /// filter: trim, lowercase (invariant), drop any scheme/path/port a configured
    /// dbo.Sites.Domain may carry, then strip a leading "www.". Must match what
    /// lower(domainWithoutWWW(...)) produces in the ClickHouse query.
    /// </summary>
    internal static string NormalizeHost(string? value)
    {
        var s = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (s.Length == 0) return string.Empty;
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        var slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];
        var colon = s.IndexOf(':');
        if (colon >= 0) s = s[..colon];
        if (s.StartsWith("www.", StringComparison.Ordinal)) s = s[4..];
        return s;
    }
```

Required `using`s in `RollupService.cs`: `Dapper` and `TelemetryGuard.Data.Models` are already imported; add none unless the compiler asks.

**Metrics**: reuse the existing `RowsUpserted` counter via `rows++` (above). Do not add a new `Meter`.

### 8. Admin reports (API-07 group)

`TelemetryGuard.Api/Endpoints/AdminModels.cs` — append:

```csharp
public sealed record PublisherReportRowResponse(
    string Placement, int Events, int Allowed, int Challenged, int Blocked,
    int Flagged,               // Challenged + Blocked
    double? FlaggedRatio,      // Flagged / Events; null when Events == 0 (missing != zero)
    double? AvgScore,          // ScoreSum / Events; null when Events == 0
    int NoJsBeaconCount,
    bool LowVolume,            // Events < LowVolumePlacementEvents — read the ratio with care
    DateOnly FirstDay, DateOnly LastDay);

public sealed record PublisherReportResponse(
    DateOnly From, DateOnly To, IReadOnlyList<PublisherReportRowResponse> Rows);

public sealed record SiteReportDayResponse(
    DateOnly Date, string SiteKey, int TotalEvents, int Events,
    int Allowed, int Challenged, int Blocked, double? AvgScore, int NoJsBeaconCount);

public sealed record SiteReportResponse(
    DateOnly From, DateOnly To, IReadOnlyList<SiteReportDayResponse> Rows);
```

`TelemetryGuard.Api/Endpoints/AdminEndpoints.cs` — two more routes in the same group, plus one const:

```csharp
        admin.MapGet("/reports/publishers", GetPublisherReportAsync);
        admin.MapGet("/reports/sites", GetSiteReportAsync);
```

```csharp
    /// <summary>Below this many scored events a placement's FlaggedRatio is noise;
    /// the response flags it rather than hiding the row (§7 — surface the volume,
    /// never fabricate confidence).</summary>
    private const int LowVolumePlacementEvents = 30;
```

```csharp
    private static async Task<IResult> GetPublisherReportAsync(
        string? from, string? to, int? limit,
        IPublisherSummaryRepository publishers, CancellationToken ct)
    {
        if (from is null || !TryParseDate(from, out var fromDate))
            return ValidationProblem("from", "from is required and must be yyyy-MM-dd.");
        if (to is null || !TryParseDate(to, out var toDate))
            return ValidationProblem("to", "to is required and must be yyyy-MM-dd.");
        if (fromDate > toDate)
            return ValidationProblem("from", "from must be <= to.");
        if (toDate.DayNumber - fromDate.DayNumber > MaxSummaryRangeDays)
            return ValidationProblem("to", $"date range must not exceed {MaxSummaryRangeDays} days.");

        var effectiveLimit = Math.Clamp(limit ?? 50, 1, 200);
        var rows = await publishers.GetTopPlacementsAsync(fromDate, toDate, effectiveLimit, ct);
        return Results.Ok(new PublisherReportResponse(
            fromDate, toDate, rows.Select(ToPublisherRow).ToList()));
    }

    private static PublisherReportRowResponse ToPublisherRow(PlacementRangeTotalsRow r)
    {
        var flagged = r.Challenged + r.Blocked;
        return new PublisherReportRowResponse(
            r.Placement, r.Events, r.Allowed, r.Challenged, r.Blocked, flagged,
            r.Events == 0 ? null : Math.Round((double)flagged / r.Events, 4),
            r.Events == 0 ? null : Math.Round((double)r.ScoreSum / r.Events, 1),
            r.NoJsBeaconCount, r.Events < LowVolumePlacementEvents,
            r.FirstDay, r.LastDay);
    }
```

`GetSiteReportAsync` follows the same validation block (no `limit`), calls `GetSiteDailyAsync`, and maps each `SiteDailySummaryRow` with the same `Events == 0 ? null : Math.Round((double)r.ScoreSum / r.Events, 1)` rule.

Update the class-level doc comment on `AdminEndpoints` so the D23 sentence covers the new repository: *"reads here go ONLY through the small RLS-protected SQL aggregate tables (`IVerdictSummaryRepository`, `IPublisherSummaryRepository`) — never ClickHouse/`IAnalyticsQueries`."*

No `Program.cs` change is needed: `MapAdminEndpoints()` is already called, and `AddTelemetryGuardData()` already runs.

### 9. Update the existing `IAnalyticsQueries` fakes (compile-breaking)

`tests/TelemetryGuard.Tests.Integration/RollupServiceTests.cs` contains the **only** other implementation of the interface, `private sealed class ThrowingAnalyticsQueries : IAnalyticsQueries`. Add the two new methods to it, throwing the same `InvalidOperationException("Deliberate per-tenant analytics failure (test).")`. Without this the Integration project does not compile.

The same file's `RollupServiceTests` makes **two** `dbo.RollupWatermarks` assertions that a second watermark name breaks. Both must be updated — grep the file for `watermarks` and fix every hit, do not stop at the first:

1. the single-tenant test's `Assert.Equal((RollupName: "verdict_daily", WatermarkUtc: Now), Assert.Single(watermarks));` — `Assert.Single` now throws, because that tenant has **two** rows. Replace with a lookup by `RollupName` asserting both `'verdict_daily'` and `'publisher_daily'` sit at `Now`.
2. the multi-tenant test's `Assert.Equal(3, watermarks.Count);` + `Assert.All(watermarks, w => Assert.Equal("verdict_daily", w.RollupName));` — there are now **two rows per active tenant (6 total)**. Group by `RollupName` and assert both names exist per tenant, and that in the failure run tenant D advanced **neither**.

---

## Files to create or modify

**Create**
- `TelemetryGuard.Analytics.Abstractions/PlacementDailyCounts.cs`
- `TelemetryGuard.Analytics.Abstractions/SiteDailyCounts.cs`
- `TelemetryGuard.Data/migrations/0008_publisher_site_summaries.sql`
- `TelemetryGuard.Data/Repositories/IPublisherSummaryRepository.cs`
- `TelemetryGuard.Data/Repositories/PublisherSummaryRepository.cs`
- `tests/TelemetryGuard.Tests.Integration/Sql/PublisherSummaryRepositoryTests.cs`

**Modify**
- `TelemetryGuard.Analytics.Abstractions/IAnalyticsQueries.cs` (two intent-named methods)
- `TelemetryGuard.Analytics.ClickHouse/ClickHouseAnalyticsQueries.cs` (two implementations + `SessionJoinLookbehind`)
- `TelemetryGuard.Data/Models/SummaryRecords.cs` (three records)
- `TelemetryGuard.Data/DataServiceCollectionExtensions.cs` (one `TryAddScoped`)
- `TelemetryGuard.Data/migrations/README.md` (registry row `0008`)
- `TelemetryGuard.Api/Workers/RollupOptions.cs` (`TopPlacementsLimit`, `PublisherRollupName`)
- `TelemetryGuard.Api/Workers/RollupService.cs` (new section + `NormalizeHost`)
- `TelemetryGuard.Api/Endpoints/AdminEndpoints.cs` (two routes + handlers + const + doc comment)
- `TelemetryGuard.Api/Endpoints/AdminModels.cs` (four response records)
- `TelemetryGuard.Api/appsettings.json` (`Rollup.TopPlacementsLimit`)
- `tests/TelemetryGuard.Tests.Contracts/TestEvents.cs` (add `siteKey` + `referrer` parameters)
- `tests/TelemetryGuard.Tests.Contracts/AnalyticsContractTests.cs` (new contract tests)
- `tests/TelemetryGuard.Tests.Contracts/README.md` (its layout table says *"The abstract, provider-agnostic suite (8 tests)"* — update the count; **that one cell only**, since P2-05 rewrites this file's "Adding a provider" section in parallel)
- `tests/TelemetryGuard.Tests.Integration/TestEvents.cs` (add `siteKey` + `referrer` parameters)
- `tests/TelemetryGuard.Tests.Integration/ClickHouseAnalyticsQueriesTests.cs` (provider-specific cases)
- `tests/TelemetryGuard.Tests.Integration/RollupServiceTests.cs` (fake + watermark assertions + new expectations)
- `tests/TelemetryGuard.Tests.Integration/Api/AdminEndpointTests.cs` (end-to-end report reads)
- `tests/TelemetryGuard.Tests.Unit/Api/RollupServiceTests.cs` (`NormalizeHost` cases)
- `tests/TelemetryGuard.Tests.Unit/Api/AdminEndpointTests.cs` (fake repo + handler math/validation)

**Deliberately NOT touched** — call it out in the PR description if you think otherwise:
- `TelemetryGuard.sln` (no new projects), `.github/workflows/ci.yml` (the existing three `dotnet test` invocations already cover the new tests), `.gitignore`, `docker-compose.yml`, `TelemetryGuard.Api/Program.cs`, any `*.csproj`, any file under `TelemetryGuard.Analytics.ClickHouse/schema/`, `TelemetryGuard.Sdk/**`, `TelemetryGuard.RiskEngine*/**`, `TelemetryGuard.Training/**`, `doc/spec.md`, `doc/plan.md`, `CLAUDE.md`, and every already-applied migration `0001`–`0007`.
- Any sibling P2 task's new project — `TelemetryGuard.Portal/**` (P2-03) and `TelemetryGuard.Analytics.Kusto/**` (P2-05) — even if it already exists in your tree.

---

## Acceptance criteria

- `dotnet build TelemetryGuard.sln -c Release` succeeds with **0 warnings** (`TreatWarningsAsErrors=true`).
- Migration `0008` applies cleanly on a fresh DB and is journaled in `dbo.SchemaVersions`; re-running the runner is a no-op; `README.md` has the `0008` row.
- **RLS proof** (extend `tests/TelemetryGuard.Tests.Integration/Sql/RlsProofTests.cs`'s existing pattern): with `SESSION_CONTEXT('TenantId')` = tenant B, `SELECT * FROM dbo.PublisherDailySummaries` / `dbo.SiteDailySummaries` returns **zero** of tenant A's rows, and an `INSERT` carrying tenant A's `TenantId` fails on the BLOCK predicate.
- **Placement derivation**: given a session with a `tracker` row (`referrer = "https://www.Publisher-A.com/page?x=1"`) followed by a `verdict` row, `GetTopPlacementsDailyAsync` returns exactly one bucket with `Placement == "publisher-a.com"` — lowercased, `www.` stripped, path/query gone.
- **Missing ≠ zero**: a verdict whose session has **no** tracker/pixel row, or whose capture row has a null/empty referrer, produces **no** placement bucket and **no** `dbo.PublisherDailySummaries` row — not a `''` row, not an `Events = 0` row. A placement day with rows always has `Events >= 1`; `AvgScore` on an unscored scope is `NaN` from the query and `null` in the endpoint, never `0`.
- **Cross-day join**: a tracker at `23:59:55` on day *N* with its verdict at `00:00:05` on day *N+1* is attributed to the publisher and counted on day *N+1* (the verdict's day). This is what `SessionJoinLookbehind` exists for.
- **Per-day limit**: with `TopPlacementsLimit = 2` and 5 distinct placements on each of 2 days, the query returns exactly 4 rows — the top 2 *per day*, not the top 2 overall.
- **Tenant isolation at the analytics layer**: two tenants seeded with the **same** `session_id` and the **same** referrer must not join across tenants — tenant A's placement counts contain none of tenant B's verdicts (both sides of the join filter `tenant_id`).
- **Self-referral filter**: a tenant whose `dbo.Sites.Domain` is `www.shop.example` gets **no** `shop.example` placement row even when its own landing page is the referrer on every pixel session; a third-party publisher on the same days still lands.
- **Rollup**: one `RunOnceAsync` over seeded ClickHouse fixtures materializes exact per-day placement and site rows; a second back-to-back run leaves every value identical (absolute-value MERGE convergence); a tenant whose analytics throws advances **neither** watermark while healthy tenants advance both; the inactive tenant (`Status = 1`) is skipped entirely.
- **Watermark independence**: on a database where `verdict_daily` already sits at *now* but `publisher_daily` is absent, the first run backfills `LookbackDays` of publisher/site rows (proves the second watermark is real and not aliased to the first).
- **Contract suite**: `tests/TelemetryGuard.Tests.Contracts` passes with the new cases; `ClickHouseAnalyticsContractTests` needs no change beyond inheriting them (it is a one-line subclass).
- **Endpoints**: `GET /admin/reports/publishers?from=&to=&limit=` and `GET /admin/reports/sites?from=&to=` return 200 with camelCase bodies for an `X-Api-Key`-resolved tenant, 401 without a key, and `ValidationProblem` 400s for a missing/malformed date, `from > to`, and a range over 366 days. `limit` clamps to `[1, 200]`.
- **`flaggedRatio` / `avgScore` semantics**: a placement with `events = 0` cannot exist, but the mapper still returns `null` (not `0`) for both when it does; `lowVolume` is `true` exactly when `events < 30`.
- **D23 guard**: `grep -n "IAnalyticsQueries\|ClickHouse" TelemetryGuard.Api/Endpoints/AdminEndpoints.cs` matches only the doc comment — no admin handler touches the event store.
- **D3 guard**: `grep -rn "GetTopPlacementsDailyAsync\|GetSiteDailyCountsAsync\|IPublisherSummaryRepository" TelemetryGuard.RiskEngine TelemetryGuard.Api/Endpoints/DecisionEndpoints.cs TelemetryGuard.Api/Services` returns nothing — neither query nor table is reachable from the scoring path.

## Testing

**Unit — `tests/TelemetryGuard.Tests.Unit/Api/RollupServiceTests.cs`** (extend the existing pure-function class):
- `NormalizeHost`: `"https://WWW.Example.COM/path?q=1"` → `"example.com"`; `"example.com:8443"` → `"example.com"`; `"www.www.example.com"` → `"www.example.com"` (only ONE leading label is stripped — assert it, so the behavior is intentional); `null`/`""`/`"   "` → `""`.

**Unit — `tests/TelemetryGuard.Tests.Unit/Api/AdminEndpointTests.cs`** (same `WebApplicationFactory<Program>` + hand-rolled-fake pattern already used there). Add a `FakePublisherSummaryRepository` field to the existing `AdminApp` harness and register it the way every other fake in that file is registered — **`RemoveAll` then `AddSingleton`, not `TryAdd*`** (`TryAddScoped` is a no-op because `AddTelemetryGuardData()` already registered the real repository, and the test would silently exercise the real Dapper class against the unreachable `Server=localhost,1` connection string):

```csharp
                    services.RemoveAll<IPublisherSummaryRepository>();
                    services.AddSingleton<IPublisherSummaryRepository>(Publishers);
```
- `ToPublisherRow` math through the endpoint: `events=100, challenged=15, blocked=25, scoreSum=4200` → `flagged=40`, `flaggedRatio=0.4`, `avgScore=42.0`, `lowVolume=false`.
- `events=10` → `lowVolume=true`; ratio still returned (flagged, not hidden).
- Validation: missing `from`, malformed `to`, `from > to`, 400-day range → 400 `ValidationProblem` with the right field name; `limit=0` → clamped to 1; `limit=9999` → clamped to 200.
- `AdminScopeFilter` backstop: a site-key-resolved context gets 403 on both new routes.
- Site report: `events = 0` day → `avgScore` is `null`, and `totalEvents` still reflects the unscored traffic.

**Contract — `tests/TelemetryGuard.Tests.Contracts/AnalyticsContractTests.cs`** (the D7 guardrail; every provider must pass, so keep assertions engine-neutral and use `EventuallyAsync`, `Unique(...)`, `UniqueIpv4()`, and a disjoint anchor day per test):

First extend `tests/TelemetryGuard.Tests.Contracts/TestEvents.cs` — `Create` currently hardcodes `SiteKey = "site-1"` and never sets `Referrer`. Add `string siteKey = "site-1"` and `string? referrer = null` parameters (defaults keep every existing call site compiling) and forward them from `Verdict`.

New tests:
1. `TopPlacements_AttributesVerdictsToTheSessionsCaptureReferrer` — one session: tracker with `referrer` on `https://www.Pub-One.example/a`, then a verdict (score 90, block). Assert a single bucket, `Placement == "pub-one.example"`, `ScoredEvents == 1`, `Blocked == 1`, `ScoreSum == 90`, `AvgScore == 90`.
2. `TopPlacements_SessionWithoutReferrer_ProducesNoBucket` — a verdict whose session has a tracker row with `referrer: null`, plus a second session on a real publisher. Assert exactly one bucket and that it is the publisher's (§7 proof: no `""` bucket).
3. `TopPlacements_RanksAndLimitsPerDay` — 3 placements × 2 days with distinct volumes, `limitPerDay: 2` → 4 rows, top 2 per day, ordered `ScoredEvents` desc.
4. `TopPlacements_CaptureRowOnPreviousDay_StillAttributed` — the range starts at a UTC midnight `day`; seed the tracker at **`day.AddSeconds(-5)`** (5 s *before* the range start, i.e. on the previous UTC day) and its verdict at `day.AddSeconds(+5)`. Assert the verdict is attributed to the publisher and lands on day `day`. **The offset must stay inside `SessionJoinLookbehind` (1 hour)** — `day.AddDays(-1)…` would be a day and change before `captureFromTs = range.FromUtc - 1h`, so the capture row would fall outside the subquery's window and the test would (correctly) find no bucket. If a wider lookbehind is ever wanted, change `SessionJoinLookbehind` and this test together; never let the test assert an attribution the query cannot make.
5. `TopPlacements_TenantIsolation_SameSessionIdAndReferrer` — tenants A and B seeded with the **same** session id and referrer; A's bucket counts only A's verdicts, B's only B's.
6. `SiteDailyCounts_AggregatesPerSiteKeyAndDay` — two site keys, mixed kinds; assert `TotalEvents` includes trackers/pixels and `ScoredEvents` counts only verdicts, per day.
7. `SiteDailyCounts_NoScoredEvents_AvgScoreIsNaN` — a site with tracker/pixel rows only → `ScoredEvents == 0`, `ScoreSum == 0`, `double.IsNaN(AvgScore)`.

**Integration — ClickHouse (`ClickHouseAnalyticsQueriesTests.cs`)**: provider-specific edges that do not belong in the shared suite — a referrer that is not a URL (`"not a url"`) yields no bucket; a 300-character host is dropped by the `length(placement) <= 253` guard; two capture rows on one session (a pixel at T+5 s after a tracker at T) resolve to the **tracker's** host (`argMin` by timestamp). Extend that file's `TestEvents` helpers with the same `siteKey`/`referrer` parameters.

**Integration — SQL (`tests/TelemetryGuard.Tests.Integration/Sql/PublisherSummaryRepositoryTests.cs`)**, modeled on `VerdictSummaryRepositoryTests.cs` and using the DAT-08 `SqlServerFixture` (`[Collection("sqlserver")]`):
- Upsert → read round trip for both tables, including `DateOnly` fidelity.
- Absolute-value convergence: upsert the same key twice with different values → one row holding the **second** value (never a sum).
- Ambient-tenant guard: a row whose `TenantId` differs from the ambient tenant throws `InvalidOperationException` before any SQL runs.
- Cross-tenant read returns empty (the RLS proof this suite exists for).
- `GetTopPlacementsAsync` sums across days, orders by `Blocked` desc then flagged desc, respects `TOP (@Limit)`, and rejects `limit = 0` / `limit = 1001` with `ArgumentOutOfRangeException`.

**Integration — rollup (`RollupServiceTests.cs`)**: extend the existing fixture seed with (a) tracker+verdict pairs on two publishers for tenant A, (b) a self-referral session on tenant A's own `dbo.Sites.Domain` (seed a `dbo.Sites` row for it), and (c) tenant B sharing a publisher host. Assert exact `dbo.PublisherDailySummaries` / `dbo.SiteDailySummaries` rows per tenant, the absence of the self-referral row, idempotence across two runs, and the two-watermark behavior described in step 9.

**Integration — admin (`tests/TelemetryGuard.Tests.Integration/Api/AdminEndpointTests.cs`)**: seed both tables directly through the fixture's stamped connection, then read both reports over real HTTP with the fixture's `admin`-scope API key; assert the JSON body values and that a second tenant's key sees none of the first tenant's placements.

**No new test project, no CI change.** Run: `dotnet test tests/TelemetryGuard.Tests.Unit`, `dotnet test tests/TelemetryGuard.Tests.Integration`, `dotnet test tests/TelemetryGuard.Tests.Contracts`.

## Out of scope / guardrails

- **The T2 feature path belongs to a FUTURE task — not this one, and not P2-02.** Do **not** add `publisher_fraud_ratio` to `FraudFeatureVector`, do **not** bump `FraudFeatureVector.FeatureSetVersion` (currently `public const int FeatureSetVersion = 1`), do **not** touch `MlFeatureRow`/`MlFeatureMapper`, and do **not** add a score-time cache. Reason: a v2 feature without a retrained model is dead weight, and the bump forces a coordinated change across `MlFeatureRow`, the training pipeline, and every stamped verdict's lineage (D18). **Explicitly not P2-02**: that task's `depends_on` does not include P2-01, and its own guardrails list "publisher-aggregate features (P2-01)" as out of scope — it consumes the feature set as-is and hard-fails a retrain that straddles a `feature_set_version` boundary. **Handoff contract the future task must implement** (written here so nothing is lost; it depends on P2-01 *and* P2-02 both being complete, because bumping the feature set requires P2-02's retrain/promote loop to produce a v2 model):
  - Source: `dbo.PublisherDailySummaries` summed over a trailing window (7 d and 30 d proposed as two separate features), read from SQL/Redis/in-memory — **never** ClickHouse at score time (D3/D23).
  - Smoothing (m-estimate): `ratio = (Challenged + Blocked + m · priorRate) / (Events + m)` with configured `m` (proposed 50) and `priorRate` (proposed 0.10).
  - `NaN` — never `0` — when the placement is unknown or `Events < minEvents` (proposed 30). LightGBM branches on `NaN` natively.
  - Rules never consume it: publisher ratios are **T2 model features only**. Rules only ever raise a score and are reserved for T1.
- **No ClickHouse schema change and no ingest change.** Do not add a `placement` column to `tg_events`, a `pl=` query parameter to `/c`, or `Referrer` to the verdict `ClickEvent`. If a first-party placement id is ever needed, that is a separate task with its own ClickHouse migration and SDK/tracker changes.
- **No generic query layer (D7).** Two intent-named methods, native ClickHouse SQL, implemented per provider. P2-05's Kusto provider inherits the obligation to implement both and pass the contract suite — a provider missing them is incomplete. **It is not this task's job to write KQL, and this task creates/edits no file under `TelemetryGuard.Analytics.Kusto/`.** P2-05's task file carries a "P2-01 interlock" section that tells its implementer exactly what to add when this task has already landed; if P2-05 lands first, its implementer adds nothing and this task's implementer must **also** add the two KQL methods to `KustoAnalyticsQueries` (mechanical: the same conditional aggregates, `argmin`-per-session for the placement join). Whichever lands second closes the gap — the solution must never contain an `IAnalyticsQueries` implementation missing a method, because that is a compile error.
- **No per-campaign placement breakdown.** These tables are keyed `(TenantId, Date, Placement)` / `(TenantId, Date, SiteKey)`. A campaign dimension multiplies row count and is not needed for the report; add it later with its own migration if the portal (P2-03) asks.
- **No distinct-count columns.** Every stored number must be summable across rows; `uniqExact(session_id)` per day is not, and would silently lie on multi-day reads. Keeping the tables strictly mergeable is what makes the absolute-value MERGE + watermark-replay contract hold.
- **No automatic exclusions.** Nothing here writes `dbo.ExclusionQueue`, and nothing here pushes a placement to Google Ads (INT-03) or Meta (INT-04). Turning a bad publisher aggregate into an exclusion is a human/portal decision (D21 `EnforcementMode`), not a side effect of a rollup.
- **Not on the request path (D3).** `RollupService` is a background worker; the two `/admin` reads are SQL-only. No endpoint's latency changes, and the <50 ms scoring budget is untouched.
- **Tenancy (D11).** The SYSTEM sentinel is used only for the tenant enumeration that already exists in `RunOnceAsync`; every new statement runs on an `ITenantConnectionFactory` connection with an explicit `WHERE TenantId = @TenantId`. Remember `dbo.Sites` is **RLS-exempt** — its predicate is the only thing scoping the self-referral read. No raw `SqlConnection`, no EF Core (D9).
- **Backlog items stay in the backlog**: no IP-reputation store, no per-tenant Turnstile keys, no SSO/user accounts, no multi-instance rollup claiming/leader election (the single API host owns the timer; concurrent rollups are a later, measured decision), no Kafka/queue (D12), no elastic-pool tenant isolation, no Grafana dashboard changes (OPS-01 owns those; the new tables are readable by the existing SQL datasource without edits).
- **No server-side Python/Node (D1).** Never edit an applied migration (`0001`–`0007`), and never edit `0008` once merged. `0009` is reserved by P2-02 — fix a mistake in `0008` with `0010`.
