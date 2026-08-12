---
id: P2-01
title: Publisher aggregates (outline)
phase: 2
workstream: analytics
depends_on: [ANA-07]
size: M
spec_refs: [D7, D23, "§7 T2 publisher aggregates", "§10 Phase 2"]
detail_level: outline
---

# P2-01: Publisher aggregates (outline)

> **OUTLINE ONLY — expand into a full task file before implementation.** Scope, dependencies, and open decisions below; no code should be written from this page alone.

## Objective

Compute per-placement/per-publisher fraud ratios from ClickHouse event history (e.g. "this Display-network placement produced 84% block-band verdicts over 30 days"), materialize them into SQL Server summary tables on the existing rollup schedule, and feed them back as (a) T2 model features and (b) tenant-facing reports. Spec §7 lists "publisher aggregates (Phase 2)" as a T2 feature class.

## Spec context (self-contained)

- D23: aggregates live in SQL Server; raw events stay in ClickHouse; portals/APIs read only SQL summaries.
- D7: new analytics reads = new intent-named methods on `IAnalyticsQueries` implemented per provider in native dialect — never a generic query layer.
- §7: publisher aggregates are T2 (strong model feature, never a rule); missing aggregate (new/low-volume placement) = NaN, not 0.
- D3: scoring budget <50 ms — feature lookup at score time must hit SQL/Redis/in-memory, never ClickHouse.
- RSK-01: adding features to `FraudFeatureVector` requires bumping `FeatureSetVersion` (1 → 2) — coordinate with the training lineage (RSK-08/P2-02).

## Prerequisites

ANA-07's rollup worker (ClickHouse → SQL summaries, watermark tables from DAT-06, SYSTEM-sentinel per-tenant connection pattern from DAT-03) is the host for the new aggregation step. Placement identity must exist on events (see open decisions).

## Implementation steps (outline)

1. Extend `IAnalyticsQueries` with e.g. `GetPlacementFraudStatsAsync(DateRange, CancellationToken)` (intent-named; ClickHouse SQL impl in ANA-04's project; Kusto later per P2-05).
2. Migration: `dbo.PublisherDailySummary` (TenantId-leading PK, Placement, Day, TotalEvents, Blocked, Challenged, AvgScore, ...) + RLS predicates per the DAT-03 rule.
3. Extend ANA-07's rollup cycle to materialize placement aggregates behind the same watermark.
4. Feature path: `publisher_fraud_ratio` (smoothed) added to `FraudFeatureVector` v2; RSK-04 reads it from a cached lookup (Redis `t:{tid}:pubagg:*` or in-memory, refreshed per rollup) — NaN when the placement is unknown/low-volume.
5. Report path: expose via summary-reading endpoints/portal (P2-03) and Grafana.

## Files to create or modify (outline)

- `TelemetryGuard.Analytics.Abstractions` (+ method/DTO), `TelemetryGuard.Analytics.ClickHouse` (impl), `TelemetryGuard.Data/migrations/NNNN_*.sql`, ANA-07 worker, RSK-01 contracts (v2 bump), RSK-04 extraction, contract tests (ANA-06 suite gains the new method).

## Acceptance criteria (outline)

- Contract suite covers the new query method; rollup produces correct per-placement rows (fixture events); score-time lookup adds no ClickHouse call (<50 ms preserved); unknown placement → NaN feature; RLS proof on the new table.

## Testing (outline)

Contract tests (ANA-06 pattern), rollup integration test with seeded CH events, feature-extraction unit tests incl. NaN cases.

## Out of scope / guardrails

- Never rules — publisher ratios are T2 model features only; rules only raise scores and are reserved for T1.
- Missing ≠ zero: low-volume/unseen placements are NaN with a documented smoothing/prior policy, never 0.
- No ClickHouse reads at scoring time (D3/D23); no generic cross-engine query layer (D7); Dapper + DbUp for SQL (D9/D10); tenant_id never optional; no server-side Python/Node.

## Open decisions (resolve when expanding)

1. Placement identity source: Google Ads placement report join vs a `pl=` tracker param vs referrer parsing — likely platform-dependent.
2. Smoothing/prior for low volume (Beta prior? minimum-event threshold before non-NaN?).
3. Window(s): 7d vs 30d vs both as separate features.
4. Cache medium and refresh (Redis vs in-memory per instance) and staleness bound.
5. Timing of the `FeatureSetVersion` bump relative to P2-02's retraining loop (features without retraining are dead weight).
