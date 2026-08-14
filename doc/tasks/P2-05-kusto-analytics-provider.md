---
id: P2-05
title: Kusto analytics provider
phase: later
workstream: kusto
depends_on: [ANA-01, ANA-02, ANA-03, ANA-04, ANA-05, ANA-06, DAT-01, FND-03]
size: L
spec_refs: [D1, D6, D7, D11, D12, D20, "§7 (null semantics)", "§9", "§10 later"]
detail_level: full
---

# P2-05: Kusto analytics provider

## Objective

Implement the second — and final (D6: "exactly two provider implementations ever needed") — analytics provider: a new project `TelemetryGuard.Analytics.Kusto` containing `KustoEventSink` (`IEventSink`), `KustoLabelSink` (`ILabelSink`), `KustoAnalyticsQueries` (`IAnalyticsQueries`, hand-written KQL), a `KustoSchemaMigrator` applying embedded `schema/*.kql` scripts that mirror ANA-02's `tg_events` / `tg_labels` 1:1, and a `KustoRetentionSweeper` that implements D20 per-row retention the Kusto way (there is no per-row TTL). Move the ANA-05 provider switch to the composition root (`TelemetryGuard.Api`) exactly as ANA-05's own comment instructs, so neither provider project references the other, and register `"Kusto"` there. Add a Kusto runner for the **unmodified** ANA-06 contract suite backed by the Kusto emulator container (`mcr.microsoft.com/azuredataexplorer/kustainer-linux`) via Testcontainers, opt-in compiled so a machine without the emulator skips cleanly rather than failing, plus a CI job that runs it.

**The provider is not done until the unmodified ANA-06 contract suite passes against it.**

## Spec context (self-contained)

- **D6 — one provider, two targets.** Kusto covers both Azure Data Explorer and Microsoft Fabric Eventhouse: *same engine, same .NET SDK, different connection string*. Fabric is never a third provider. D6 fixes the provider count at two forever, so this task closes the provider surface.
- **D7 — abstract by intent, not by query.** Every `IAnalyticsQueries` method is re-answered in native KQL. **No shared SQL/KQL generation, no cross-engine query layer, no LINQ-over-both.** Each provider owns its own schema scripts (`schema/*.sql` for ClickHouse, `schema/*.kql` here). Engine-native features are deliberately reimplemented per provider; lowest-common-denominator pressure is accepted, not "solved".
- **D7 — ingestion semantics.** `IEventSink` promises only **eventual, batched** delivery *precisely so* Kusto's queued ingestion (visibility in seconds) can honor it. The XML doc on `IEventSink` says: "implementations must never block the caller on storage I/O; delivery is best-effort under backpressure." ANA-06 already polls with a 5 s timeout (`AnalyticsContractTests.EventuallyAsync`) and never asserts immediate visibility — **do not weaken it further, and do not add a sync-flush member to the abstraction to make Kusto look faster.**
- **D11 — tenancy.** `IAnalyticsQueries` has **no tenant parameter on any method by design**; the implementation takes the tenant from the scoped `ITenantContext` (FND-04) and injects it into every query. `tenant_id` is first-class in every table and every query, never optional. There is no SQL Server RLS at this layer — the analytics store's isolation *is* the mandatory `tenant_id ==` predicate, which contract test 6 (`TenantIsolation_TenantAQueries_NeverSeeTenantB`) proves.
- **D20 — per-tenant retention, 30–180 days, default 90.** `retention_days` is denormalized onto every event row at ingest. ClickHouse implements this with a per-row TTL expression (`TTL toDateTime(timestamp) + toIntervalDay(retention_days) DELETE`). **Kusto has no per-row TTL**; D20 says explicitly that "the future Kusto provider implements the same policy via its own retention mechanism — a provider-specific concern, per D7." Step 8 below is that mechanism.
- **§7 — missing ≠ zero.** Absent SDK numerics are `NaN`, absent flags are `null`, velocity counters are legitimately `0`. Contract test 4 (`CampaignReport_NoScoredEvents_AvgScoreIsNaN`) and test 7 (`SdkNumerics_AbsentValues_RoundTripAsNaN`) pin this; the Kusto type mapping must preserve it end to end.
- **§9 / ANA-06 — the contract suite is what makes the second provider safe.** `tests/TelemetryGuard.Tests.Contracts/AnalyticsContractTests.cs` carries a header comment that is normative here: *"Every analytics provider MUST have a runner class in this project inheriting `AnalyticsContractTests<TFixture>`, with a fixture backed by that provider's REAL engine running in a container… Adding a provider without its runner — or skipping/weakening a contract test to make a provider pass — violates spec D7."*
- **D1** — no server-side Python/Node. Everything here is C#; `.kql` scripts are data, not a scripting runtime. **D9/D10** — SQL Server access stays Dapper + DbUp; nothing in this task touches relational data. **D12** — no broker; the sink stays an in-process bounded channel.
- **`TreatWarningsAsErrors=true`** is set repo-wide in `Directory.Build.props`. The new project inherits it.

## Parallel-execution seams (read before you touch a shared file)

P2-01..P2-05 are written to be implemented **in parallel by separate agents**. These are the only files this task shares with a sibling:

| Shared file | Also touched by | Rule |
|---|---|---|
| `TelemetryGuard.Analytics.Abstractions/IAnalyticsQueries.cs` | **P2-01 (adds two methods)** | **This is the one seam that can break your build.** See *P2-01 interlock* immediately below — it is mandatory reading before you write `KustoAnalyticsQueries`. |
| `tests/TelemetryGuard.Tests.Contracts/AnalyticsContractTests.cs`, `TestEvents.cs` | P2-01 (extends both) | **This task modifies neither.** You inherit whatever `[Fact]`s exist when you land — which may be 8 or 15. |
| `TelemetryGuard.Api/Program.cs` | P2-02 | This task swaps the `using` on **line 19** and **deletes** the `.AddCheck<ClickHouseHealthCheck>` line (~line 235). P2-02 **inserts a block** between `AddTelemetryGuardData()` (~line 70) and `AddScoringPipeline(...)` (~line 105) plus a log flush after `builder.Build()`. Different regions — but locate them by **content, never by line number**, since a sibling's edit shifts them. |
| `TelemetryGuard.Api/appsettings.json` | P2-01, P2-02 | This task edits only `"Analytics"` (and the two comment blocks above it). P2-01 edits only `"Rollup"`, P2-02 only `"Scoring"`. Stay inside your own section. |
| `TelemetryGuard.sln` | P2-03 | Both add one top-level project with `dotnet sln add`. Never hand-edit GUIDs. |
| `tests/TelemetryGuard.Tests.Unit/…csproj` | P2-03 | Both add **one** `<ProjectReference>`. Additive. |
| `.github/workflows/ci.yml` | P2-04 | Both **append** a job and leave `build-test`/`integration`/`sdk` byte-for-byte unchanged. This task appends `kusto-contracts`; P2-04 appends `image` and `iac`. Append at the end. |

**No migration, no spec amendment.** This task adds nothing under `TelemetryGuard.Data/migrations/` (`0008` is P2-01's, `0009` is P2-02's) and does not touch `doc/spec.md` (P2-03 appends `D24` there).

### P2-01 interlock — MANDATORY, read before writing `KustoAnalyticsQueries`

P2-01 (*Publisher aggregates*) **adds two intent-named methods to `IAnalyticsQueries`** and adds new `[Fact]`s to the shared contract suite. Because `IAnalyticsQueries` is an interface, a `KustoAnalyticsQueries` that implements only the original three **will not compile** once P2-01 has merged. Handle it explicitly rather than discovering it:

**Step 0.5 (do this before step 1): `cat TelemetryGuard.Analytics.Abstractions/IAnalyticsQueries.cs` and count the methods.**

- **Three methods** → P2-01 has not landed. Implement the three below and nothing else. If P2-01 merges later, *its* implementer adds the two KQL methods (its task file says so explicitly).
- **Five methods** (`GetTopPlacementsDailyAsync`, `GetSiteDailyCountsAsync` present) → P2-01 has landed. You **must** implement both in KQL, and the inherited contract suite will exercise them against the emulator. Do not stub them, do not `throw new NotImplementedException()`, and above all **do not weaken, skip or filter out the new contract tests** — that is the D7 violation the suite exists to prevent.

The KQL, so it is not left to guess. Both follow the step-6 conventions exactly (`declare query_parameters` prologue, `tenant_id == tenantId` on every branch, `ClientRequestProperties.SetParameter`, results mapped through `KustoValueMapping`):

```kusto
// GetSiteDailyCountsAsync — the ClickHouse twin is one conditional-aggregate scan
// grouped on site_key. Rows with an empty site_key are omitted.
declare query_parameters(tenantId: guid, fromTs: datetime, toTs: datetime);
tg_events
| where tenant_id == tenantId
    and isnotempty(site_key)
    and timestamp >= fromTs
    and timestamp <  toTs
| summarize
    total_events      = count(),
    scored_events     = countif(kind == 'verdict'),
    allowed           = countif(kind == 'verdict' and band == 'allow'),
    challenged        = countif(kind == 'verdict' and band == 'challenge'),
    blocked           = countif(kind == 'verdict' and band == 'block'),
    score_sum         = sumif(tolong(score), kind == 'verdict' and isnotnull(score)),
    scored_with_score = countif(kind == 'verdict' and isnotnull(score)),
    no_js_beacon      = countif(kind == 'verdict' and has_js_beacon == false)
    by day = startofday(timestamp), site_key
| order by day asc, site_key asc
```

```kusto
// GetTopPlacementsDailyAsync — the session join. ClickHouse resolves one placement
// per session with argMin(referrer, timestamp) then joins verdicts to it; KQL does
// the same with arg_min() in a let-bound subquery and an inner join on session_id.
// BOTH sides filter tenant_id, so the join can never cross tenants (D11).
// The capture side looks back SessionJoinLookbehind (1 h) before fromTs, because a
// tracker hit precedes its verdict by the API-02 grace period and may fall on the
// previous UTC day — P2-01's contract point 3.
declare query_parameters(tenantId: guid, captureFromTs: datetime, fromTs: datetime, toTs: datetime, lim: long);
let placements =
    tg_events
    | where tenant_id == tenantId
        and kind in ('tracker', 'pixel')
        and isnotempty(referrer)
        and timestamp >= captureFromTs
        and timestamp <  toTs
    | summarize arg_min(timestamp, referrer) by session_id
    | extend placement = tolower(tostring(parse_url(referrer).Host))
    | extend placement = iff(placement startswith 'www.', substring(placement, 4), placement)
    | where isnotempty(placement) and strlen(placement) <= 253
    | project session_id, placement;
tg_events
| where tenant_id == tenantId
    and kind == 'verdict'
    and timestamp >= fromTs
    and timestamp <  toTs
| join kind=inner placements on session_id
| summarize
    scored_events     = count(),
    allowed           = countif(band == 'allow'),
    challenged        = countif(band == 'challenge'),
    blocked           = countif(band == 'block'),
    score_sum         = sumif(tolong(score), isnotnull(score)),
    scored_with_score = countif(isnotnull(score)),
    no_js_beacon      = countif(has_js_beacon == false)
    by day = startofday(timestamp), placement
| order by day asc, scored_events desc, placement asc
| partition by day (top lim by scored_events desc)   // ClickHouse's `LIMIT n BY day`
```

Notes that carry the §7 contract through:

