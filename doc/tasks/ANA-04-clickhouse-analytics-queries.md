---
id: ANA-04
title: ClickHouse analytics queries
phase: 1
workstream: analytics
depends_on: [ANA-01, ANA-02]
size: M
spec_refs: [D7, D11, "section 7 (null semantics)"]
detail_level: full
---

# ANA-04: ClickHouse analytics queries

## Objective

Implement `ClickHouseAnalyticsQueries : IAnalyticsQueries` in `TelemetryGuard.Analytics.ClickHouse` — the three intent-named read methods answered in native ClickHouse SQL against `tg_events`. The tenant is ALWAYS injected from the scoped `ITenantContext`; it is never a method parameter and never omitted from a WHERE clause.

## Spec context (self-contained)

- **D7 — abstract by intent:** the interface is fixed (three methods, defined in ANA-01); each engine answers in its own dialect. Write idiomatic ClickHouse SQL here — do not generalize, do not build SQL-string helpers intended for reuse by a future Kusto provider.
- **D11 — tenancy:** every query MUST filter `tenant_id = <current tenant>` taken from `ITenantContext` (FND-04). `tenant_id` is the first `ORDER BY` column of `tg_events`, so this predicate is also the primary pruning key. A missing tenant filter is a data breach, not a bug.
- **Injection guardrail:** all values (tenant, ip, campaign, dates, limit) go through `ClickHouse.Client` bound parameters (`{name:Type}` placeholders + `AddParameter`). String interpolation/concatenation of values into SQL is forbidden.
- **Null semantics (§7):** aggregate results honor *missing ≠ zero* — `AvgScore` is `double.NaN` when there are no scored rows in scope (ClickHouse `avgIf` over an empty set returns `nan`, which must be passed through, not replaced with 0).
- Verdict rows are `kind = 'verdict'`; bands are the strings `allow|challenge|block`; block band = score 71–100 (`FlaggedCount`/`FlaggedEvents` count these).
- `DateRange` is half-open UTC: `[FromUtc, ToUtc)`.

## Prerequisites

- ANA-01 (`doc/tasks/ANA-01-analytics-abstractions.md`): `IAnalyticsQueries`, `IpVelocityStats`, `CampaignFraudReport`, `CampaignDailyCounts` (incl. `ScoreSum`), `FlaggedSource` (incl. `BlockedEvents`/`ScoreSum`), `DateRange` — exact record shapes are defined there and consumed verbatim here.
- ANA-02: `tg_events` exists with the column names used below; `ClickHouseAnalyticsOptions` exists from ANA-03 (property `ConnectionString`). If implementing before ANA-03 merged, create `ClickHouseAnalyticsOptions` exactly as specified in `doc/tasks/ANA-03-clickhouse-event-sink.md` step 1.
- FND-04: `TelemetryGuard.Core.Tenancy.ITenantContext` (`TenantId TenantId` — a `readonly record struct` wrapping `Guid Value`) and `TelemetryGuard.Core.Time.IClock` (**`DateTimeOffset UtcNow`** — convert with `.UtcDateTime` where a `DateTime` is needed).

## Implementation steps

1. **Create `TelemetryGuard.Analytics.ClickHouse/ClickHouseAnalyticsQueries.cs`:**
   ```csharp
   using ClickHouse.Client.ADO;
   using Microsoft.Extensions.Options;
   using TelemetryGuard.Analytics.Abstractions;
   using TelemetryGuard.Core.Tenancy;
   using TelemetryGuard.Core.Time;

   namespace TelemetryGuard.Analytics.ClickHouse;

   /// <summary>
   /// Intent-named reads over tg_events in native ClickHouse SQL (D7).
   /// Tenant comes exclusively from ITenantContext (D11) — never a parameter.
   /// Registered SCOPED (ANA-05) because ITenantContext is scoped.
   /// </summary>
   public sealed class ClickHouseAnalyticsQueries(
       ITenantContext tenant,
       IOptions<ClickHouseAnalyticsOptions> options,
       IClock clock) : IAnalyticsQueries
   {
       private readonly string _cs = options.Value.ConnectionString;

       public async Task<IpVelocityStats> GetIpVelocityAsync(string ip, TimeSpan window, CancellationToken ct) { ... }
       public async Task<CampaignFraudReport> GetCampaignReportAsync(string campaignId, DateRange range, CancellationToken ct) { ... }
       public async Task<IReadOnlyList<FlaggedSource>> GetTopFlaggedSourcesAsync(DateRange range, int limit, CancellationToken ct) { ... }
   }
   ```

