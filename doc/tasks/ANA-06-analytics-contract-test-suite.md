---
id: ANA-06
title: Analytics contract test suite
phase: 1
workstream: analytics
depends_on: [ANA-03, ANA-04]
size: M
spec_refs: [D7, "section 9", "section 7 (null semantics)", D11]
detail_level: full
---

# ANA-06: Analytics contract test suite

## Objective

Create the shared, provider-agnostic xUnit contract suite in `tests/TelemetryGuard.Tests.Contracts` that pins the behavioral contract of `IEventSink` + `IAnalyticsQueries` + `ILabelSink`, plus its first concrete runner: the ClickHouse provider via Testcontainers. This suite is the guardrail that makes the D7 two-provider design safe — **the future Kusto provider (task P2-05) MUST run by inheriting this exact base class against the Kusto emulator container; a provider that does not pass this suite is incomplete by definition.**

## Spec context (self-contained)

- **D7 accepted cost #1:** once two providers exist, every report touches two implementations. Mitigation is THIS shared contract-test suite run against both (ClickHouse via Testcontainers now; Kusto via Microsoft's free emulator container later). "Without contract tests the second provider silently rots."
- **D7 weak guarantee:** `IEventSink` promises *eventual, batched* delivery. The contract therefore asserts visibility within a generous window (≤ 5 s), never immediate read-after-write.
- **D11:** queries are tenant-scoped via `ITenantContext`; the contract must PROVE isolation — a queries instance bound to tenant A must never surface tenant B's rows.
- **§7 null semantics:** absent SDK numerics are stored and surfaced as NaN, not zero — the contract asserts a Float32 NaN survives the full write→store→aggregate path.
- Band strings `allow|challenge|block`; block band = score ≥ 71; verdict rows have `Kind = Verdict`; `DateRange` is half-open UTC.

## Prerequisites

- ANA-01 contracts (`IEventSink`, `IAnalyticsQueries`, `ILabelSink`, `ClickEvent`, `LabelEvent`, DTOs — see `doc/tasks/ANA-01-analytics-abstractions.md`).
- ANA-02 `SchemaMigrator` + `tg_events`/`tg_labels`; ANA-03 `ClickHouseEventSink`/`ClickHouseLabelSink` (+ `ClickHouseAnalyticsOptions`); ANA-04 `ClickHouseAnalyticsQueries`.
- FND-04 `TenantId`, `ITenantContext`, `IClock`.
- The `tests/TelemetryGuard.Tests.Contracts` project exists from FND-01's scaffold (xunit, in the solution, with project references to the analytics projects and `TelemetryGuard.Core`).

## Implementation steps

1. **Project** — verify `tests/TelemetryGuard.Tests.Contracts/TelemetryGuard.Tests.Contracts.csproj` has: xunit + `xunit.runner.visualstudio` + `Microsoft.NET.Test.Sdk` (FND-01), and add if missing: `Testcontainers.ClickHouse` (3.10.0, same pin as the Integration project), `Microsoft.Extensions.Options`, plus project references to `TelemetryGuard.Analytics.Abstractions`, `TelemetryGuard.Analytics.ClickHouse`, `TelemetryGuard.Core`.

2. **Test doubles** — `Fakes.cs` (FND-04 contracts: `ITenantContext` is in `TelemetryGuard.Core.Tenancy` with `TenantId`/`SiteKey`/`IsResolved`; `IClock` is in `TelemetryGuard.Core.Time` with `DateTimeOffset UtcNow`):
   ```csharp
   using TelemetryGuard.Core.Tenancy;
   using TelemetryGuard.Core.Time;

   public sealed class FixedTenantContext(TenantId tenantId, string? siteKey = null) : ITenantContext
   {
       public TenantId TenantId { get; } = tenantId;
       public string? SiteKey { get; } = siteKey;
       public bool IsResolved => true;
   }

   public sealed class FixedClock(DateTimeOffset utcNow) : IClock
   {
       public DateTimeOffset UtcNow { get; set; } = utcNow;
   }
   ```