- `AvgScore = scoredWithScore == 0 ? double.NaN : (double)scoreSum / scoredWithScore` in C#, exactly as step 6 does for `GetCampaignReportAsync` — never `0`, and never Kusto's empty-scope `avg`.
- `ScoreSum` maps through `KustoValueMapping.ToInt64OrZero` (a sum's mergeable empty value **is** 0 — this is the one place 0 is correct).
- **Missing ≠ zero, structurally**: a verdict whose session has no capture row, or whose referrer is empty/unparseable, is dropped by the inner join and the `isnotempty(placement)` filter — it must never produce a `''` bucket or an `Events = 0` row.
- `ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limitPerDay)` before the query, matching P2-01's declared contract.
- If step-0 answer **Q4** came back "no", `lim` is validated in C# and interpolated as an integer — the same single allowed exception as `GetTopFlaggedSourcesAsync`.
- `parse_url(...).Host`, `arg_min()`, `strlen()` and `partition by … (top …)` are stock KQL; verify each against the emulator in step 0 and record any substitution in `TelemetryGuard.Analytics.Kusto/README.md` alongside the Q1–Q7 answers.

## Prerequisites

Read these task files and these exact source files before writing code. Every name below is copied from the current tree.

### ANA-01 — `TelemetryGuard.Analytics.Abstractions` (contract-bearing, normative for type names)

- `IEventSink.WriteBatchAsync(ReadOnlyMemory<ClickEvent> events, CancellationToken ct)` → `ValueTask`.
- `ILabelSink.WriteAsync(LabelEvent label, CancellationToken ct)` → `ValueTask`.
- `IAnalyticsQueries` — three methods **in the tree today**, no tenant parameter on any of them:
  ```csharp
  Task<IpVelocityStats> GetIpVelocityAsync(string ip, TimeSpan window, CancellationToken ct);
  Task<CampaignFraudReport> GetCampaignReportAsync(string campaignId, DateRange range, CancellationToken ct);
  Task<IReadOnlyList<FlaggedSource>> GetTopFlaggedSourcesAsync(DateRange range, int limit, CancellationToken ct);
  ```
  **Re-read the file before you implement it** — P2-01 appends two more (`GetTopPlacementsDailyAsync`, `GetSiteDailyCountsAsync`) and may land in parallel. See *P2-01 interlock* above, which carries the KQL for both.
- `ClickEvent` — the 71-field raw record (`ClickEvent.cs`). Note the exact CLR types you must map: `TenantId TenantId` (a `readonly record struct` wrapping `Guid Value`), `IReadOnlyList<string> HeaderNames` / `RuleHits`, `uint? CfAsn` / `uint? Asn`, `bool?` for every optional flag, `float` (NaN-defaulted) for every SDK numeric, `int` for the four velocity counters, `int? Score` / `int? FeatureSetVersion` / `int? ShadowScore`, `ushort RetentionDays`, `DateTime TimestampUtc`, `string Features` (defaults `""`).
- `EventKindWire.ToWire(this EventKind)` → `"tracker"|"pixel"|"beacon"|"verdict"` — the storage contract every provider must use.
- `VerdictBands.Allow/Challenge/Block` = `"allow"|"challenge"|"block"`; `LabelValues`, `LabelSources`.
- Result DTOs: `IpVelocityStats(Ip, Window, WindowEndUtc, ClickCount, DistinctSessions, DistinctUserAgents, DistinctFingerprints, FlaggedCount)`; `CampaignFraudReport(CampaignId, Range, TotalEvents, ScoredEvents, Allowed, Challenged, Blocked, AvgScore, NoJsBeaconCount, IReadOnlyList<CampaignDailyCounts> Days)`; `CampaignDailyCounts(Day, TotalEvents, ScoredEvents, Allowed, Challenged, Blocked, ScoreSum, AvgScore, NoJsBeaconCount)`; `FlaggedSource(SourceType, SourceValue, FlaggedEvents, BlockedEvents, TotalEvents, ScoreSum, AvgScore, FirstSeenUtc, LastSeenUtc)`; `DateRange(FromUtc, ToUtc)` — half-open `[From, To)`, both must be `DateTimeKind.Utc`.
- `LabelEvent(TenantId TenantId, string SessionId, string Label, string LabelSource, DateTime CreatedAtUtc)`.

### FND-04 — `TelemetryGuard.Core`

- `TelemetryGuard.Core.Tenancy.TenantId` (`readonly record struct TenantId(Guid Value)`), `ITenantContext { TenantId TenantId; string? SiteKey; bool IsResolved; }`.
- `TelemetryGuard.Core.Time.IClock { DateTimeOffset UtcNow; }` — `ClickHouseAnalyticsQueries` uses `clock.UtcNow.UtcDateTime` for the trailing-window end; mirror that exactly.

### ANA-02 — `TelemetryGuard.Analytics.ClickHouse/schema/*.sql` (the schema you are mirroring)

- `0001_events.sql` creates `tg_events` (69 columns) and `tg_labels` (5 columns, fixed 400-day TTL).
- `0002_training_and_shadow.sql` ALTERs `tg_labels ADD weight Float32 DEFAULT 1` and `tg_events ADD features String DEFAULT ''`, `shadow_score Nullable(Int16)`, `shadow_scorer_version LowCardinality(String) DEFAULT ''`.
- Net effect: `tg_events` has 71 columns, `tg_labels` has 6. **Kusto has no column defaults** — the Kusto label sink must therefore write `weight = 1.0` explicitly (ClickHouse gets it from the column DEFAULT).

### ANA-03 — `ClickHouseEventSink.cs` / `ClickHouseLabelSink.cs` (the sink shape you are copying)

- `internal static readonly string[] ColumnNames` — the **order-bound 71-name array** ("MUST match schema/0001_events.sql column order (ANA-02) exactly"), with the three RSK-08 columns appended at the end. Copy this array verbatim into `KustoEventSink`; it is the single source of truth for the Kusto JSON ingestion mapping too (step 4).
- `Channel.CreateBounded<ClickEvent>(new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false }, static _ => DroppedFull.Add(1))`; `WriteBatchAsync` only `TryWrite`s and returns `ValueTask.CompletedTask`; `StartAsync` kicks `Task.Run(RunAsync)`; `StopAsync` calls `_channel.Writer.TryComplete()` then `_runTask.WaitAsync(TimeSpan.FromSeconds(ShutdownDrainTimeoutSeconds))`; `RunAsync` batches on `EventMaxBatchSize` or `EventMaxBatchAgeSeconds` (whichever first, via a `CancellationTokenSource(maxAge)`), then drains on completion; `FlushBatchAsync` retries `FlushMaxRetries` times with `FlushRetryBaseDelayMs * 2^(attempt-1)` backoff and then **drops the batch with an error log** ("D7: eventual delivery only") — never throws to the caller.
- Meter/counter names to mirror: `tg.events.enqueued`, `tg.events.dropped_queue_full`, `tg.events.written`, `tg.events.batches_flushed`, `tg.events.batch_retries`, `tg.events.rows_dropped_flush_failed`, and the `tg.labels.*` set. Keep the counter names identical so dashboards work under either provider; only the `Meter` name changes (`TelemetryGuard.Analytics.Kusto`).
- `ClickHouseEventSink.ToIpV6(string)` — IPv4 inputs are stored mapped-to-IPv6 so reads and writes agree. You need the same normalization, in your own helper (no cross-provider reference).

### ANA-04 — `ClickHouseAnalyticsQueries.cs` (the query semantics you must reproduce)

Read it line by line. The KQL in step 6 reproduces its results exactly, including:
- Tenant from `ITenantContext`, never a parameter; every value bound as a query parameter, never string-interpolated.
- `NormalizeIp` before comparing, matching the writer.
- `ToInt64OrZero` (`DBNull` → `0` for sums, "the correct mergeable value") and `ToDoubleOrNaN` (`DBNull` → `NaN` for averages, "missing != zero").
- Report-level `AvgScore = totalScored == 0 ? NaN : (double)totalScoreSum / totalScored` where `totalScored = days.Sum(d => d.ScoredEvents)` — **note this divides by the count of `kind='verdict'` rows, while the per-day `AvgScore` divides by the count of verdict rows with a non-null score.** Reproduce this arithmetic exactly; do not "fix" it here.
- `GetTopFlaggedSourcesAsync` filters `kind = 'verdict'`, groups by ip, `HAVING flagged_events > 0`, orders `blocked_events DESC, flagged_events DESC`, and returns `SourceType: "ip"` as a constant.

### ANA-05 — `TelemetryGuard.Analytics.ClickHouse/AnalyticsServiceCollectionExtensions.cs`

The current switch, whose own XML doc is the instruction for this task:

> "D7 provider switch. Lives in the ClickHouse project while it is the only implemented provider; **when Kusto (P2-05) lands, move this switch to the composition root so neither provider references the other.**"

It currently: binds `Analytics:ClickHouse` to `ClickHouseAnalyticsOptions` with `.ValidateOnStart()`, throws `NotSupportedException` for `"Kusto"`, registers the concrete sinks **once** and forwards `IEventSink`/`ILabelSink`/`IHostedService` to the **same instances** ("two instances would split the queue from the flusher"), registers `IAnalyticsQueries` **scoped** (not singleton — it consumes the scoped `ITenantContext`), and throws `InvalidOperationException($"Unknown analytics provider '{p}'")` otherwise. Step 9 preserves every one of those properties.

### ANA-06 — `tests/TelemetryGuard.Tests.Contracts`

- `IAnalyticsProviderFixture : IAsyncLifetime` — the exact seam you implement:
  ```csharp
  IEventSink Sink { get; }        // started and flushing
  ILabelSink LabelSink { get; }   // started and flushing
  IAnalyticsQueries CreateQueries(TenantId tenantId, DateTime? utcNow = null);
  Task<float> ReadStorageAgeSecAsync(TenantId tenantId, string sessionId);
  Task<long> CountLabelsAsync(TenantId tenantId);
  ```
  Its XML doc already names you: *"The Kusto provider (P2-05) MUST supply an implementation backed by the Kusto emulator container and run the same AnalyticsContractTests subclass — that is the D7 guardrail."*
- `AnalyticsContractTests<TFixture>` — 8 `[Fact]`s **today**, `IClassFixture<TFixture>`, 5 s `EventuallyAsync` poll. **This file is not modified by this task** — but P2-01 adds 7 more `[Fact]`s to it, so count what is actually there and expect to pass all of them (see *P2-01 interlock*). Its `Unique(...)` / `UniqueIpv4()` helpers are `private static` on the base class; that is fine, because you add no test to this file.
- `ClickHouseProviderFixture` — the reference implementation of the seam (container → `SchemaMigrator.ApplyAsync()` → real sinks with `EventMaxBatchAgeSeconds = 0.3`). Your fixture has the same shape.
- `TestEvents.Create/Verdict` — seeds leave every SDK float at `NaN`, every flag `null`, `Score` `null` on non-verdict rows, `RetentionDays = 90`, `SiteKey = "site-1"`.
- `Fakes.cs` — `FixedTenantContext`, `FixedClock` (reuse; do not duplicate).
- `README.md` — "Adding a provider (e.g. Kusto, task P2-05)" section, which you update in step 11.
- Runner classes carry `[Trait("requires", "docker")]`.

### DAT-01 / MigrationRunner

`TelemetryGuard.MigrationRunner/Program.cs` dispatches `provision` → `ProvisionCommand`, `--clickhouse` → `Migrations.RunClickHouseAsync(Environment.GetEnvironmentVariable("CLICKHOUSE_CONNECTIONSTRING"))`, else DbUp SQL Server. `Migrations.RunClickHouseAsync` delegates to `new SchemaMigrator(cs).ApplyAsync()`. Step 10 adds the `--kusto` twin.

### API-01 / Api host

- `TelemetryGuard.Api/Program.cs`: `using TelemetryGuard.Analytics.ClickHouse;` (line 19) and `builder.Services.AddTelemetryGuardAnalytics(builder.Configuration);` (~line 75); `builder.Services.AddHttpClient();` (line 49); health checks registered as `AddCheck<SqlHealthCheck>("sql", tags: ["ready"]).AddCheck<RedisHealthCheck>("redis", tags: ["ready"]).AddCheck<ClickHouseHealthCheck>("clickhouse", tags: ["ready"])` (~line 232); `/healthz` = liveness (`Predicate = _ => false`), `/ready` = `Tags.Contains("ready")`.
- `TelemetryGuard.Api/Health/ClickHouseHealthCheck.cs` — *"Deliberately ClickHouse-specific (spec D7): no generic analytics-engine health abstraction."* Your `KustoHealthCheck` is deliberately Kusto-specific for the same reason.
- `TelemetryGuard.Api/Workers/` is the house location for `BackgroundService` workers (`RollupService`, `GoogleAdsExclusionSyncService`, `MetaExclusionSyncService`). The retention worker goes there.
- `TelemetryGuard.Api/Workers/RollupService.cs` consumes `IAnalyticsQueries` from a per-tenant DI scope (`scopeFactory.CreateScope()` → `TenantContext.Resolve(new TenantId(tid))` → `GetRequiredService<IAnalyticsQueries>()`). **It needs no change** — that is the whole point of D7 — but its Kusto behavior is an acceptance criterion.

### FND-03 / CI

`.github/workflows/ci.yml` has jobs `build-test` (sln build + unit tests), `integration` (Testcontainers integration + the contract suite), `sdk`. Step 12 adds a fourth job.

## Implementation steps

### Step 0 — Emulator + SDK reconnaissance (do this first, write the answers down)

Several details below have a documented primary choice and a documented fallback because the emulator is not the cloud service. **Spend the first hour verifying them against a running emulator and record the outcome in `TelemetryGuard.Analytics.Kusto/README.md`.** Do not skip this: every later step names which answer it depends on.

Start the emulator by hand (podman or docker; `~/.tg-env` already exports `DOCKER_HOST` and `TESTCONTAINERS_RYUK_DISABLED=true`):

```bash
podman run -d --name kustainer -e ACCEPT_EULA=Y -m 4g -p 8080:8080 \
  mcr.microsoft.com/azuredataexplorer/kustainer-linux:latest
# engine endpoint: http://localhost:8080 ; anonymous auth ; default database: NetDefaultDB
curl -s -X POST http://localhost:8080/v1/rest/mgmt \
  -H 'Content-Type: application/json' -d '{"csl":".show version"}'
```

Answer and record:

| # | Question | Primary | Fallback if the primary fails |
| --- | --- | --- | --- |
| Q1 | Does the emulator accept **streaming ingestion** (`KustoIngestFactory.CreateStreamingIngestClient` against the engine endpoint)? | Yes → fixture uses `IngestMode=Streaming` | Use `.ingest inline into table tg_events <\| …` behind the same `IKustoIngestTransport` seam (step 3), marked emulator-only in code |
| Q2 | Does a JSON `"NaN"` string ingest into a `real` column as `double.NaN`? (`tg_events \| where isnan(storage_age_sec) \| count`) | Yes → write `"NaN"` for non-finite floats | **Null-encoding**: write JSON `null` for non-finite floats and treat a null `real` as absent everywhere; `KustoValueMapping.RealToFloat` (step 3) maps `null → float.NaN`, so §7 still holds |
| Q3 | Is `count_distinctif()` available? | Yes → use it | `dcountif(col, predicate, 4)` (accuracy level 4) |
| Q4 | Does `\| take lim` accept a declared query parameter? | Yes → bind `lim` | Validate `limit > 0` in C# (already done via `ArgumentOutOfRangeException.ThrowIfNegativeOrZero`) and interpolate the **integer** into the KQL — the only interpolation allowed anywhere in this task |
| Q5 | Does `.delete table tg_events records <\| …` (soft delete) run on the emulator? | Yes → the retention integration test asserts rows disappear | Test asserts only the backstop policy via `.show table tg_events policy retention`; the sweep itself gets a documented manual-verification note |
| Q6 | Does `.alter-merge table … policy retention softdelete = …` run on the emulator? | Yes | Move the two policy commands into their own script and tolerate failure in the emulator path only (never in the MigrationRunner path) |
| Q7 | Does `.create database <name> …` work, or must you use the built-in `NetDefaultDB`? | Use `NetDefaultDB` in the fixture; make the database name an option | — |

**No real ADX endpoint is ever contacted — not in tests, not in CI, not in dev.** The only Kusto endpoint this repo may reach is a locally started emulator container.

### Step 1 — Project scaffold

```bash
dotnet new classlib -o TelemetryGuard.Analytics.Kusto -f net8.0
dotnet sln TelemetryGuard.sln add TelemetryGuard.Analytics.Kusto/TelemetryGuard.Analytics.Kusto.csproj
dotnet add TelemetryGuard.Analytics.Kusto reference TelemetryGuard.Core TelemetryGuard.Analytics.Abstractions
dotnet add TelemetryGuard.Analytics.Kusto package Microsoft.Azure.Kusto.Data
dotnet add TelemetryGuard.Analytics.Kusto package Microsoft.Azure.Kusto.Ingest
```

Use `dotnet sln add` rather than hand-editing `TelemetryGuard.sln` — it generates the project GUID and both `Debug|Any CPU` / `Release|Any CPU` rows in `GlobalSection(ProjectConfigurationPlatforms)` correctly. The new project is a top-level project (not under the `tests` solution folder).

`TelemetryGuard.Analytics.Kusto/TelemetryGuard.Analytics.Kusto.csproj` — mirror the ClickHouse csproj exactly, swapping the engine packages and the embedded-resource glob. **Pin whatever versions `dotnet add package` resolves** (the ClickHouse project pins `ClickHouse.Client` 7.2.2 and the `Microsoft.Extensions.*` 8.0.x set; match that style):

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <ProjectReference Include="..\TelemetryGuard.Core\TelemetryGuard.Core.csproj" />
    <ProjectReference Include="..\TelemetryGuard.Analytics.Abstractions\TelemetryGuard.Analytics.Abstractions.csproj" />
  </ItemGroup>

  <ItemGroup>
    <!-- Azure Data Explorer / Fabric Eventhouse SDK (D6: one provider, two targets). -->
    <PackageReference Include="Microsoft.Azure.Kusto.Data" Version="<resolved>" />
    <PackageReference Include="Microsoft.Azure.Kusto.Ingest" Version="<resolved>" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="8.0.2" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="8.0.1" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="8.0.2" />
    <PackageReference Include="Microsoft.Extensions.Options" Version="8.0.2" />
    <PackageReference Include="Microsoft.Extensions.Options.ConfigurationExtensions" Version="8.0.0" />
  </ItemGroup>

  <ItemGroup>
    <EmbeddedResource Include="schema/**/*.kql" />
  </ItemGroup>

  <ItemGroup>
    <!-- Unit tests exercise KustoSchemaMigrator.SplitCommands, KustoEventSink.MapRow/WriteRow
         and the mapping-vs-ColumnNames invariant. -->
    <InternalsVisibleTo Include="TelemetryGuard.Tests.Unit" />
  </ItemGroup>

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
```

If the Kusto SDK produces build warnings, `TreatWarningsAsErrors=true` will fail the build. Fix them properly (e.g. explicit `ConfigureAwait(false)`, non-nullable annotations); **do not add `<NoWarn>` or `<TreatWarningsAsErrors>false</TreatWarningsAsErrors>` to this project.**

### Step 2 — Options

`TelemetryGuard.Analytics.Kusto/KustoAnalyticsOptions.cs` — deliberately mirrors `ClickHouseAnalyticsOptions` name-for-name where the meaning is the same, so an operator flipping providers recognizes every knob:

```csharp
namespace TelemetryGuard.Analytics.Kusto;

/// <summary>
/// Options for the Kusto analytics provider (Azure Data Explorer AND Fabric
/// Eventhouse — D6: one provider, two targets, differing only in the connection
/// string). Bound to config section <c>Analytics:Kusto</c> by the composition-root
/// switch (P2-05 step 9). Sink tuning names mirror
/// <c>ClickHouseAnalyticsOptions</c> on purpose.
/// </summary>
public sealed class KustoAnalyticsOptions
{
    /// <summary>Engine endpoint KCSB, e.g. "Data Source=https://&lt;cluster&gt;.&lt;region&gt;.kusto.windows.net;Fed=true"
    /// or, for the local emulator, "Data Source=http://localhost:8080;Federated Security=False".
    /// NEVER logged — may carry an app key.</summary>
    public string ConnectionString { get; init; } = "";

    /// <summary>Data-management (ingest-) endpoint KCSB. Required only when
    /// <see cref="IngestMode"/> is "Queued". Deliberately explicit: no silent
    /// "ingest-" prefixing of the engine URI.</summary>
    public string IngestConnectionString { get; init; } = "";

    public string Database { get; init; } = "telemetry_guard";

    /// <summary>"Queued" (production default — D7's weak eventual guarantee) or
    /// "Streaming" (emulator/dev; the Kusto emulator has no DM service).</summary>
    public string IngestMode { get; init; } = "Queued";

    // event sink
    public int EventQueueCapacity { get; init; } = 100_000;
    public int EventMaxBatchSize { get; init; } = 5_000;
    public double EventMaxBatchAgeSeconds { get; init; } = 2.0;
    // label sink
    public int LabelQueueCapacity { get; init; } = 10_000;
    public int LabelMaxBatchSize { get; init; } = 500;
    public double LabelMaxBatchAgeSeconds { get; init; } = 5.0;
    // flush retry (both sinks)
    public int FlushMaxRetries { get; init; } = 3;
    public double FlushRetryBaseDelayMs { get; init; } = 200;
    public double ShutdownDrainTimeoutSeconds { get; init; } = 10;
    // queries
    public double QueryTimeoutSeconds { get; init; } = 30;
    // retention (D20 — see step 8)
    public bool RetentionSweepEnabled { get; init; } = true;
    public double RetentionSweepIntervalHours { get; init; } = 24;

    public const string QueuedMode = "Queued";
    public const string StreamingMode = "Streaming";
}
```

### Step 3 — Connection seams (query executor + ingest transport)

`TelemetryGuard.Analytics.Kusto/IKustoQueryExecutor.cs` + `KustoQueryExecutor.cs`. One singleton owns the expensive `ICslQueryProvider` / `ICslAdminProvider` (they pool connections); `KustoAnalyticsQueries` stays scoped.

```csharp
using System.Data;
using Kusto.Data.Common;

namespace TelemetryGuard.Analytics.Kusto;

/// <summary>
/// The ONLY type in the solution that talks to a Kusto engine endpoint. KQL text
/// is always supplied by callers inside this assembly (D7: no cross-engine query
/// layer, no query strings crossing an assembly boundary).
/// </summary>
public interface IKustoQueryExecutor
{
    Task<IDataReader> ExecuteQueryAsync(string kql, ClientRequestProperties properties, CancellationToken ct);
    Task ExecuteControlCommandAsync(string command, CancellationToken ct);
    /// <summary>`print 1` round-trip for the API-01 readiness probe.</summary>
    Task<bool> PingAsync(CancellationToken ct);
}
```

`KustoQueryExecutor(IOptions<KustoAnalyticsOptions> opts, ILogger<KustoQueryExecutor> log) : IKustoQueryExecutor, IDisposable`:

- Build `new KustoConnectionStringBuilder(opts.Value.ConnectionString)` once; create the providers **lazily** (`Lazy<ICslQueryProvider>` / `Lazy<ICslAdminProvider>` via `KustoClientFactory.CreateCslQueryProvider(kcsb)` / `CreateCslAdminProvider(kcsb)`) so DI resolution never performs I/O — the `Provider=Kusto` boot smoke test (step 11) must pass with no cluster reachable.
- `ClientRequestProperties` per call, with `props.SetOption(ClientRequestProperties.OptionServerTimeout, TimeSpan.FromSeconds(opts.QueryTimeoutSeconds))` and a `ClientRequestId` of `$"tg;{Guid.NewGuid()}"`.
- Honor `ct`: use the `ExecuteQueryAsync(database, query, properties, CancellationToken)` overload if the installed package version exposes one; otherwise `ct.ThrowIfCancellationRequested()` before the call and `.WaitAsync(ct)` the returned task. **Every public method takes and flows a `CancellationToken` — the house rule.**
- Never log `ConnectionString` or `IngestConnectionString`.

`TelemetryGuard.Analytics.Kusto/IKustoIngestTransport.cs` — the seam that makes queued-vs-streaming a config choice (and makes the emulator usable):

```csharp
public interface IKustoIngestTransport
{
    /// <summary>Ingests one already-serialized multijson payload into <paramref name="table"/>
    /// using the named ingestion mapping. Eventual visibility only (D7).</summary>
    Task IngestAsync(Stream multiJson, string table, string mappingName, CancellationToken ct);
}
```

Two implementations, both in this project, selected by `IngestMode` in the registration extension (step 9):

- `QueuedKustoIngestTransport` — `KustoIngestFactory.CreateQueuedIngestClient(new KustoConnectionStringBuilder(opts.IngestConnectionString))`, held in a `Lazy<IKustoIngestClient>`; per call:
  ```csharp
  var props = new KustoQueuedIngestionProperties(_opts.Database, table)
  {
      Format = DataSourceFormat.multijson,
      IngestionMapping = new IngestionMapping
      {
          IngestionMappingReference = mappingName,
          IngestionMappingKind = IngestionMappingKind.Json
      },
      FlushImmediately = true,                       // seconds, not minutes — still "eventual" (D7)
      ReportLevel = IngestionReportLevel.FailuresOnly,
      ReportMethod = IngestionReportMethod.Queue
  };
  await _client.Value.IngestFromStreamAsync(multiJson, props,
      new StreamSourceOptions { LeaveOpen = true }).ConfigureAwait(false);
  ```
- `StreamingKustoIngestTransport` — identical body but `KustoIngestFactory.CreateStreamingIngestClient(new KustoConnectionStringBuilder(opts.ConnectionString))` (engine endpoint) and a plain `KustoIngestionProperties`. If Q1 came back "no", this class instead issues `.ingest inline into table <table> …` through `IKustoQueryExecutor.ExecuteControlCommandAsync` and carries an XML `<remarks>` saying **emulator/dev only, never production**.

`TelemetryGuard.Analytics.Kusto/KustoValueMapping.cs` — the null/NaN contract in one place, used by the sinks, the queries and the contract fixture:

```csharp
public static class KustoValueMapping
{
    /// <summary>§7 missing != zero. A Kusto `real` that is null (absent) reads back
    /// as NaN, never 0. Under Q2-primary the stored value is already NaN and this is
    /// a pass-through; under Q2-fallback (null-encoding) this IS the contract.</summary>
    public static float RealToFloat(object? value) =>
        value is null or DBNull ? float.NaN : Convert.ToSingle(value);

    public static long ToInt64OrZero(object value) => value is DBNull ? 0L : Convert.ToInt64(value);
    public static double ToDoubleOrNaN(object value) => value is DBNull ? double.NaN : Convert.ToDouble(value);
    public static DateTime AsUtc(object value) =>
        DateTime.SpecifyKind(Convert.ToDateTime(value), DateTimeKind.Utc);

    /// <summary>IPv4 inputs must compare equal to stored values on both read and
    /// write paths — the Kusto twin of ANA-03's ToIpV6 (no cross-provider reference).</summary>
    public static string NormalizeIp(string ip)
    {
        var addr = IPAddress.TryParse(ip, out var parsed) ? parsed : IPAddress.IPv6None;
        return (addr.AddressFamily == AddressFamily.InterNetwork ? addr.MapToIPv6() : addr).ToString();
    }
}
```

`NormalizeIp("10.1.2.3")` yields `"::ffff:10.1.2.3"`, which the contract suite's `SameIp` helper (`IPAddress.Parse(rendered).MapToIPv6()`) accepts.

### Step 4 — `schema/0001_events.kql`

Create `TelemetryGuard.Analytics.Kusto/schema/0001_events.kql`. Columns are the 71 `ColumnNames` entries **in the same order**, with ClickHouse types translated as follows (the whole translation table, so there is nothing to guess):

| ClickHouse (ANA-02) | Kusto | Notes |
| --- | --- | --- |
| `UUID` | `guid` | `tenant_id` |
| `String` / `LowCardinality(String)` / `Nullable(String)` | `string` | Kusto has no LowCardinality; an absent string is `""`/null and `isempty()` covers both |
| `Array(String)` | `dynamic` | `header_names`, `rule_hits` |
| `IPv6` | `string` | Kusto has no IP column type — store the normalized mapped-IPv6 text (step 3) |
| `Nullable(UInt8)` used as a flag | `bool` | Kusto scalars are natively nullable — **map `bool?` straight through, no 0/1 encoding** |
| `UInt8` (`has_js_beacon`) | `bool` | |
| `Float32` | `real` | 64-bit; NaN semantics per Q2 |
| `Int32` (velocity) | `int` | |
| `Nullable(Int16)` (`score`, `shadow_score`) | `int` | |
| `Nullable(UInt16)` (`feature_set_version`), `UInt16` (`retention_days`) | `int` | |
| `Nullable(UInt32)` (`cf_asn`, `asn`) | `long` | `uint` does not fit `int` |
| `DateTime64(3,'UTC')` | `datetime` | Kusto datetimes are always UTC |

```kusto
// ---------------------------------------------------------------------------
// P2-05 / 0001_events.kql — the Kusto mirror of ANA-02's schema/0001_events.sql
// (+ the RSK-08 columns added by 0002_training_and_shadow.sql). Column ORDER and
// NAMES match KustoEventSink.ColumnNames / ClickHouseEventSink.ColumnNames
// exactly; KustoIngestionMappingTests enforces that.
//
// Retention (D20): Kusto has NO per-row TTL. The table policy below is the
// 180-day BACKSTOP (the D20 maximum); the per-row half of D20 is
// KustoRetentionSweeper (P2-05 step 8), driven by the retention_days column.
// ---------------------------------------------------------------------------
.create-merge table tg_events (
    tenant_id: guid,
    site_key: string,
    session_id: string,
    kind: string,
    campaign_id: string,
    gclid: string,
    fbclid: string,
    msclkid: string,
    ttclid: string,
    click_id_invalid: bool,
    ip: string,
    header_names: dynamic,
    user_agent: string,
    sec_ch_ua: string,
    sec_ch_ua_mobile: string,
    sec_ch_ua_platform: string,
    accept_language: string,
    referrer: string,
    tls_ja3: string,
    tls_ja4: string,
    cf_asn: long,
    country: string,
    asn: long,
    asn_org: string,
    asn_type: string,
    is_datacenter: bool,
    is_proxy: bool,
    is_vpn: bool,
    is_tor: bool,
    is_private_relay: bool,
    has_js_beacon: bool,
    beacon_integrity_ok: bool,
    fingerprint_visitor_id: string,
    storage_age_sec: real,
    webdriver_flag: bool,
    headless_browser: bool,
    screen_width: real,
    screen_height: real,
    timezone: string,
    language: string,
    mouse_event_count: real,
    key_event_count: real,
    touch_event_count: real,
    scroll_event_count: real,
    mean_inter_event_ms: real,
    std_inter_event_ms: real,
    mouse_path_linearity: real,
    first_interaction_delay_ms: real,
    form_fill_time_sec: real,
    autofill_detected: bool,
    paste_in_identity_fields: bool,
    honeypot_touched: bool,
    pointer_untrusted: bool,
    input_modality_mismatch: bool,
    time_on_page_sec: real,
    pages_viewed: real,
    ip_clicks_last_min: int,
    ip_distinct_uas_last_hour: int,
    device_sessions_last_hour: int,
    device_ids_this_ip_hour: int,
    score: int,
    band: string,
    action: string,
    rule_hits: dynamic,
    scorer_version: string,
    feature_set_version: int,
    retention_days: int,
    timestamp: datetime,
    features: string,
    shadow_score: int,
    shadow_scorer_version: string
)

// tg_labels: training labels (D18/D19). ClickHouse gives `weight` a DEFAULT of 1;
// Kusto has no column defaults, so KustoLabelSink writes 1.0 explicitly.
.create-merge table tg_labels (
    tenant_id: guid,
    session_id: string,
    label: string,
    label_source: string,
    created_at: datetime,
    weight: real
)

// D20 backstop: 180 days is the maximum tenant-configurable retention. Per-row
// expiry inside that window is KustoRetentionSweeper's job.
.alter-merge table tg_events policy retention softdelete = 180d recoverability = disabled

// Mirrors ClickHouse's fixed 400-day tg_labels TTL (ANA-02).
.alter-merge table tg_labels policy retention softdelete = 400d recoverability = disabled

// Ingestion mappings. One entry per KustoEventSink.ColumnNames name, SAME ORDER.
// Generate this block once from ColumnNames, then let the unit test keep it honest.
.create-or-alter table tg_events ingestion json mapping "tg_events_json_mapping"
'['
'  {"Column":"tenant_id","Properties":{"Path":"$.tenant_id"}},'
'  {"Column":"site_key","Properties":{"Path":"$.site_key"}},'
'  … one line per column, in ColumnNames order …'
'  {"Column":"shadow_scorer_version","Properties":{"Path":"$.shadow_scorer_version"}}'
']'

.create-or-alter table tg_labels ingestion json mapping "tg_labels_json_mapping"
'[{"Column":"tenant_id","Properties":{"Path":"$.tenant_id"}},'
' {"Column":"session_id","Properties":{"Path":"$.session_id"}},'
' {"Column":"label","Properties":{"Path":"$.label"}},'
' {"Column":"label_source","Properties":{"Path":"$.label_source"}},'
' {"Column":"created_at","Properties":{"Path":"$.created_at"}},'
' {"Column":"weight","Properties":{"Path":"$.weight"}}]'
```

Generate the 71 mapping lines mechanically (a throwaway script over `ColumnNames`), never by hand-typing — then `KustoIngestionMappingTests` (step 11) parses the embedded `.kql` and asserts the mapped column list is **exactly** `KustoEventSink.ColumnNames`, same names, same order, so the two can never drift. This is the Kusto analogue of ANA-03's "MUST match schema column order" comment.

### Step 5 — `KustoSchemaMigrator`

`TelemetryGuard.Analytics.Kusto/KustoSchemaMigrator.cs` — the Kusto twin of ANA-02's `SchemaMigrator`, same journal-once contract:

```csharp
/// <summary>
/// Applies embedded schema/*.kql scripts in ordinal filename order, once each,
/// journaled in tg_schema_migrations. Kusto analog of ANA-02's SchemaMigrator
/// (D10: versioned scripts per store). Invoked by the MigrationRunner console
/// (DAT-01) via its --kusto switch.
/// </summary>
public sealed class KustoSchemaMigrator(IKustoQueryExecutor executor)
{
    public async Task<IReadOnlyList<string>> ApplyAsync(
        bool enableStreamingIngestion = false, CancellationToken ct = default);
}
```

Behavior, step by step:

1. `.create-merge table tg_schema_migrations (script_name: string, applied_at: datetime)`.
2. Read applied names: query `tg_schema_migrations | distinct script_name`.
3. Enumerate embedded resources containing `".schema."` and ending `.kql`, `OrderBy(n => n, StringComparer.Ordinal)` — identical to `SchemaMigrator`. Derive `scriptName` the same way (`resource[(resource.IndexOf(marker) + marker.Length)..]`).
4. For each unapplied script, `SplitCommands(text)` and run each command through `ExecuteControlCommandAsync`.
5. Journal: `.set-or-append tg_schema_migrations <| print script_name = "<name>", applied_at = now()`. The script name is a filename from an embedded resource (never user input); still, reject any name containing a quote or backslash with `InvalidOperationException` before composing the command.
6. When `enableStreamingIngestion` is true, additionally run (NOT journaled — idempotent policy commands, and they are the only commands allowed to fail with a logged warning, because a production cluster may have streaming ingestion disabled at cluster level):
   `.alter table tg_events policy streamingingestion enable` and the same for `tg_labels`.

`internal static IEnumerable<string> SplitCommands(string kql)` — the KQL analogue of `SchemaMigrator.SplitStatements`, and the rule is deliberately dead simple so it is unit-testable and unambiguous:

> Normalize `\r\n` → `\n`. A new command starts at any line whose **first character is `.` at column 0**. All following lines (including blank ones and continuation lines that start with whitespace, `'`, `{` or `[`) belong to that command until the next column-0 `.` line. Lines before the first command, and any line whose first two characters are `//`, are dropped. Trailing whitespace is trimmed from each command; empty commands are skipped.

### Step 6 — `KustoAnalyticsQueries`

`TelemetryGuard.Analytics.Kusto/KustoAnalyticsQueries.cs`:

```csharp
/// <summary>
/// Intent-named reads over tg_events in native KQL (D7). Tenant comes exclusively
/// from ITenantContext (D11) — never a parameter. Registered SCOPED (step 9)
/// because ITenantContext is scoped. Every value reaches KQL through a declared
/// query parameter — never string interpolation. Aggregate results honor §7 null
/// semantics (missing != zero: empty-scope averages are NaN) and reproduce
/// ClickHouseAnalyticsQueries' arithmetic exactly, because ANA-06 asserts both.
/// </summary>
public sealed class KustoAnalyticsQueries(
    ITenantContext tenant,
    IOptions<KustoAnalyticsOptions> options,
    IClock clock,
    IKustoQueryExecutor executor) : IAnalyticsQueries
```

Parameter binding uses the KQL `declare query_parameters(...)` prologue plus `ClientRequestProperties.SetParameter(name, value)` — the KQL equivalent of ClickHouse's `{name:Type}` binding. There is **no** string interpolation of user-supplied values anywhere (the only exception is Q4-fallback, and only after `limit` has been validated as a positive int).

**`GetIpVelocityAsync`** — same guard clause as ANA-04 (`ArgumentException.ThrowIfNullOrWhiteSpace(ip)`), same window arithmetic (`windowEnd = clock.UtcNow.UtcDateTime; windowStart = windowEnd - window`), returns `IpVelocityStats(Ip: ip, Window: window, WindowEndUtc: windowEnd, …)` with the **original** `ip` string, not the normalized one:

```kusto
declare query_parameters(tenantId: guid, ipAddr: string, fromTs: datetime, toTs: datetime);
tg_events
| where tenant_id == tenantId
    and ip == ipAddr
    and timestamp >= fromTs
    and timestamp <  toTs
| summarize
    click_count           = count(),
    distinct_sessions     = count_distinctif(session_id, isnotempty(session_id)),
    distinct_user_agents  = count_distinctif(user_agent, isnotempty(user_agent)),
    distinct_fingerprints = count_distinctif(fingerprint_visitor_id, isnotempty(fingerprint_visitor_id)),
    flagged_count         = countif(isnotnull(score) and score >= 71)
```

The `isnotempty(...)` guards are **load-bearing**: ClickHouse's `uniqExact` ignores NULLs, but an absent string ingested into Kusto is an empty string, which a naive `count_distinct` would count as a value — contract test 2 expects `DistinctUserAgents == 2` over rows whose user agents are `ua1, ua1, ua2, null, ua2, ua1`. A `summarize` with no `by` always returns exactly one row (zeros over an empty scope), so read it with a single `Read()` like ANA-04 does.

**`GetCampaignReportAsync`** — `ArgumentException.ThrowIfNullOrWhiteSpace(campaignId)`:

```kusto
declare query_parameters(tenantId: guid, campaign: string, fromTs: datetime, toTs: datetime);
tg_events
| where tenant_id == tenantId
    and campaign_id == campaign
    and timestamp >= fromTs
    and timestamp <  toTs
| summarize
    total_events      = count(),
    scored_events     = countif(kind == 'verdict'),
    allowed           = countif(kind == 'verdict' and band == 'allow'),
    challenged        = countif(kind == 'verdict' and band == 'challenge'),
    blocked           = countif(kind == 'verdict' and band == 'block'),
    score_sum         = sumif(tolong(score), kind == 'verdict' and isnotnull(score)),
    scored_with_score = countif(kind == 'verdict' and isnotnull(score)),
    no_js_beacon      = countif(kind == 'verdict' and has_js_beacon == false)
    by day = startofday(timestamp)
| order by day asc
```

Per-day mapping: `Day = DateOnly.FromDateTime(Convert.ToDateTime(r["day"]))`, `ScoreSum = ToInt64OrZero(r["score_sum"])`, and

```csharp
// ANA-04 computes the daily average as avgIf(score, kind='verdict' AND isNotNull(score));
// deriving it from the two counters gives the identical value and avoids depending on
// Kusto's empty-scope avg semantics. Missing != zero: 0 scored rows => NaN, never 0.0.
AvgScore = scoredWithScore == 0 ? double.NaN : (double)scoreSum / scoredWithScore
```

Report-level roll-up, **byte-for-byte ANA-04's arithmetic** (note the deliberate asymmetry — the report divides by `ScoredEvents`, the day divides by scored-with-score):

```csharp
var totalScored   = days.Sum(d => d.ScoredEvents);
var totalScoreSum = days.Sum(d => d.ScoreSum);
var avg = totalScored == 0 ? double.NaN : (double)totalScoreSum / totalScored;
return new CampaignFraudReport(campaignId, range,
    days.Sum(d => d.TotalEvents), totalScored,
    days.Sum(d => d.Allowed), days.Sum(d => d.Challenged), days.Sum(d => d.Blocked),
    avg, days.Sum(d => d.NoJsBeaconCount), days);
```

**`GetTopFlaggedSourcesAsync`** — `ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit)`:

```kusto
declare query_parameters(tenantId: guid, fromTs: datetime, toTs: datetime, lim: long);
tg_events
| where tenant_id == tenantId
    and kind == 'verdict'
    and timestamp >= fromTs
    and timestamp <  toTs
| summarize
    flagged_events = countif(band in ('challenge', 'block')),
    blocked_events = countif(band == 'block'),
    total_events   = count(),
    score_sum      = sumif(tolong(score), isnotnull(score)),
    scored         = countif(isnotnull(score)),
    first_seen     = min(timestamp),
    last_seen      = max(timestamp)
    by source_value = ip
| where flagged_events > 0
| order by blocked_events desc, flagged_events desc
| take lim
```

Map to `FlaggedSource` with `SourceType: "ip"` (constant), `SourceValue` = the stored normalized text (the suite compares parsed addresses, so `"::ffff:10.1.2.3"` is fine), `AvgScore = scored == 0 ? double.NaN : (double)score_sum / scored`, `FirstSeenUtc/LastSeenUtc = AsUtc(...)`.

### Step 7 — `KustoEventSink` and `KustoLabelSink`

`TelemetryGuard.Analytics.Kusto/KustoEventSink.cs` — copy `ClickHouseEventSink`'s channel/batch/drain/metrics machinery **verbatim in shape** (same `BoundedChannelFullMode.DropWrite`, same `SingleReader = true`, same age-vs-size batching, same retry-then-drop policy, same `IHostedService` start/stop), changing only the flush body, the meter name, and the row mapping. Deliberately its own concrete class — **no shared generic base with the ClickHouse sink and no reference to that project.**

```csharp
public sealed class KustoEventSink : IEventSink, IHostedService
{
    // MUST match schema/0001_events.kql column order AND the ingestion mapping.
    // Same 71 names, same order, as ClickHouseEventSink.ColumnNames (ANA-03) —
    // duplicated on purpose: providers never reference each other (D7).
    internal static readonly string[] ColumnNames =
    {
        "tenant_id", "site_key", "session_id", "kind",
        "campaign_id", "gclid", "fbclid", "msclkid", "ttclid", "click_id_invalid",
        "ip", "header_names", "user_agent", "sec_ch_ua", "sec_ch_ua_mobile",
        "sec_ch_ua_platform", "accept_language", "referrer", "tls_ja3", "tls_ja4", "cf_asn",
        "country", "asn", "asn_org", "asn_type",
        "is_datacenter", "is_proxy", "is_vpn", "is_tor", "is_private_relay",
        "has_js_beacon", "beacon_integrity_ok", "fingerprint_visitor_id", "storage_age_sec",
        "webdriver_flag", "headless_browser", "screen_width", "screen_height",
        "timezone", "language",
        "mouse_event_count", "key_event_count", "touch_event_count", "scroll_event_count",
        "mean_inter_event_ms", "std_inter_event_ms", "mouse_path_linearity",
        "first_interaction_delay_ms", "form_fill_time_sec",
        "autofill_detected", "paste_in_identity_fields", "honeypot_touched",
        "pointer_untrusted", "input_modality_mismatch",
        "time_on_page_sec", "pages_viewed",
        "ip_clicks_last_min", "ip_distinct_uas_last_hour",
        "device_sessions_last_hour", "device_ids_this_ip_hour",
        "score", "band", "action", "rule_hits", "scorer_version", "feature_set_version",
        "retention_days", "timestamp",
        "features", "shadow_score", "shadow_scorer_version"
    };

    internal const string TableName   = "tg_events";
    internal const string MappingName = "tg_events_json_mapping";
```

`MapRow` — same order as `ColumnNames`, but **Kusto-native nullability** (no `bool?` → `byte?` encoding, unlike ClickHouse):

```csharp
internal static object?[] MapRow(ClickEvent e) =>
[
    e.TenantId.Value,
    e.SiteKey, e.SessionId, e.Kind.ToWire(),
    e.CampaignId, e.Gclid, e.Fbclid, e.Msclkid, e.Ttclid, e.ClickIdInvalid,
    KustoValueMapping.NormalizeIp(e.Ip),
    e.HeaderNames as string[] ?? e.HeaderNames.ToArray(),
    e.UserAgent, e.SecChUa, e.SecChUaMobile, e.SecChUaPlatform,
    e.AcceptLanguage, e.Referrer, e.TlsJa3, e.TlsJa4, (long?)e.CfAsn,
    e.Country, (long?)e.Asn, e.AsnOrg, e.AsnType,
    e.IsDatacenter, e.IsProxy, e.IsVpn, e.IsTor, e.IsPrivateRelay,
    e.HasJsBeacon, e.BeaconIntegrityOk, e.FingerprintVisitorId, e.StorageAgeSec,
    e.WebdriverFlag, e.HeadlessBrowser, e.ScreenWidth, e.ScreenHeight,
    e.Timezone, e.Language,
    e.MouseEventCount, e.KeyEventCount, e.TouchEventCount, e.ScrollEventCount,
    e.MeanInterEventMs, e.StdInterEventMs, e.MousePathLinearity,
    e.FirstInteractionDelayMs, e.FormFillTimeSec,
    e.AutofillDetected, e.PasteInIdentityFields, e.HoneypotTouched,
    e.PointerUntrusted, e.InputModalityMismatch,
    e.TimeOnPageSec, e.PagesViewed,
    e.IpClicksLastMin, e.IpDistinctUasLastHour,
    e.DeviceSessionsLastHour, e.DeviceIdsThisIpHour,
    e.Score, e.Band, e.Action,
    e.RuleHits as string[] ?? e.RuleHits.ToArray(),
    e.ScorerVersion, e.FeatureSetVersion,
    (int)e.RetentionDays,
    DateTime.SpecifyKind(e.TimestampUtc, DateTimeKind.Utc),
    e.Features, e.ShadowScore, e.ShadowScorerVersion ?? ""
];
```

Serialization — `internal static void WriteBatchJson(Utf8JsonWriter w, IReadOnlyList<ClickEvent> batch)`:

- Write a **JSON array of objects** (`WriteStartArray()` … `WriteEndArray()`), which is a valid `multijson` payload and avoids `Utf8JsonWriter`'s multiple-root-value restriction.
- Per event: `WriteStartObject()`, then for `i` in `0..ColumnNames.Length-1` write `ColumnNames[i]` against `row[i]` with this exact value dispatch — `null` → `WriteNull(name)`; `string s` → `WriteString`; `bool b` → `WriteBoolean`; `Guid g` → `WriteString(name, g)`; `DateTime d` → `WriteString(name, d.ToString("O", CultureInfo.InvariantCulture))`; `float f` → **Q2-primary**: `float.IsFinite(f) ? WriteNumber(name, f) : WriteString(name, "NaN")`, **Q2-fallback**: `float.IsFinite(f) ? WriteNumber(name, f) : WriteNull(name)`; `int`/`long`/`ushort` → `WriteNumber`; `string[] a` → `WriteStartArray(name)` + `WriteStringValue` each.
- `null` here is what makes contract tests 3/4/5 work: an absent `score` MUST be JSON `null` (so `isnotnull(score)` is false), never `0`.

Flush body (replacing `ClickHouseBulkCopy`), retry/backoff/drop policy unchanged:

```csharp
using var buffer = new MemoryStream();
await using (var w = new Utf8JsonWriter(buffer)) { WriteBatchJson(w, batch); await w.FlushAsync(); }
for (var attempt = 1; ; attempt++)
{
    try
    {
        buffer.Position = 0;
        await _transport.IngestAsync(buffer, TableName, MappingName, CancellationToken.None)
            .ConfigureAwait(false);
        Written.Add(batch.Count); BatchesFlushed.Add(1);
        return;
    }
    catch (Exception ex) when (attempt <= _opts.FlushMaxRetries) { /* BatchRetries + backoff, as ANA-03 */ }
    catch (Exception ex) { /* DroppedFlush.Add(batch.Count) + LogError "D7: eventual delivery only" */ }
}
```

`KustoLabelSink` — the same treatment for `ILabelSink`, its own concrete class, `ColumnNames = { "tenant_id", "session_id", "label", "label_source", "created_at", "weight" }`, `TableName = "tg_labels"`, `MappingName = "tg_labels_json_mapping"`, and:

```csharp
internal static object?[] MapRow(LabelEvent l) =>
[
    l.TenantId.Value, l.SessionId, l.Label, l.LabelSource,
    DateTime.SpecifyKind(l.CreatedAtUtc, DateTimeKind.Utc),
    1.0f   // ClickHouse gets this from `weight Float32 DEFAULT 1`; Kusto has no defaults.
];
```

### Step 8 — Retention: the D20 mechanism for Kusto (resolves outline open decision 1)

**Decision, and why.** Kusto has no per-row TTL, so D20 is implemented in two layers:

1. **Backstop (declarative, in the schema script):** `.alter-merge table tg_events policy retention softdelete = 180d` — 180 days is D20's stated maximum, so nothing can outlive the policy even if the sweep stops running. `tg_labels` gets `400d`, mirroring ClickHouse's fixed 400-day label TTL.
2. **Per-row (imperative, a scheduled sweep):** a daily soft-delete driven by the `retention_days` column that every row already carries — the same denormalized value ClickHouse's TTL expression reads. This is the piece that makes a 30-day tenant actually expire at 30 days.

Rejected alternatives, recorded so nobody re-litigates them: *retention-bucket tables* (one table per retention value + update-policy routing) multiplies the schema by the 30–180 range and breaks the single-`tg_events` shape the queries assume; *`.purge`* is a DM-side GDPR tool measured in hours and is unavailable on the emulator; *materialized views* solve aggregation, not expiry.

`TelemetryGuard.Analytics.Kusto/KustoRetentionSweeper.cs`:

```csharp
/// <summary>
/// D20 per-row retention for Kusto. ClickHouse expresses this as a per-row TTL
/// (`timestamp + toIntervalDay(retention_days)`); Kusto has no per-row TTL, so the
/// same policy is enforced by this scheduled soft delete. D20 explicitly allows —
/// requires — each provider to own its retention mechanism (D7).
/// The 180d table retention policy in schema/0001_events.kql is the backstop; this
/// is the per-tenant half. Tenant-agnostic on purpose: the predicate is per ROW,
/// so a single command covers every tenant (no ITenantContext here).
/// </summary>
public sealed class KustoRetentionSweeper(
    IKustoQueryExecutor executor,
    IOptions<KustoAnalyticsOptions> options,
    ILogger<KustoRetentionSweeper> log)
{
    internal const string SweepCommand =
        """
        .delete table tg_events records <|
        tg_events
        | where timestamp + (retention_days * 1d) < now()
        """;

    public async Task<bool> SweepAsync(CancellationToken ct);   // true = command accepted
}
```

`TelemetryGuard.Api/Workers/KustoRetentionService.cs` — a thin `BackgroundService` in the house workers folder, following `GoogleAdsExclusionSyncService`'s shape (`PeriodicTimer`, run once at startup after a short delay, one try/catch per cycle so a failure never kills the host):

```csharp
public sealed class KustoRetentionService(
    KustoRetentionSweeper sweeper, IOptions<KustoAnalyticsOptions> options,
    ILogger<KustoRetentionService> log) : BackgroundService
```

- Exits immediately (logging once at Information) when `RetentionSweepEnabled` is false.
- `new PeriodicTimer(TimeSpan.FromHours(opts.RetentionSweepIntervalHours))`.
- OTel counters (API-01 conventions): `tg.kusto.retention_sweeps`, `tg.kusto.retention_sweep_failures`.
- **Never runs when the provider is ClickHouse** — it is registered only inside the switch's `"Kusto"` branch (step 9).

### Step 9 — Move the D7 switch to the composition root and register Kusto

This is the step ANA-05 pre-authorized. Three edits, in order.

**9a. ClickHouse project — demote the switch to a provider-local registration.** Rename `TelemetryGuard.Analytics.ClickHouse/AnalyticsServiceCollectionExtensions.cs` → `ClickHouseServiceCollectionExtensions.cs`, class `ClickHouseServiceCollectionExtensions`, method `AddClickHouseAnalytics`. Keep every registration byte-identical; the only behavioral change is that the options `Validate` predicate no longer has to test the provider name (the method is only called on the `"ClickHouse"` branch), and the switch itself is gone:

```csharp
namespace TelemetryGuard.Analytics.ClickHouse;

public static class ClickHouseServiceCollectionExtensions
{
    /// <summary>
    /// Registers the ClickHouse analytics provider. Called ONLY from the D7 provider
    /// switch in the composition root (TelemetryGuard.Api/Analytics/
    /// AnalyticsServiceCollectionExtensions.cs) — moved out of this project by P2-05
    /// so that neither provider references the other (ANA-05's own instruction).
    /// </summary>
    public static IServiceCollection AddClickHouseAnalytics(
        this IServiceCollection s, IConfiguration cfg)
    {
        s.AddOptions<ClickHouseAnalyticsOptions>()
            .Bind(cfg.GetSection("Analytics:ClickHouse"))
            .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString),
                "Analytics:ClickHouse:ConnectionString is required when Analytics:Provider is 'ClickHouse'.")
            .Validate(o => o.EventQueueCapacity > 0 && o.EventMaxBatchSize > 0
                           && o.EventMaxBatchAgeSeconds > 0 && o.FlushMaxRetries >= 0,
                "Analytics:ClickHouse sink tuning values must be positive.")
            .ValidateOnStart();

        // Concrete sinks are registered ONCE and forwarded so IEventSink/ILabelSink
        // and IHostedService resolve the SAME instance — two instances would split
        // the queue from the flusher.
        return s
            .AddSingleton<ClickHouseEventSink>()
            .AddSingleton<IEventSink>(sp => sp.GetRequiredService<ClickHouseEventSink>())
            .AddSingleton<IHostedService>(sp => sp.GetRequiredService<ClickHouseEventSink>())
            .AddSingleton<ClickHouseLabelSink>()
            .AddSingleton<ILabelSink>(sp => sp.GetRequiredService<ClickHouseLabelSink>())
            .AddSingleton<IHostedService>(sp => sp.GetRequiredService<ClickHouseLabelSink>())
            // Scoped (not singleton as in the D7 sketch): consumes scoped ITenantContext (D11).
            .AddScoped<IAnalyticsQueries, ClickHouseAnalyticsQueries>();
    }
}
```

**9b. Kusto project — the twin.** `TelemetryGuard.Analytics.Kusto/KustoServiceCollectionExtensions.cs`:

```csharp
public static IServiceCollection AddKustoAnalytics(this IServiceCollection s, IConfiguration cfg)
{
    s.AddOptions<KustoAnalyticsOptions>()
        .Bind(cfg.GetSection("Analytics:Kusto"))
        .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString),
            "Analytics:Kusto:ConnectionString is required when Analytics:Provider is 'Kusto'.")
        .Validate(o => !string.IsNullOrWhiteSpace(o.Database),
            "Analytics:Kusto:Database is required when Analytics:Provider is 'Kusto'.")
        .Validate(o => o.IngestMode is KustoAnalyticsOptions.QueuedMode or KustoAnalyticsOptions.StreamingMode,
            "Analytics:Kusto:IngestMode must be 'Queued' or 'Streaming'.")
        .Validate(o => o.IngestMode != KustoAnalyticsOptions.QueuedMode
                       || !string.IsNullOrWhiteSpace(o.IngestConnectionString),
            "Analytics:Kusto:IngestConnectionString is required when IngestMode is 'Queued'.")
        .Validate(o => o.EventQueueCapacity > 0 && o.EventMaxBatchSize > 0
                       && o.EventMaxBatchAgeSeconds > 0 && o.FlushMaxRetries >= 0,
            "Analytics:Kusto sink tuning values must be positive.")
        .ValidateOnStart();

    s.AddSingleton<IKustoQueryExecutor, KustoQueryExecutor>();
    s.AddSingleton<IKustoIngestTransport>(sp =>
    {
        var o = sp.GetRequiredService<IOptions<KustoAnalyticsOptions>>();
        return o.Value.IngestMode == KustoAnalyticsOptions.StreamingMode
            ? new StreamingKustoIngestTransport(o, sp.GetRequiredService<IKustoQueryExecutor>())
            : new QueuedKustoIngestTransport(o);
    });
    s.AddSingleton<KustoRetentionSweeper>();

    return s
        .AddSingleton<KustoEventSink>()
        .AddSingleton<IEventSink>(sp => sp.GetRequiredService<KustoEventSink>())
        .AddSingleton<IHostedService>(sp => sp.GetRequiredService<KustoEventSink>())
        .AddSingleton<KustoLabelSink>()
        .AddSingleton<ILabelSink>(sp => sp.GetRequiredService<KustoLabelSink>())
        .AddSingleton<IHostedService>(sp => sp.GetRequiredService<KustoLabelSink>())
        .AddScoped<IAnalyticsQueries, KustoAnalyticsQueries>();
}
```

**9c. Api — the one and only switch.** New file `TelemetryGuard.Api/Analytics/AnalyticsServiceCollectionExtensions.cs`, namespace `TelemetryGuard.Api.Analytics`, keeping the public entry-point name so `Program.cs` changes by one `using`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using TelemetryGuard.Analytics.ClickHouse;
using TelemetryGuard.Analytics.Kusto;
using TelemetryGuard.Api.Health;
using TelemetryGuard.Api.Workers;

namespace TelemetryGuard.Api.Analytics;

public static class AnalyticsServiceCollectionExtensions
{
    /// <summary>
    /// The D7 provider switch (spec D6/D7), living in the composition root exactly as
    /// ANA-05 said it must once the second provider landed (P2-05): neither provider
    /// project references the other, and this is the ONLY place in the solution that
    /// branches on Analytics:Provider. Config shape:
    /// <c>"Analytics": { "Provider": "ClickHouse"|"Kusto", "ClickHouse": {...}, "Kusto": {...} }</c>
    /// Unknown or missing Analytics:Provider aborts startup — silent defaults hide
    /// misconfiguration. Each branch also owns its readiness check (deliberately
    /// engine-specific: there is no generic analytics-engine health abstraction).
    /// </summary>
    public static IServiceCollection AddTelemetryGuardAnalytics(
        this IServiceCollection s, IConfiguration cfg) =>
        cfg["Analytics:Provider"] switch
        {
            "ClickHouse" => s.AddClickHouseAnalytics(cfg)
                .AddHealthChecks().AddCheck<ClickHouseHealthCheck>("clickhouse", tags: ["ready"]).Services,

            // Kusto also owns D20 retention: Kusto has no per-row TTL, so the sweep
            // worker is part of the provider's contract, not an optional extra.
            "Kusto" => s.AddKustoAnalytics(cfg)
                .AddHealthChecks().AddCheck<KustoHealthCheck>("kusto", tags: ["ready"]).Services
                .AddHostedService<KustoRetentionService>(),

            var p => throw new InvalidOperationException($"Unknown analytics provider '{p}'")
        };
}
```

Then in `TelemetryGuard.Api/Program.cs`:

- replace `using TelemetryGuard.Analytics.ClickHouse;` (line 19) with `using TelemetryGuard.Api.Analytics;`;
- leave `builder.Services.AddTelemetryGuardAnalytics(builder.Configuration);` untouched;
- **delete** `.AddCheck<ClickHouseHealthCheck>("clickhouse", tags: ["ready"])` from the `AddHealthChecks()` block (~line 232) — it now lives in the switch, so a Kusto deployment no longer reports `/ready` unhealthy for an absent ClickHouse. `AddHealthChecks()` is additive and may be called from both places.

Add `<ProjectReference Include="..\TelemetryGuard.Analytics.Kusto\TelemetryGuard.Analytics.Kusto.csproj" />` to `TelemetryGuard.Api.csproj`.

`TelemetryGuard.Api/Health/KustoHealthCheck.cs` — the Kusto twin of `ClickHouseHealthCheck`, same "deliberately engine-specific" comment:

```csharp
public sealed class KustoHealthCheck(IKustoQueryExecutor executor) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try { return await executor.PingAsync(ct) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("kusto ping failed"); }
        catch (Exception ex) { return HealthCheckResult.Unhealthy("kusto", ex); }
    }
}
```

**appsettings.** In `TelemetryGuard.Api/appsettings.json`, replace the ANA-05 comment block above `"Analytics"` (currently "…'Kusto' is deferred to P2-05") with one that says both providers are implemented and the switch lives in `TelemetryGuard.Api/Analytics/`, extend the API-01 section-ownership header comment with `Analytics:Kusto → P2-05`, and add the sibling section — **shipped inert: `Provider` stays `"ClickHouse"` and the Kusto connection strings stay empty, so no default deployment can dial a cluster**:

```json
"Kusto": {
  "ConnectionString": "",
  "IngestConnectionString": "",
  "Database": "telemetry_guard",
  "IngestMode": "Queued",
  "EventQueueCapacity": 100000,
  "EventMaxBatchSize": 5000,
  "EventMaxBatchAgeSeconds": 2,
  "LabelQueueCapacity": 10000,
  "LabelMaxBatchSize": 500,
  "LabelMaxBatchAgeSeconds": 5,
  "FlushMaxRetries": 3,
  "FlushRetryBaseDelayMs": 200,
  "ShutdownDrainTimeoutSeconds": 10,
  "QueryTimeoutSeconds": 30,
  "RetentionSweepEnabled": true,
  "RetentionSweepIntervalHours": 24
}
```

`appsettings.Development.json` keeps `Provider: "ClickHouse"` and gains **no** Kusto section — local dev stays on the compose stack.

### Step 10 — MigrationRunner `--kusto`

`TelemetryGuard.MigrationRunner/TelemetryGuard.MigrationRunner.csproj`: add `<ProjectReference Include="..\TelemetryGuard.Analytics.Kusto\TelemetryGuard.Analytics.Kusto.csproj" />` next to the ClickHouse one.

`TelemetryGuard.MigrationRunner/Migrations.cs` — the twin of `RunClickHouseAsync`:

```csharp
/// <summary>Delegates Kusto schema application to P2-05's KustoSchemaMigrator
/// (single journal: tg_schema_migrations).</summary>
public static async Task<int> RunKustoAsync(string connectionString, string database, bool enableStreaming)
```

It constructs the options + `KustoQueryExecutor` directly (`Options.Create(new KustoAnalyticsOptions { ConnectionString = …, Database = … })`, `NullLogger<KustoQueryExecutor>.Instance`), runs `ApplyAsync(enableStreaming)`, prints `Applied Kusto script {name}` per script then `Kusto schema up to date.`, and returns `0` / `1` on exception — exactly mirroring `RunClickHouseAsync`.

`TelemetryGuard.MigrationRunner/Program.cs`, inserted **after** the `--clickhouse` block and before the SQL Server fallback:

```csharp
if (args.Contains("--kusto", StringComparer.OrdinalIgnoreCase))
{
    var kustoCs = Environment.GetEnvironmentVariable("KUSTO_CONNECTIONSTRING");
    if (string.IsNullOrWhiteSpace(kustoCs))
    {
        Console.Error.WriteLine("KUSTO_CONNECTIONSTRING environment variable is not set.");
        return 2;
    }
    var kustoDb = Environment.GetEnvironmentVariable("KUSTO_DATABASE") ?? "telemetry_guard";
    var streaming = Environment.GetEnvironmentVariable("KUSTO_ENABLE_STREAMING") == "1";
    return await Migrations.RunKustoAsync(kustoCs, kustoDb, streaming);
}
```

### Step 11 — Tests

**11a. Contract fixture** — `tests/TelemetryGuard.Tests.Contracts/Kusto/KustoProviderFixture.cs`, the exact `ClickHouseProviderFixture` shape (start container → apply schema → real sinks with a 0.3 s batch age → raw readbacks), **compiled unconditionally** so it can never rot:

```csharp
public sealed class KustoProviderFixture : IAnalyticsProviderFixture
{
    public const string EmulatorImage = "mcr.microsoft.com/azuredataexplorer/kustainer-linux:latest";
    private const int EnginePort = 8080;