2. **`GetIpVelocityAsync`** — window end is `clock.UtcNow`:
   ```csharp
   public async Task<IpVelocityStats> GetIpVelocityAsync(string ip, TimeSpan window, CancellationToken ct)
   {
       var windowEnd = clock.UtcNow.UtcDateTime; // IClock.UtcNow is DateTimeOffset (FND-04)
       var windowStart = windowEnd - window;

       await using var conn = new ClickHouseConnection(_cs);
       await conn.OpenAsync(ct);
       await using var cmd = conn.CreateCommand();
       cmd.CommandText =
           """
           SELECT
               count()                          AS click_count,
               uniqExact(session_id)            AS distinct_sessions,
               uniqExact(user_agent)            AS distinct_user_agents,
               uniqExact(fingerprint_visitor_id) AS distinct_fingerprints,
               countIf(score >= 71)             AS flagged_count
           FROM tg_events
           WHERE tenant_id = {tenantId:UUID}
             AND ip = toIPv6({ip:String})
             AND timestamp >= {fromTs:DateTime64(3)}
             AND timestamp <  {toTs:DateTime64(3)}
           """;
       cmd.AddParameter("tenantId", tenant.TenantId.Value);
       cmd.AddParameter("ip", NormalizeIp(ip));
       cmd.AddParameter("fromTs", windowStart);
       cmd.AddParameter("toTs", windowEnd);

       await using var r = await cmd.ExecuteReaderAsync(ct);
       await r.ReadAsync(ct);
       return new IpVelocityStats(
           Ip: ip,
           Window: window,
           WindowEndUtc: windowEnd,
           ClickCount: Convert.ToInt64(r["click_count"]),
           DistinctSessions: Convert.ToInt64(r["distinct_sessions"]),
           DistinctUserAgents: Convert.ToInt64(r["distinct_user_agents"]),
           DistinctFingerprints: Convert.ToInt64(r["distinct_fingerprints"]),
           FlaggedCount: Convert.ToInt64(r["flagged_count"]));
   }
   ```
   `NormalizeIp`: reuse `ClickHouseEventSink.ToIpV6(ip).ToString()` (ANA-03) so IPv4 inputs compare equal to the stored IPv4-mapped IPv6 values; if ANA-03 is not merged yet, implement the same 4-line helper privately. ClickHouse aggregate functions skip NULLs, so `uniqExact(user_agent)` counts only non-null values — the desired semantics.

3. **`GetCampaignReportAsync`** — one grouped query; totals computed client-side from the daily rows (weighted by `scored_events` for the average):
   ```csharp
   cmd.CommandText =
       """
       SELECT
           toDate(timestamp)                                   AS day,
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
         AND campaign_id = {campaignId:String}
         AND timestamp >= {fromTs:DateTime64(3)}
         AND timestamp <  {toTs:DateTime64(3)}
       GROUP BY day
       ORDER BY day
       """;
   cmd.AddParameter("tenantId", tenant.TenantId.Value);
   cmd.AddParameter("campaignId", campaignId);
   cmd.AddParameter("fromTs", range.FromUtc);
   cmd.AddParameter("toTs", range.ToUtc);
   ```
   Read rows into `CampaignDailyCounts` (`day` → `DateOnly.FromDateTime`; `score_sum` → `ScoreSum` (Convert.ToInt64; 0 when no scored rows); `avg_score` may come back as `double.NaN` — pass through unchanged). Then:
   ```csharp
   var totalScored = days.Sum(d => d.ScoredEvents);
   var totalScoreSum = days.Sum(d => d.ScoreSum);
   var avg = totalScored == 0 ? double.NaN : (double)totalScoreSum / totalScored;
   return new CampaignFraudReport(campaignId, range,
       days.Sum(d => d.TotalEvents), totalScored,
       days.Sum(d => d.Allowed), days.Sum(d => d.Challenged), days.Sum(d => d.Blocked),
       avg, days.Sum(d => d.NoJsBeaconCount), days);
   ```
   An empty result set returns a report with all-zero counts, `AvgScore = double.NaN`, and an empty `Days` list — never null.

