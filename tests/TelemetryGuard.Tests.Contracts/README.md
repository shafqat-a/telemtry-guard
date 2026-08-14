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
| `AnalyticsContractTests.cs` | The abstract, provider-agnostic suite (15 tests). Engine names must never appear here. |
| `IAnalyticsProviderFixture.cs` | The per-provider seam: started sinks, a tenant-scoped query factory, and two narrow raw-readback hooks. Resist widening it into a query abstraction (D7). |
| `TestEvents.cs`, `Fakes.cs` | Provider-agnostic seed builder and FND-04 test doubles. |
| `ClickHouse/` | The ClickHouse runner: `ClickHouseProviderFixture` (Testcontainers, `clickhouse/clickhouse-server:24.8`, ANA-02 schema, real ANA-03 sinks) + the one-line `ClickHouseAnalyticsContractTests`. |
| `Kusto/` | The Kusto runner (P2-05): `KustoProviderFixture` (Testcontainers, `mcr.microsoft.com/azuredataexplorer/kustainer-linux`, P2-05 schema, real KustoEventSink/KustoLabelSink) — **always compiled** — plus `KustoAnalyticsContractTests` and `KustoEngineTests` (schema idempotency, D20 sweep), both gated behind the `KustoContracts=true` opt-in (see "Running" below). |

## What the suite pins

1. Eventual batched visibility: writes are readable within ≤ 5 s — never asserted as immediate.
2. IP velocity counts (sessions/UAs/fingerprints/flagged) over a trailing window, out-of-window rows excluded.
3. Campaign report band/day aggregation, days ascending, empty days absent.
4. **Missing ≠ zero**: a scope with no scored events has `AvgScore = NaN`, never `0.0`.
5. Flagged-source ranking (`BlockedEvents` desc), limit respected, allow-only sources excluded.
6. **Tenant isolation (D11)**: a queries instance bound to tenant A never surfaces tenant B's rows — same IP, same campaign id, only the tenant differs.
7. **NaN round-trip (§7)**: an absent SDK `Float32` survives write → store → raw readback as NaN, never coerced to 0.
8. Label sink writes become visible in the label store.

## Both providers are now implemented (D6: exactly two, ever)

ClickHouse (ANA-02..06) and Kusto (P2-05) both have a fixture + runner here —
D6 fixes the provider count at two forever, so this section is now closed, not
a template for a third provider. The Kusto runner's shape, for reference:

1. `IAnalyticsProviderFixture` backed by the provider's real engine in a
   container (Kusto: the emulator image, `Kusto/KustoProviderFixture.cs`),
   in a subdirectory named after the engine. **Always compiled** — unlike the
   runner class below it, so it can never rot silently even on a machine that
   never opts into the emulator.
2. The one-line runner:
   `public sealed class KustoAnalyticsContractTests(KustoProviderFixture fx) : AnalyticsContractTests<KustoProviderFixture>(fx);`
   — gated behind `#if TG_KUSTO_CONTRACTS` (see "Running" below) because the
   ADX emulator image is multi-GB and slow to warm, and the shared suite's
   inherited `[Fact]`s cannot be dynamically skipped — "unavailable" must mean
   "not discovered", never "failed".
3. The abstract suite (`AnalyticsContractTests.cs`) is never modified by a
   provider's own task. A provider that does not pass it unmodified is
   incomplete by definition (D7).

## Running

Requires a container runtime (Docker/Podman) — the runner classes carry
`[Trait("requires", "docker")]` so a Docker-less environment can exclude them
with `dotnet test --filter "requires!=docker"`. CI runs the ClickHouse runner in
the `integration` job (FND-03) and the Kusto runner in the dedicated
`kusto-contracts` job (P2-05), both with Docker available.

```
# ClickHouse runner only (the CLAUDE.md default — stays green with no emulator,
# no filter, and no Kusto image ever pulled):
dotnet test tests/TelemetryGuard.Tests.Contracts

# Both runners (requires a container runtime that can pull/run the
# multi-GB Kusto emulator image; first run is slow — ~1-2 min engine warm-up):
KustoContracts=true dotnet test tests/TelemetryGuard.Tests.Contracts

# Kusto runner only:
KustoContracts=true dotnet test tests/TelemetryGuard.Tests.Contracts --filter "provider=kusto"
```