    private readonly IContainer _container = new ContainerBuilder()
        .WithImage(EmulatorImage)
        .WithEnvironment("ACCEPT_EULA", "Y")
        .WithPortBinding(EnginePort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().AddCustomWaitStrategy(new KustoEngineReady(EnginePort)))
        .Build();
    …
}
```

- `KustoEngineReady : IWaitUntil` — `UntilAsync(IContainer container)` POSTs `{"csl":".show version"}` to `http://localhost:{container.GetMappedPublicPort(8080)}/v1/rest/mgmt` and returns true on 200. Testcontainers polls it; give the container a generous startup timeout (the image is large and the engine takes ~1–2 min to warm).
- `InitializeAsync`: start the container; wrap start failures in an `InvalidOperationException` naming the prerequisites (podman socket via `DOCKER_HOST`, `TESTCONTAINERS_RYUK_DISABLED=true`, image pull) so an opted-in run fails legibly; then `new KustoSchemaMigrator(executor).ApplyAsync(enableStreamingIngestion: true)`.
- Options: `Database = "NetDefaultDB"`, `IngestMode = "Streaming"`, `ConnectionString = $"Data Source=http://localhost:{port};Federated Security=False"`, `EventMaxBatchSize = 100, EventMaxBatchAgeSeconds = 0.3, LabelMaxBatchSize = 10, LabelMaxBatchAgeSeconds = 0.3` — the same tuning the ClickHouse fixture uses so the suite's 5 s window is comfortably met.
- `CreateQueries(tenantId, utcNow)` → `new KustoAnalyticsQueries(new FixedTenantContext(tenantId), Options.Create(_opts!), new FixedClock(DateTime.SpecifyKind(utcNow ?? DateTime.UtcNow, DateTimeKind.Utc)), _executor!)` — reuse `Fakes.cs`, do not add new doubles.
- `ReadStorageAgeSecAsync` → `declare query_parameters(t:guid, s:string); tg_events | where tenant_id == t and session_id == s | project storage_age_sec | take 1`, mapped through `KustoValueMapping.RealToFloat` (throw `InvalidOperationException($"No tg_events row stored yet for session '{sessionId}'.")` when there is no row at all, matching the ClickHouse fixture).
- `CountLabelsAsync` → `tg_labels | where tenant_id == t | count`.
- `DisposeAsync`: stop both sinks, dispose the executor, dispose the container.