4. **`GetTopFlaggedSourcesAsync`** — IP-grouped for MVP (`SourceType = "ip"`):
   ```csharp
   cmd.CommandText =
       """
       SELECT
           IPv6NumToString(ip)                        AS source_value,
           countIf(band IN ('challenge', 'block'))    AS flagged_events,
           countIf(band = 'block')                    AS blocked_events,
           count()                                    AS total_events,
           sumIf(score, isNotNull(score))             AS score_sum,
           avgIf(score, isNotNull(score))             AS avg_score,
           min(timestamp)                             AS first_seen,
           max(timestamp)                             AS last_seen
       FROM tg_events
       WHERE tenant_id = {tenantId:UUID}
         AND kind = 'verdict'
         AND timestamp >= {fromTs:DateTime64(3)}
         AND timestamp <  {toTs:DateTime64(3)}
       GROUP BY ip
       HAVING flagged_events > 0
       ORDER BY blocked_events DESC, flagged_events DESC
       LIMIT {limit:Int32}
       """;
   cmd.AddParameter("tenantId", tenant.TenantId.Value);
   cmd.AddParameter("fromTs", range.FromUtc);
   cmd.AddParameter("toTs", range.ToUtc);
   cmd.AddParameter("limit", limit);
   ```
   Map to `FlaggedSource("ip", source_value, flagged_events, blocked_events, total_events, score_sum, avg_score, first_seen(UTC), last_seen(UTC))`. Flagged = challenge-or-block band; the `blocked_events DESC, flagged_events DESC` ordering mirrors the DAT-06 read side so the rollup (ANA-07) and portal reads rank identically. Guard `limit`: if `limit <= 0` throw `ArgumentOutOfRangeException` before querying. Ensure timestamps are returned as `DateTimeKind.Utc` (`DateTime.SpecifyKind` after reading).

5. **Validation:** each method first checks its inputs (`ip` non-empty, `campaignId` non-empty) and throws `ArgumentException` — never silently widens a query.

## Files to create or modify

- `TelemetryGuard.Analytics.ClickHouse/ClickHouseAnalyticsQueries.cs`
- (only if ANA-03 not yet merged) `TelemetryGuard.Analytics.ClickHouse/ClickHouseAnalyticsOptions.cs` per ANA-03 step 1

## Acceptance criteria

- `dotnet build` passes; the class implements `IAnalyticsQueries` exactly (no added public methods, no tenant parameters anywhere).
- `grep -n "tenant_id" TelemetryGuard.Analytics.ClickHouse/ClickHouseAnalyticsQueries.cs` shows the predicate present in all three SQL strings; `grep` finds no `$"` / string.Format / `+` concatenation building SQL with values.
- Against a seeded ClickHouse (see Testing): velocity counts, campaign aggregates, and flagged-source ranking return the exact expected numbers, and rows from a different tenant NEVER appear (query as tenant A with tenant B data present returns tenant-A-only numbers).
- `GetCampaignReportAsync` over a range with zero scored events returns `AvgScore` = `double.NaN` (assert `double.IsNaN`), not 0.
- `GetIpVelocityAsync("1.2.3.4", ...)` matches events stored from IPv4 source `1.2.3.4` (IPv4-mapped comparison works).
- `GetTopFlaggedSourcesAsync` returns at most `limit` rows, ordered by `BlockedEvents` desc then `FlaggedEvents` desc, and excludes IPs with no challenge/block-band verdicts.

## Testing

- Integration tests in `tests/TelemetryGuard.Tests.Integration` (Testcontainers.ClickHouse, schema via `SchemaMigrator`): seed `tg_events` directly with raw INSERTs (or via `ClickHouseEventSink` if merged) covering: two tenants; one IP with 5 events / 3 sessions / 2 UAs / 1 block verdict; one campaign spanning 3 days with known band counts; a no-verdict campaign for the NaN case. Build the class under test with a stub `ITenantContext` (fixed tenant) and stub `IClock` (fixed now) — both are trivial test doubles.
- The definitive cross-provider behavior suite is ANA-06 and will reuse these scenarios; keep the seed helpers `internal` and reusable (e.g., a `TestEvents` builder in the Integration test project) so ANA-06 can lift them.

## Out of scope / guardrails

- **Tenant is never optional (D11):** no method parameter, no config fallback, no "all tenants" mode. If `ITenantContext` has no tenant, let it throw — do not catch and query unscoped.
- **No generic query layer (D7):** no query builders, no shared SQL-generation utilities, no expression translation. Hand-written ClickHouse SQL per method is the design.
- **No string interpolation of values into SQL** — bound parameters only ({name:Type} + AddParameter).
- **Missing ≠ zero:** never convert NaN averages to 0; never invent default rows for empty ranges.
- Read-only: this class must contain no INSERT/ALTER/DDL. Writers live in ANA-03; schema in ANA-02.
- Do not add methods beyond the three interface members (rollup needs are met by these three — see ANA-07). Do not implement Kusto (P2-05).
- No EF Core, no Dapper here (Dapper is for SQL Server repositories in `TelemetryGuard.Data`); this project talks to ClickHouse via `ClickHouse.Client` only.
