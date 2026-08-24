---
id: REQ-01
title: Add score distributions to every daily rollup
phase: 3
workstream: analytics
depends_on: [ANA-07, P2-01]
size: S
priority: P0
---

# REQ-01: Score distributions on daily rollups

## Objective

Preserve enough score evidence for downstream consumers to define their own bands. Add eleven decile counts and `ScoreSumSq` to `VerdictDailySummaries`, `FlaggedSourcesDaily`, `PublisherDailySummaries`, and `SiteDailySummaries`, and populate them during the existing rollup pass.

## Contract

- Migration: `TelemetryGuard.Data/migrations/0010_score_distribution.sql`.
- Buckets are integer counts: `ScoreBucket00` counts 0–9 through `ScoreBucket90` counting 90–99; `ScoreBucket100` counts exactly 100.
- `ScoreSumSq bigint` is `SUM(score * score)`.
- Distribution values are absolute and mergeable; rollup replay must converge.
- Extend ClickHouse and Kusto implementations through the shared analytics contracts.
- Extend the live campaign-summary increment as well as scheduled absolute upserts.
- Existing rows default to zero and the configured lookback must be re-covered after deployment.

## Acceptance criteria

- Bucket sum equals `Events` on campaign, publisher, and site rows.
- Bucket sum equals `FlaggedCount` on flagged-source rows.
- Existing band totals remain unchanged and reconcile to `Events`.
- Scores 0, 9, 10, 99, and 100 land in the correct buckets.
- Provider contract tests produce identical distributions.
- Re-running a rollup produces byte-identical values.