**11b. Contract runner + provider-specific engine tests** — `tests/TelemetryGuard.Tests.Contracts/Kusto/KustoAnalyticsContractTests.cs`:

```csharp
#if TG_KUSTO_CONTRACTS
namespace TelemetryGuard.Tests.Contracts.Kusto;

/// <summary>Kusto runner of the shared analytics contract suite (D7).</summary>
[Trait("requires", "docker")]
[Trait("provider", "kusto")]
public sealed class KustoAnalyticsContractTests(KustoProviderFixture fx)
    : AnalyticsContractTests<KustoProviderFixture>(fx);
#endif
```

**The clean-skip mechanism** (resolves outline open decision 5, and it is the only mechanism available without touching the shared suite — its `[Fact]`s are inherited, so a failed fixture would *fail*, not skip): the runner class and the emulator-only tests are **compiled only when opted in**. Add to `tests/TelemetryGuard.Tests.Contracts/TelemetryGuard.Tests.Contracts.csproj`:

```xml
<!-- P2-05: the Kusto runner is compiled only on demand. The ADX emulator image is
     ~GBs and slow to warm, and the shared suite's inherited [Fact]s cannot be
     dynamically skipped, so "unavailable" must mean "not discovered" rather than
     "failed". KustoProviderFixture itself is ALWAYS compiled — only ~40 lines of
     runner are conditional — so this cannot rot silently.
       KustoContracts=true dotnet test tests/TelemetryGuard.Tests.Contracts
     (MSBuild reads KustoContracts from the environment as well as -p:.)          -->
<PropertyGroup Condition="'$(KustoContracts)' == 'true'">
  <DefineConstants>$(DefineConstants);TG_KUSTO_CONTRACTS</DefineConstants>
</PropertyGroup>
```

