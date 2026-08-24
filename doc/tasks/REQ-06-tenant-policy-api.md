---
id: REQ-06
title: Add audited per-tenant policy API
phase: 3
workstream: control
depends_on: [REQ-07]
size: M
priority: P1
---

# REQ-06: Per-tenant policy

## Objective

Add audited `GET`/`PUT /admin/policy` for per-tenant band thresholds, enforcement mode, observe-only behavior, and external-authority ownership.

## Contract

- Nullable `AllowMax`, `ChallengeMax`, and `ObserveOnly` columns fall back per field to deployment configuration.
- Validate values in 0–100 and `AllowMax < ChallengeMax` after effective-value resolution.
- A single policy provider is used by `/decide` and `VerdictFinalizer`.
- GET returns effective values and whether each came from tenant configuration or deployment fallback.
- PUT and its before/after audit record commit in one SQL transaction; actor is the API-key hash.
- Invalidate the tenant configuration cache after a successful write.

## Acceptance criteria

- All-null tenant policy behaves identically to the pre-change deployment.
- One tenant's override cannot affect another.
- Invalid combinations return field-level validation problems.
- Every successful mutation has an immutable audit record.

