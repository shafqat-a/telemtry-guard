---
id: REQ-04
title: Expose projected evidence for one session verdict
phase: 3
workstream: api
depends_on: [RSK-08, ANA-05]
size: S
priority: P1
---

# REQ-04: Session verdict evidence

## Objective

Add `GET /admin/sessions/{sid}/verdict` returning the latest verdict's score, band, action, rule hits, scorer lineage, shadow score, and projected 44-field `FraudFeatureVector`.

## Security boundary

The response is an allowlisted projection. It must never expose headers, cookies, header names, referrer, landing URL, query data, or a raw `ClickEvent`. Another tenant's session is indistinguishable from an unknown session and returns `404`.

## Acceptance criteria

- Route is protected by `AdminScopeFilter` and validates the canonical SID shape.
- Missing verdict returns `404`.
- NaN and other non-finite feature values serialize as JSON `null`, never zero.
- A response-key allowlist test fails if any unexpected field is added.
- ClickHouse and Kusto provider contracts return equivalent evidence.