so the documented default command in `CLAUDE.md` (`dotnet test tests/TelemetryGuard.Tests.Contracts`) stays green on a machine with no emulator, with **no filter and no CLAUDE.md edit**.

Under the same `#if`, add `tests/TelemetryGuard.Tests.Contracts/Kusto/KustoEngineTests.cs` (`[Trait("provider","kusto")]`, same fixture) for the two behaviors with no cross-provider analog:

- **Schema idempotency**: a second `ApplyAsync()` applies zero scripts and `tg_schema_migrations | distinct script_name` still has one row per script (the twin of the ClickHouse `SchemaMigratorTests`).
- **Retention (D20)**: seed one row with `RetentionDays = 30` and `TimestampUtc = now - 45 d` plus one with `RetentionDays = 90` and `now - 45 d`; run `KustoRetentionSweeper.SweepAsync`; poll until only the 90-day row remains. If Q5 came back "unsupported on the emulator", downgrade this to asserting the backstop via `.show table tg_events policy retention` and record the manual cloud-verification procedure in the provider README `<remarks>`.

Also add the required packages/reference to that csproj: `<PackageReference Include="Testcontainers" Version="3.10.0" />` (base package, for `ContainerBuilder`; `Testcontainers.ClickHouse` 3.10.0 already pins the same core version) and `<ProjectReference Include="..\..\TelemetryGuard.Analytics.Kusto\TelemetryGuard.Analytics.Kusto.csproj" />` (the Kusto SDK flows in transitively).