3. **Provider fixture seam** — `IAnalyticsProviderFixture.cs`:
   ```csharp
   using TelemetryGuard.Analytics.Abstractions;
   using TelemetryGuard.Core.Tenancy;

   namespace TelemetryGuard.Tests.Contracts;

   /// <summary>
   /// One implementation per analytics provider. The Kusto provider (P2-05) MUST
   /// supply an implementation backed by the Kusto emulator container and run the
   /// same AnalyticsContractTests subclass — that is the D7 guardrail.
   /// </summary>
   public interface IAnalyticsProviderFixture : IAsyncLifetime  // xunit calls InitializeAsync/DisposeAsync
   {
       IEventSink Sink { get; }        // started and flushing
       ILabelSink LabelSink { get; }   // started and flushing
       IAnalyticsQueries CreateQueries(TenantId tenantId, DateTime? utcNow = null);
       /// <summary>Provider-native raw readback used only by the NaN round-trip test.</summary>
       Task<float> ReadStorageAgeSecAsync(TenantId tenantId, string sessionId);
       /// <summary>Provider-native raw count of label rows for a tenant.</summary>
       Task<long> CountLabelsAsync(TenantId tenantId);
   }
   ```

4. **Abstract contract suite** — `AnalyticsContractTests.cs`. Generic over the fixture so each provider is a one-line subclass:
   ```csharp
   public abstract class AnalyticsContractTests<TFixture>(TFixture fx) : IClassFixture<TFixture>
       where TFixture : class, IAnalyticsProviderFixture
   {
       protected static readonly TenantId TenantA = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"));
       protected static readonly TenantId TenantB = new(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"));
   ```
   Include a `TestEvents` builder (static helper in the project) producing a valid `ClickEvent` with overridable tenant/ip/session/campaign/kind/band/score/timestamp, defaults: `SiteKey="site-1"`, `HasJsBeacon=false`, `RetentionDays=90`, `TimestampUtc=DateTime.UtcNow`. And an async poll helper:
   ```csharp
   protected static async Task<T> EventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> done,
       TimeSpan? timeout = null)
   {
       var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
       while (true)
       {
           var v = await read();
           if (done(v)) return v;
           if (DateTime.UtcNow > deadline) return v; // final value; caller's assert produces the failure
           await Task.Delay(250);
       }
   }
   ```
   **Required test methods** (all `[Fact] public async Task`; each uses unique IPs/sessions/campaigns per run — suffix with `Guid.NewGuid():N` — so the shared container never cross-contaminates tests):
   1. `BatchWrite_IsEventuallyReadable_Within5s` — write 25 events for TenantA, same fresh IP, via `fx.Sink.WriteBatchAsync`; poll `CreateQueries(TenantA).GetIpVelocityAsync(ip, 1h)` until `ClickCount == 25` within 5 s; assert final value.
   2. `IpVelocity_ComputesCountsOverSeededEvents` — seed one IP: 6 events across 3 sessions, 2 distinct UAs, 2 distinct fingerprints, 2 verdicts with score 90 (band block) → assert `ClickCount`(non-verdict kinds counted per ANA-04 semantics: assert against what you seeded), `DistinctSessions == 3`, `DistinctUserAgents == 2`, `DistinctFingerprints == 2`, `FlaggedCount == 2`. Seed events OUTSIDE the window (timestamp now−2h, window 1h) and assert they are excluded.
   3. `CampaignReport_AggregatesBandsAndDays` — one campaign, 3 UTC days: day1 = 2 allow + 1 block verdicts (+1 tracker), day2 = 1 challenge, day3 = nothing; range covering all days → `Days.Count == 2` (empty day absent), totals `Allowed==2, Challenged==1, Blocked==1`, `TotalEvents` includes the tracker row, each day's `ScoreSum` equals the sum of seeded scores, `AvgScore` equals the seeded weighted mean (tolerance 0.01), `Days` ordered ascending.
   4. `CampaignReport_NoScoredEvents_AvgScoreIsNaN` — campaign with only tracker/pixel rows → `ScoredEvents == 0` and `double.IsNaN(report.AvgScore)`; counts zero; **never 0.0 for the average**.
   5. `TopFlaggedSources_RanksAndLimits` — 3 IPs with 5/3/1 block verdicts + 1 IP with only allow verdicts; `limit=2` → exactly 2 results, ordered by `BlockedEvents` desc (5-then-3), allow-only IP absent, `SourceType == "ip"`, and an IP with only challenge verdicts counts in `FlaggedEvents` but has `BlockedEvents == 0`.
   6. `TenantIsolation_TenantAQueries_NeverSeeTenantB` — seed identical-shaped data for TenantA and TenantB (same IP, same campaign id); queries created for TenantA must return ONLY TenantA counts for all three methods; then TenantB queries see only TenantB's. This is the D11 proof at the analytics layer.
   7. `SdkNumerics_AbsentValues_RoundTripAsNaN` — write a pixel-mode event (all SDK floats left at NaN defaults) for a fresh session; `await fx.ReadStorageAgeSecAsync(TenantA, sessionId)` → `float.IsNaN(...)` is true.
   8. `LabelSink_WritesAreEventuallyStored` — `fx.LabelSink.WriteAsync(new LabelEvent(TenantA, session, LabelValues.Fraud, LabelSources.T1Rule, DateTime.UtcNow), ct)`; poll `fx.CountLabelsAsync(TenantA)` until ≥ 1 within 5 s.

