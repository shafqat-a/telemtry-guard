# TelemetryGuard.Tests.Contracts — the shared analytics contract suite

This project pins the behavioral contract of `IEventSink` + `IAnalyticsQueries` +
`ILabelSink` across every analytics provider. It is the guardrail that makes the
D7 two-provider design safe.

> Every analytics provider MUST have a runner class here inheriting
> `AnalyticsContractTests<TFixture>` with a fixture backed by that provider's
> real engine (ClickHouse: Testcontainers; Kusto: Microsoft's free Kusto
> emulator container). Adding a provider without its runner — or
> skipping/weakening a contract test to make a provider pass — violates spec
> D7. The suite asserts only the WEAK shared guarantees (eventual batched
> visibility ≤ 5 s); do not tighten it with engine-specific timing that only
> one provider can meet.

## Layout

| File | Role |
| --- | --- |
| `AnalyticsContractTests.cs` | The abstract, provider-agnostic suite (8 tests). Engine names must never appear here. |
| `IAnalyticsProviderFixture.cs` | The per-provider seam: started sinks, a tenant-scoped query factory, and two narrow raw-readback hooks. Resist widening it into a query abstraction (D7). |
| `TestEvents.cs`, `Fakes.cs` | Provider-agnostic seed builder and FND-04 test doubles. |
| `ClickHouse/` | The ClickHouse runner: `ClickHouseProviderFixture` (Testcontainers, `clickhouse/clickhouse-server:24.8`, ANA-02 schema, real ANA-03 sinks) + the one-line `ClickHouseAnalyticsContractTests`. |

## What the suite pins

1. Eventual batched visibility: writes are readable within ≤ 5 s — never asserted as immediate.
2. IP velocity counts (sessions/UAs/fingerprints/flagged) over a trailing window, out-of-window rows excluded.
3. Campaign report band/day aggregation, days ascending, empty days absent.
4. **Missing ≠ zero**: a scope with no scored events has `AvgScore = NaN`, never `0.0`.
5. Flagged-source ranking (`BlockedEvents` desc), limit respected, allow-only sources excluded.
6. **Tenant isolation (D11)**: a queries instance bound to tenant A never surfaces tenant B's rows — same IP, same campaign id, only the tenant differs.
7. **NaN round-trip (§7)**: an absent SDK `Float32` survives write → store → raw readback as NaN, never coerced to 0.
8. Label sink writes become visible in the label store.

## Adding a provider (e.g. Kusto, task P2-05)

1. Implement `IAnalyticsProviderFixture` backed by the provider's real engine in
   a container (Kusto: the Microsoft Kusto emulator image), in a new
   subdirectory named after the engine.
2. Add the one-line runner:
   `public sealed class KustoAnalyticsContractTests(KustoProviderFixture fx) : AnalyticsContractTests<KustoProviderFixture>(fx);`
3. Do not modify the abstract suite. A provider that does not pass this suite
   is incomplete by definition.

## Running

Requires a container runtime (Docker/Podman) — the runner classes carry
`[Trait("requires", "docker")]` so a Docker-less environment can exclude them
with `dotnet test --filter "requires!=docker"`. CI runs the suite in the
integration job (FND-03), which has Docker available.

```
dotnet test tests/TelemetryGuard.Tests.Contracts
```