**11c. Container-free unit tests** — `tests/TelemetryGuard.Tests.Unit/Analytics/`. These are the safety net that makes the emulator optional; they run in the ordinary `build-test` job:

- `KustoIngestionMappingTests.cs` — **the drift guard.** Reads the embedded `schema/0001_events.kql`, extracts the `tg_events_json_mapping` blob, parses it with `System.Text.Json`, and asserts the `Column` values equal `KustoEventSink.ColumnNames` element-for-element **in order**; asserts every `Path` is `$.<column>`; asserts the same for `tg_labels_json_mapping` vs `KustoLabelSink.ColumnNames`; and asserts the column list declared in the `.create-merge table tg_events (...)` statement matches `ColumnNames` in order too. Three artifacts, one order, enforced.
- `KustoEventSinkMapRowTests.cs` — mirrors `ClickHouseEventSinkMapRowTests`: `MapRow` length equals `ColumnNames.Length`; a `TestEvents`-style pixel event maps every SDK float to `NaN` and every optional flag to `null`; `bool?` stays `bool?` (not `byte`); `Score`/`Band`/`Action` are `null` on a non-verdict row; `RetentionDays` boxes as `int`; `TimestampUtc` comes out `DateTimeKind.Utc`; `NormalizeIp("10.1.2.3") == "::ffff:10.1.2.3"`.
- `KustoJsonSerializationTests.cs` — round-trips `WriteBatchJson` output through `JsonDocument`: array of objects; property order equals `ColumnNames`; absent `score` is JSON `null`, **not** `0`; `header_names`/`rule_hits` are arrays; `timestamp` is round-trippable ISO-8601 with a `Z`; the NaN encoding matches whichever Q2 answer was chosen (assert on the chosen one, and name the other in a comment).
- `KustoSchemaSplitCommandsTests.cs` — mirrors `SchemaMigratorSplitStatementsTests`: `//` comment-only lines dropped; a multi-line `.create-or-alter … mapping` blob stays one command; two adjacent `.`-commands split into two; trailing whitespace trimmed; empty input yields nothing.
- `KustoAnalyticsQueriesKqlTests.cs` — asserts that **every** KQL string in the provider (exposed as `internal const string` on `KustoAnalyticsQueries` so they are testable without a cluster — three today, five if the P2-01 interlock applied) starts with `declare query_parameters(`, contains `tenant_id == tenantId`, and contains **no** `string.Format`/interpolation markers; that `GetIpVelocityAsync`'s KQL guards all three `count_distinct*` calls with `isnotempty`; and — when the interlock applied — that the placement query filters `tenant_id == tenantId` on **both** sides of the join, so the D11 proof is textual and not only behavioral.
- `AnalyticsRegistrationTests.cs` — **update the existing file** (it currently lives in this folder and tests the ANA-05 switch). It has exactly **six** `[Fact]`s today: `ClickHouseProvider_RegistersSingletonSinks_ForwardedAsHostedServices`, `ClickHouseProvider_QueriesAreScoped_NotResolvableFromRoot`, `ClickHouseProvider_EmptyConnectionString_FailsHostStart_ViaValidateOnStart`, `KustoProvider_ThrowsNotSupported_AtRegistration`, `UnknownProvider_ThrowsInvalidOperation_WithProviderName`, `MissingProvider_ThrowsAtRegistration`. Change `using TelemetryGuard.Analytics.ClickHouse;` to `using TelemetryGuard.Api.Analytics;` (add `using TelemetryGuard.Analytics.ClickHouse;` back only if a test still names a ClickHouse type directly, plus `using TelemetryGuard.Analytics.Kusto;`), keep **five of the six unchanged**, and **replace the one that is now false — `KustoProvider_ThrowsNotSupported_AtRegistration`** — with:
  - `KustoProvider_RegistersSingletonSinks_ForwardedAsHostedServices` — config `Analytics:Provider=Kusto`, `Analytics:Kusto:ConnectionString=Data Source=http://localhost:1;Federated Security=False`, `IngestMode=Streaming`; assert `IEventSink`/`ILabelSink` resolve to `KustoEventSink`/`KustoLabelSink`, are singletons, and are the same references returned by `GetServices<IHostedService>()`. **No network I/O may occur** — this is what pins the lazy-provider requirement in step 3.
  - `KustoProvider_QueriesAreScoped_NotResolvableFromRoot` — the `IAnalyticsQueries` descriptor is `ServiceLifetime.Scoped` and resolves to `KustoAnalyticsQueries` inside a scope only.
  - `KustoProvider_EmptyConnectionString_FailsHostStart_ViaValidateOnStart` and `KustoProvider_QueuedModeWithoutIngestConnectionString_FailsHostStart` — both `OptionsValidationException` from `host.StartAsync()`.
  - The unknown-provider and missing-provider tests stay exactly as they are.