5. **ClickHouse fixture** — `ClickHouse/ClickHouseProviderFixture.cs`:
   ```csharp
   public sealed class ClickHouseProviderFixture : IAnalyticsProviderFixture
   {
       private readonly ClickHouseContainer _container =
           new ClickHouseBuilder().WithImage("clickhouse/clickhouse-server:24.8").Build();
       private ClickHouseEventSink? _sink;
       private ClickHouseLabelSink? _labelSink;
       private ClickHouseAnalyticsOptions? _opts;

       public async Task InitializeAsync()
       {
           await _container.StartAsync();
           var cs = _container.GetConnectionString();
           await new SchemaMigrator(cs).ApplyAsync();
           _opts = new ClickHouseAnalyticsOptions
           {
               ConnectionString = cs,
               EventMaxBatchSize = 100, EventMaxBatchAgeSeconds = 0.3,
               LabelMaxBatchSize = 10,  LabelMaxBatchAgeSeconds = 0.3
           };
           _sink = new ClickHouseEventSink(Options.Create(_opts), NullLogger<ClickHouseEventSink>.Instance);
           _labelSink = new ClickHouseLabelSink(Options.Create(_opts), NullLogger<ClickHouseLabelSink>.Instance);
           await _sink.StartAsync(CancellationToken.None);
           await _labelSink.StartAsync(CancellationToken.None);
       }

       public IEventSink Sink => _sink!;
       public ILabelSink LabelSink => _labelSink!;

       public IAnalyticsQueries CreateQueries(TenantId tenantId, DateTime? utcNow = null) =>
           new ClickHouseAnalyticsQueries(
               new FixedTenantContext(tenantId),
               Options.Create(_opts!),
               new FixedClock(utcNow ?? DateTime.UtcNow));

       public async Task<float> ReadStorageAgeSecAsync(TenantId tenantId, string sessionId)
       { /* raw ClickHouseConnection: SELECT storage_age_sec FROM tg_events
            WHERE tenant_id = {t:UUID} AND session_id = {s:String} LIMIT 1 */ }

       public async Task<long> CountLabelsAsync(TenantId tenantId)
       { /* SELECT count() FROM tg_labels WHERE tenant_id = {t:UUID} */ }

       public async Task DisposeAsync()
       {
           if (_sink is not null) await _sink.StopAsync(CancellationToken.None);
           if (_labelSink is not null) await _labelSink.StopAsync(CancellationToken.None);
           await _container.DisposeAsync();
       }
   }
   ```

