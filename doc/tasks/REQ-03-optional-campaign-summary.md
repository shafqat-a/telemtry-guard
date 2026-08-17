---
id: REQ-03
title: Make campaignId optional on summary reports
phase: 3
workstream: api
depends_on: [REQ-01, REQ-02]
size: XS
priority: P0
---

# REQ-03: Optional campaign summary scope

## Objective

Allow `GET /admin/reports/summary` without `campaignId`, returning tenant-wide daily totals including campaignless organic and SDK-only traffic.

## Contract

- Omitted `campaignId`: group all `VerdictDailySummaries` rows by day and return `campaignId: null`.
- Valid campaign GUID: preserve current behavior.
- Present but malformed value: return a field-level `400`.
- Preserve the 366-day cap and date validation.
- Aggregate all additive histogram fields in SQL.

## Acceptance criteria

- Tenant-wide daily events include `Guid.Empty` campaignless summaries.
- Campaign-scoped output remains compatible.
- Tenant isolation remains enforced by RLS and an explicit tenant predicate.