- Add `<ProjectReference Include="..\..\TelemetryGuard.Analytics.Kusto\TelemetryGuard.Analytics.Kusto.csproj" />` to `tests/TelemetryGuard.Tests.Unit/TelemetryGuard.Tests.Unit.csproj` (explicit, matching how Analytics.ClickHouse is already listed even though it also arrives via the Api reference).

**11d. Docs.** Update `tests/TelemetryGuard.Tests.Contracts/README.md`: add a `Kusto/` row to the layout table, replace the "Adding a provider (e.g. Kusto, task P2-05)" section with the now-done reality (fixture + runner + the `KustoContracts=true` opt-in and why it exists), and document the two commands. Write `TelemetryGuard.Analytics.Kusto/README.md` covering: the step-0 answers, the retention design and its rejected alternatives, the queued-vs-streaming trade-off, "emulator ≠ production parity", and the manual smoke procedure against a real ADX dev cluster **run by a human, never by CI, and never against a production cluster**.

### Step 12 — CI

Add a fourth job to `.github/workflows/ci.yml`, after `integration`:

```yaml
  kusto-contracts:
    name: Kusto contract tests (ADX emulator)
    runs-on: ubuntu-latest
    timeout-minutes: 40
    needs: build-test
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET 8
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 8.0.x

      - name: Cache NuGet packages
        uses: actions/cache@v4
        with:
          path: ~/.nuget/packages
          key: nuget-${{ runner.os }}-${{ hashFiles('**/*.csproj', 'Directory.Build.props') }}
          restore-keys: |
            nuget-${{ runner.os }}-

      # Compile coverage is NEVER skipped: the conditional runner must build on every PR.
      - name: Build contracts project with the Kusto runner enabled
        run: >
          dotnet build tests/TelemetryGuard.Tests.Contracts/TelemetryGuard.Tests.Contracts.csproj
          -c Release -p:KustoContracts=true

      # The emulator image is large and occasionally unavailable; a PULL failure SKIPS
      # the run with a warning instead of failing CI (P2-05 clean-skip rule).
      - name: Pull the Kusto emulator image (preflight)
        id: emulator
        run: |
          if docker pull mcr.microsoft.com/azuredataexplorer/kustainer-linux:latest; then
            echo "available=true" >> "$GITHUB_OUTPUT"
          else
            echo "available=false" >> "$GITHUB_OUTPUT"
            echo "::warning::Kusto emulator image unavailable — Kusto contract tests skipped for this run."
          fi

      - name: Kusto contract tests
        if: steps.emulator.outputs.available == 'true'
        run: >
          dotnet test tests/TelemetryGuard.Tests.Contracts/TelemetryGuard.Tests.Contracts.csproj
          -c Release --no-build -p:KustoContracts=true
          --filter "provider=kusto"
          --logger "trx;LogFileName=kusto-contracts.trx"
          --results-directory TestResults

      - name: Upload test results
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: kusto-contract-test-results
          path: TestResults/*.trx
          if-no-files-found: ignore
```

The existing `integration` job is unchanged: its unfiltered `dotnet test tests/TelemetryGuard.Tests.Contracts` builds without `KustoContracts`, so it keeps running the ClickHouse runner only. `.gitignore` needs no change (`TestResults/` and `bin/`/`obj/` are already covered). `docker-compose.yml` and `scripts/dev-*.sh` are **not** touched — see guardrails.

## Files to create or modify

**Create**

- `TelemetryGuard.Analytics.Kusto/TelemetryGuard.Analytics.Kusto.csproj`
- `TelemetryGuard.Analytics.Kusto/KustoAnalyticsOptions.cs`
- `TelemetryGuard.Analytics.Kusto/IKustoQueryExecutor.cs`, `KustoQueryExecutor.cs`
- `TelemetryGuard.Analytics.Kusto/IKustoIngestTransport.cs`, `QueuedKustoIngestTransport.cs`, `StreamingKustoIngestTransport.cs`
- `TelemetryGuard.Analytics.Kusto/KustoValueMapping.cs`
- `TelemetryGuard.Analytics.Kusto/KustoSchemaMigrator.cs`
- `TelemetryGuard.Analytics.Kusto/KustoEventSink.cs`, `KustoLabelSink.cs`
- `TelemetryGuard.Analytics.Kusto/KustoAnalyticsQueries.cs` (three methods, or **five** if the *P2-01 interlock* applies at your branch point)
- `TelemetryGuard.Analytics.Kusto/KustoRetentionSweeper.cs`
- `TelemetryGuard.Analytics.Kusto/KustoServiceCollectionExtensions.cs`
- `TelemetryGuard.Analytics.Kusto/schema/0001_events.kql`
- `TelemetryGuard.Analytics.Kusto/README.md`
- `TelemetryGuard.Api/Analytics/AnalyticsServiceCollectionExtensions.cs` (the moved D7 switch)
- `TelemetryGuard.Api/Health/KustoHealthCheck.cs`
- `TelemetryGuard.Api/Workers/KustoRetentionService.cs`
- `tests/TelemetryGuard.Tests.Contracts/Kusto/KustoProviderFixture.cs` (+ `KustoEngineReady` wait strategy)
- `tests/TelemetryGuard.Tests.Contracts/Kusto/KustoAnalyticsContractTests.cs` (`#if TG_KUSTO_CONTRACTS`)
- `tests/TelemetryGuard.Tests.Contracts/Kusto/KustoEngineTests.cs` (`#if TG_KUSTO_CONTRACTS`)
- `tests/TelemetryGuard.Tests.Unit/Analytics/KustoIngestionMappingTests.cs`
- `tests/TelemetryGuard.Tests.Unit/Analytics/KustoEventSinkMapRowTests.cs`
- `tests/TelemetryGuard.Tests.Unit/Analytics/KustoJsonSerializationTests.cs`
- `tests/TelemetryGuard.Tests.Unit/Analytics/KustoSchemaSplitCommandsTests.cs`
- `tests/TelemetryGuard.Tests.Unit/Analytics/KustoAnalyticsQueriesKqlTests.cs`

**Modify (shared files — exactly these, exactly this way)**