6. **Concrete runner** — `ClickHouse/ClickHouseAnalyticsContractTests.cs`:
   ```csharp
   public sealed class ClickHouseAnalyticsContractTests(ClickHouseProviderFixture fx)
       : AnalyticsContractTests<ClickHouseProviderFixture>(fx);
   ```

7. **The Kusto guardrail, in writing.** Add this comment block atop `AnalyticsContractTests.cs` AND a `README.md` in the project root:
   > Every analytics provider MUST have a runner class here inheriting `AnalyticsContractTests<TFixture>` with a fixture backed by that provider's real engine (ClickHouse: Testcontainers; Kusto: Microsoft's Kusto emulator container). Adding a provider without its runner — or skipping/weakening a contract test to make a provider pass — violates spec D7. The suite asserts only the WEAK shared guarantees (eventual batched visibility ≤ 5 s); do not tighten it with engine-specific timing that only one provider can meet.

## Files to create or modify

- `tests/TelemetryGuard.Tests.Contracts/TelemetryGuard.Tests.Contracts.csproj`
- `tests/TelemetryGuard.Tests.Contracts/README.md`
- `tests/TelemetryGuard.Tests.Contracts/Fakes.cs`
- `tests/TelemetryGuard.Tests.Contracts/TestEvents.cs`
- `tests/TelemetryGuard.Tests.Contracts/IAnalyticsProviderFixture.cs`
- `tests/TelemetryGuard.Tests.Contracts/AnalyticsContractTests.cs`
- `tests/TelemetryGuard.Tests.Contracts/ClickHouse/ClickHouseProviderFixture.cs`
- `tests/TelemetryGuard.Tests.Contracts/ClickHouse/ClickHouseAnalyticsContractTests.cs`
- `TelemetryGuard.sln`

## Acceptance criteria

- `dotnet test tests/TelemetryGuard.Tests.Contracts` passes with Docker available: all 8 contract tests green against the ClickHouse container.
- The suite is genuinely provider-agnostic: `grep -rn "ClickHouse" tests/TelemetryGuard.Tests.Contracts/AnalyticsContractTests.cs tests/TelemetryGuard.Tests.Contracts/IAnalyticsProviderFixture.cs TestEvents.cs Fakes.cs` returns nothing — engine names appear only under `ClickHouse/`.
- Tenant-isolation test fails (proving it bites) if the `tenant_id` predicate is removed from any ANA-04 query — verify once manually by commenting the predicate out, watching the red test, restoring it.
- NaN test fails if `MapRow` (ANA-03) coerces NaN to 0 — the assertions are on `float.IsNaN`/`double.IsNaN`, never `== 0`.
- Suite runs in CI (FND-03's pipeline runs `dotnet test` on the solution; no extra wiring needed beyond Docker availability — if CI lacks Docker, mark the runner class with a `[Trait("requires","docker")]` and note it in the README).

## Testing

This task IS the testing. Meta-checks: each test uses unique identifiers (no ordering dependencies between tests — xunit may parallelize within the class fixture's collection; if flakiness appears, add `[Collection]` to serialize); total suite runtime target < 90 s locally.

## Out of scope / guardrails

- **Do not implement Kusto** (P2-05) — this task only makes the Kusto runner a one-line inevitability.
- **No generic query layer (D7):** the fixture seam exposes provider factories and two narrow raw-readback hooks — resist expanding it into a query abstraction.
- **Weak guarantees only:** never assert immediate visibility, exact flush timing, or engine internals (parts, merges) in the shared suite; engine-specific assertions belong in ANA-02/ANA-03 integration tests.
- **Missing ≠ zero** and **tenant never optional** are contract content here — do not weaken tests 4, 6, 7 to make anything pass.
- Rules-raise-only, 50 ms scoring, Dapper/RLS constraints are out of this task's code but must not be contradicted by test helpers (e.g., `TestEvents` must not default `Score` to 0 on non-verdict rows — leave it null).
- No server-side Python/Node test tooling; xUnit/.NET only.
