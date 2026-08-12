---
id: P2-05
title: Kusto analytics provider (outline)
phase: later
workstream: analytics
depends_on: [ANA-06]
size: L
spec_refs: [D6, D7, D20, "§9", "§10 later"]
detail_level: outline
---

# P2-05: Kusto analytics provider (outline)

> **OUTLINE ONLY — expand into a full task file before implementation.** Build this only when a real deployment demands Kusto (D6); the seam and its guardrails already exist.

## Objective

Implement the second — and final (D6: "exactly two provider implementations ever") — analytics provider: `TelemetryGuard.Analytics.Kusto` with `KustoEventSink`, `KustoAnalyticsQueries` (and the label sink), a `/schema/*.kql` set owning its own schema AND its own retention mechanism, queued-ingestion semantics under `IEventSink`'s deliberately weak guarantee, and a CI matrix addition running the ANA-06 contract suite against the Kusto emulator container. **The provider is not done until the unmodified contract suite passes.**

## Spec context (self-contained)

- D6: Kusto covers both Azure Data Explorer and Fabric Eventhouse (same engine/SDK, different connection string) — one provider, two targets.
- D7: abstract by intent — each `IAnalyticsQueries` method is re-answered in native KQL; engine-native features are deliberately reimplemented per provider; **no shared SQL/KQL generation, no cross-engine query layer, no LINQ-over-both**. Each provider owns its schema scripts (`.kql` here vs ClickHouse `.sql`).
- D7 ingestion semantics: `IEventSink` promises only **eventual, batched** delivery precisely so Kusto's queued ingestion (visibility in seconds, not ms) can honor it. Contract tests must poll-with-timeout, never assert immediate visibility (ANA-06 already does — do not weaken further).
- D20: per-tenant retention (30–180 d, default 90; `retention_days` on every row). ClickHouse implements per-row TTL; **Kusto has no per-row TTL** — the provider must implement the same policy its own way (D20 says so explicitly). Candidate mechanisms are an open decision below.
- §9/ANA-06: the shared contract suite is what makes the second provider safe — "a new provider without passing this suite is incomplete." Kusto emulator container (`mcr.microsoft.com/azuredataexplorer/kustainer-linux`) makes it CI-runnable; note emulator limits (no managed identity, single node, some policy commands restricted) and document any suite accommodations as emulator-only.
- DI switch (D7/ANA-05) already handles `"Analytics": { "Provider": "Kusto" }` — registration only, no app-code changes anywhere else.

## Prerequisites

ANA-06's contract suite (fixture interface each provider implements — read its file for the exact fixture contract), ANA-01 abstractions (`ClickEvent` 1:1 column mapping precedent from ANA-02/ANA-03), ANA-05 DI switch, FND-03 CI workflow to extend.

## Implementation steps (outline)

1. New project `TelemetryGuard.Analytics.Kusto` (+ solution entry, refs: Abstractions + Core; packages: `Microsoft.Azure.Kusto.Data`, `Microsoft.Azure.Kusto.Ingest`).
2. `/schema/*.kql`: `click_events` table mirroring ANA-02's columns 1:1 (incl. `tenant_id` + `retention_days` + `timestamp`), ingestion mapping (JSON/CSV), ingestion batching policy tuned toward the low-latency end; label table likewise. Extend the DAT-01 runner with `--kusto` script application.
3. `KustoEventSink`: queued ingestion via `IKustoQueuedIngestClient` (same non-blocking channel/batch shape as ANA-03); `KustoAnalyticsQueries`: each method in hand-written KQL, tenant injected from `ITenantContext` into every query — never optional.
4. Retention mechanism (the provider-owned D20 implementation — open decision): scheduled purge/soft-delete predicate on `timestamp + retention_days`, vs materialized-view-plus-table-retention layering. Document chosen trade-offs in the provider README.
5. Contract-test wiring: Kusto fixture (Testcontainers generic container for the emulator) added to ANA-06's per-provider matrix — the suite itself is NOT modified to accommodate Kusto (only timeout knobs the suite already exposes).
6. CI: FND-03 workflow gains a `kusto-contract-tests` job (emulator container service); provider switch smoke test (`Provider=Kusto` boots, `/ready` strategy for CH health check — decide how health checks behave when CH is absent).

## Files to create or modify (outline)

- `TelemetryGuard.Analytics.Kusto/**` (+ `/schema/*.kql`), `TelemetryGuard.sln`, migration-runner `--kusto` mode, ANA-05 registration case (already present — verify), `tests/TelemetryGuard.Tests.Contracts` fixture, `.github/workflows/*` matrix.

## Acceptance criteria (outline)

- **The unmodified ANA-06 contract suite passes against the Kusto emulator in CI** — the headline criterion; retention policy demonstrably applied per `retention_days` (emulator-compatible proof or documented manual proof for cloud-only mechanisms); `Provider` config flip is the only change needed to switch engines; zero references from Kusto project into ClickHouse project or vice versa.

## Testing (outline)

Contract suite via emulator (CI); KQL query unit checks against recorded emulator responses where the suite's coverage is thin; manual smoke against a real ADX dev cluster documented in the provider README (emulator ≠ production parity).

## Out of scope / guardrails

- **No generic query layer / LINQ-over-both / shared query strings** (D7) — hand-written KQL only, behind the existing intent interfaces; lowest-common-denominator pressure is accepted, not "solved".
- `IEventSink` stays eventual/batched — do not add sync-flush members to the abstraction to make Kusto look faster (D7).
- `tenant_id` first-class in every table and every query, never optional (D11); missing-≠-zero null semantics preserved through the type mapping (NaN floats, nullable bools — verify Kusto `real`/`bool` null round-trips in the suite).
- Retention is this provider's own mechanism (D20) — do not bend ClickHouse's TTL design onto Kusto or vice versa.
- No server-side Python/Node (D1); no EF (D9); Fabric Eventhouse = connection string, not a third provider.

## Open decisions (resolve when expanding)

1. Retention mechanism (scheduled purge vs soft-delete predicate vs update-policy layering) and its emulator testability.
2. Ingestion format (JSON vs CSV mapping) and batching-policy values vs contract-suite timeouts.
3. Health-check semantics when Kusto is active (replace the ClickHouse `/ready` check per provider?).
4. Streaming ingestion (management-enabled) vs queued-only for lower latency — cost/complexity call.
5. CI runtime budget: emulator startup is slow — nightly matrix vs per-PR gating.