- `TelemetryGuard.sln` — one project entry + its two config rows, added via `dotnet sln add` (never hand-edited).
- `TelemetryGuard.Analytics.ClickHouse/AnalyticsServiceCollectionExtensions.cs` → **renamed** to `ClickHouseServiceCollectionExtensions.cs`; switch removed, registrations preserved verbatim, method renamed `AddClickHouseAnalytics` (step 9a).
- `TelemetryGuard.Api/Program.cs` — one `using` swapped (`using TelemetryGuard.Analytics.ClickHouse;` → `using TelemetryGuard.Api.Analytics;`, line 19 today) and one `.AddCheck<ClickHouseHealthCheck>("clickhouse", tags: ["ready"])` line deleted from the `AddHealthChecks()` block (~line 235 today). **No other line changes from this task.** Locate both by content, not line number: P2-02 may have inserted a block above them in parallel.
- `TelemetryGuard.Api/TelemetryGuard.Api.csproj` — one `ProjectReference` to the Kusto project.
- `TelemetryGuard.Api/appsettings.json` — the `Analytics:Kusto` section (inert defaults), the refreshed ANA-05/P2-05 comment above `"Analytics"`, and `Analytics:Kusto → P2-05` in the API-01 section-ownership header. `Provider` stays `"ClickHouse"`.
- `TelemetryGuard.Api/appsettings.Development.json` — **unchanged**.
- `TelemetryGuard.MigrationRunner/Program.cs` — the `--kusto` block; `Migrations.cs` — `RunKustoAsync`; `TelemetryGuard.MigrationRunner.csproj` — one `ProjectReference`.
- `tests/TelemetryGuard.Tests.Contracts/TelemetryGuard.Tests.Contracts.csproj` — `Testcontainers` package, Kusto `ProjectReference`, the `KustoContracts` → `TG_KUSTO_CONTRACTS` PropertyGroup.
- `tests/TelemetryGuard.Tests.Contracts/README.md` — layout row for `Kusto/` + the rewritten "Adding a provider" section + run commands. Leave the `AnalyticsContractTests.cs` row's **test count** alone: P2-01 owns that cell and updates it when it adds its `[Fact]`s.
- `tests/TelemetryGuard.Tests.Unit/TelemetryGuard.Tests.Unit.csproj` — one `ProjectReference`.
- `tests/TelemetryGuard.Tests.Unit/Analytics/AnalyticsRegistrationTests.cs` — namespace/using swap + the Kusto cases (step 11c).
- `.github/workflows/ci.yml` — the `kusto-contracts` job only, **appended**; `build-test`, `integration` and `sdk` byte-for-byte untouched. P2-04 appends `image` and `iac` to the same file; if those are already present, add yours after them rather than reflowing the file.

**Explicitly NOT modified**

`tests/TelemetryGuard.Tests.Contracts/AnalyticsContractTests.cs`, `IAnalyticsProviderFixture.cs`, `TestEvents.cs`, `Fakes.cs`, `ClickHouse/*`; any file under `TelemetryGuard.Analytics.Abstractions/`; `TelemetryGuard.Analytics.ClickHouse/schema/*`, `ClickHouseEventSink.cs`, `ClickHouseLabelSink.cs`, `ClickHouseAnalyticsQueries.cs`, `SchemaMigrator.cs`; `TelemetryGuard.Api/Workers/RollupService.cs`; `docker-compose.yml`; `scripts/*`; `.gitignore`; `doc/spec.md`; `CLAUDE.md`.

## Acceptance criteria

- **Headline: the unmodified ANA-06 contract suite passes against the Kusto emulator.** `KustoContracts=true dotnet test tests/TelemetryGuard.Tests.Contracts --filter "provider=kusto"` runs **every** inherited `[Fact]` green — 8 in today's tree, 15 once P2-01 has landed; count them, do not hardcode the number — and **this task's own diff** touches none of `AnalyticsContractTests.cs`, `IAnalyticsProviderFixture.cs`, `TestEvents.cs`, `Fakes.cs` (verify with `git diff --stat` against your branch point, not against `main`, so a sibling's merged changes to those files are not mistaken for yours).
- **Interface completeness**: `KustoAnalyticsQueries` implements **every** member of `IAnalyticsQueries` as it exists at your branch point — no `NotImplementedException`, no stub, no `[Obsolete]` shim. `grep -c "public async Task" TelemetryGuard.Analytics.Kusto/KustoAnalyticsQueries.cs` matches the interface's method count.
- `dotnet build TelemetryGuard.sln -c Release` succeeds with **0 warnings** (`TreatWarningsAsErrors=true`), and `dotnet build tests/TelemetryGuard.Tests.Contracts/... -p:KustoContracts=true` also builds clean.
- **No provider cross-reference**: `grep -rn "ClickHouse" TelemetryGuard.Analytics.Kusto/ --include='*.cs' --include='*.csproj' --include='*.kql'` returns nothing but prose comments; `grep -rn "Kusto" TelemetryGuard.Analytics.ClickHouse/` likewise. Neither `.csproj` references the other project.
- **One switch, and it is in the composition root**: `grep -rn "Analytics:Provider" --include='*.cs' TelemetryGuard.*/ tests/` returns only `TelemetryGuard.Api/Analytics/AnalyticsServiceCollectionExtensions.cs` (plus test config keys). No other file branches on the provider name.
- **Config flip is the only change needed to switch engines**: with `Analytics:Provider=Kusto` + a reachable emulator, the Api boots, `/ready` reports the `kusto` check (and no `clickhouse` check), and `RollupService` produces the same SQL summary rows it produces on ClickHouse — with **no code change anywhere outside the switch**.
- **Column/mapping/schema alignment** is machine-checked: `KustoIngestionMappingTests` proves `KustoEventSink.ColumnNames` (71 names), the `.create-merge table tg_events` column list and the `tg_events_json_mapping` entries are the same names in the same order; ditto the 6-column label triple.
- **§7 preserved**: contract test 7 passes (`storage_age_sec` round-trips as NaN via `KustoValueMapping.RealToFloat`); contract test 4 passes (`AvgScore` is `NaN`, never `0.0`, for an unscored scope, at both report and day level); an absent `score` is stored as null, proven by `KustoJsonSerializationTests` and by contract tests 3/5.
- **D11 preserved**: contract test 6 passes — a `KustoAnalyticsQueries` bound to tenant A never surfaces tenant B's rows for the same IP and campaign id. Every KQL string in the provider contains `tenant_id == tenantId`.
- **D20 demonstrated**: `.show table tg_events policy retention` reports the 180-day backstop after `ApplyAsync`; the sweep test shows a 30-day-retention row aged 45 days gone while a 90-day-retention row of the same age survives (or, under Q5-fallback, the backstop assertion passes and the README documents the manual cloud proof).
- **Weak-guarantee discipline**: `IEventSink`/`ILabelSink` are unchanged; `grep -rn "Flush\|Drain" TelemetryGuard.Analytics.Abstractions/` returns nothing; the sink's hot path still only `TryWrite`s and returns `ValueTask.CompletedTask`.
- **Clean skip proven**: on a machine with **no** emulator, `dotnet test tests/TelemetryGuard.Tests.Contracts` (the CLAUDE.md command, no filter) is green and reports no Kusto tests at all; the `kusto-contracts` CI job is green when the image pull fails, emitting a `::warning::`, while still having compiled the runner.
- **Regression-free**: the full pre-existing suite still passes — unit tests, `tests/TelemetryGuard.Tests.Integration`, and the ClickHouse contract runner — with `Analytics:Provider` left at `"ClickHouse"`.
- **No real ADX endpoint** appears anywhere: `grep -rn "kusto.windows.net\|kusto.fabric.microsoft.com\|\.kusto\." --include='*.cs' --include='*.json' --include='*.yml' --include='*.kql' .` returns only documentation prose and the placeholder in the options XML doc.

## Testing

- **Unit (no container, runs in `build-test`)**: the five new files in `tests/TelemetryGuard.Tests.Unit/Analytics/` (step 11c) plus the updated `AnalyticsRegistrationTests`. Between them they cover the mapping layer, the JSON encoding, the KQL text invariants, the `.kql` splitter, and the whole DI switch — so **the KQL/mapping layers are fully tested with no emulator present**, which is the point.
- **Contract (emulator, opt-in)**: `KustoContracts=true dotnet test tests/TelemetryGuard.Tests.Contracts --filter "provider=kusto"`. Requires a container runtime; under podman, `~/.tg-env` already exports `DOCKER_HOST` and `TESTCONTAINERS_RYUK_DISABLED=true`. Expect a slow first run (large image, ~1–2 min engine warm-up).
- **Provider engine tests (emulator, opt-in)**: `KustoEngineTests` — schema idempotency and the D20 sweep.
- **CI**: the `kusto-contracts` job (step 12). The image-pull preflight is the CI-level clean skip; the build step is never skipped.
- **Manual, human-run only**: a smoke against a real ADX **dev** cluster, documented in `TelemetryGuard.Analytics.Kusto/README.md` as a `<remarks>`-style procedure (create a dev database, run `MigrationRunner --kusto`, set `Provider=Kusto` with `IngestMode=Queued`, drive a handful of tracker/verdict events, verify visibility and the three queries). Emulator ≠ production parity — queued ingestion, authentication and policy behavior all differ. **No CI job, test, or script may ever point at a real cluster.**

## Out of scope / guardrails

- **No generic query layer, no LINQ-over-both, no shared query strings (D7).** Hand-written KQL behind the existing intent interfaces only. Do not extract a "common analytics SQL/KQL builder", a capability-flags mechanism, or a provider-neutral query facade. The two `ColumnNames` arrays, the two sets of null-mapping helpers and the two health checks are duplicated **on purpose**; do not "DRY" them into a shared project.
- **`IEventSink`/`ILabelSink` stay eventual and batched.** No sync-flush member, no `WaitForVisibleAsync`, no return value describing storage outcome. `TelemetryGuard.Analytics.Abstractions` must come out of this task byte-identical.
- **Do not modify, skip, weaken, or add `[Fact(Skip=…)]` to the shared contract suite** — including "just the timing knobs". If a suite test fails against Kusto, the provider is wrong, not the suite.
- **`tenant_id` is never optional (D11).** No overload of any `IAnalyticsQueries` method takes a tenant; no query omits the predicate; no "admin/cross-tenant" query is added here.
- **Retention is this provider's own mechanism (D20).** Do not bend ClickHouse's TTL design onto Kusto or vice versa; do not add `retention_days` handling to the ClickHouse project; do not make the sweeper tenant-aware (the predicate is per row).
- **Fabric Eventhouse is a connection string, not a third provider (D6).** Never add a third `Analytics:Provider` value or a Fabric-specific code path.
- **No real ADX endpoints, ever** — not in tests, CI, appsettings, scripts, or compose. Shipped config keeps `Provider: "ClickHouse"` and empty Kusto connection strings, so a fresh deployment is inert. Never log a connection string.
- **Local dev stack is unchanged**: do **not** add kustainer to `docker-compose.yml` or `scripts/dev-up.sh` (a multi-GB emulator has no place in the everyday loop), and do not change the ClickHouse services.
- **Not in the request path.** The sink stays a bounded in-process channel (D12: no broker); the retention sweep is a background worker and must never touch the <50 ms scoring budget. Rules still only raise scores; nothing here reads, writes or influences a verdict, a band, or `IScorer`.
- **No Dapper/EF/SQL Server work here (D8/D9/D23).** The Kusto provider never opens a `SqlConnection`; the only bridge between the event store and the relational tier remains ANA-07's `RollupService`, which is unchanged. `TenantConnectionFactory`/RLS is not involved at this layer.
- **No server-side Python/Node (D1).** `.kql` files are data applied by C#; do not add a Kusto CLI, a shell/python migration script, or a Node tool to generate the mapping (a throwaway generator is fine — its **output** is committed, the generator is not).
- **Backlog items stay out** (from `doc/plan.md` "Backlog" and §10 "Later"): decaying IP-reputation store, per-tenant Turnstile keys, per-tenant rate-limit quotas from config, admin re-queue for failed exclusion pushes, exclusion expiry/un-exclusion policy, multi-instance verdict-finalizer claiming, SSO/user accounts, Kafka-protocol streaming, ONNX trajectory model, elastic-pool tenant isolation. Also out: **P2-02** retraining loop, **P2-03** customer portal, **P2-04** Azure deployment (this task does not provision, deploy, or bicep anything), Grafana dashboards for Kusto (`ops/grafana` untouched), and Kusto **update policies / materialized views** for aggregation — the intent queries answer from the raw table exactly as ClickHouse does.
- **P2-01 is out of scope EXCEPT for the interface obligation.** This task adds no SQL migration, no `dbo.PublisherDailySummaries`/`SiteDailySummaries` table, no repository, no `RollupService` change and no `/admin` endpoint — all of that is P2-01's, and `TelemetryGuard.Api/Workers/RollupService.cs` stays byte-identical here (that it needs no change under a second provider is the whole point of D7). What **is** in scope is implementing P2-01's two `IAnalyticsQueries` methods in KQL *if they exist on the interface at your branch point* — see *P2-01 interlock*. An interface member you refuse to implement is a compile error, not a scope boundary.
- **Never edit applied migration scripts.** `schema/0001_events.kql` is journaled once; corrections ship as `0002_*.kql`, exactly like ANA-02 → RSK-08 did on the ClickHouse side.
