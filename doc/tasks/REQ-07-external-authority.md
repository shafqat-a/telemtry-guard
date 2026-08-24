---
id: REQ-07
title: Enforce external ownership of platform exclusions
phase: 3
workstream: control
depends_on: [INT-02, INT-03, INT-04]
size: S
priority: P1
---

# REQ-07: ExternalAuthority

## Objective

Add `ExternalAuthority` per tenant. When enabled, every block-band exclusion enters `pending`, even when `EnforcementMode` is `AutoEnforce`, and cannot reach an ad platform without `/admin/enforcement/approve`.

## Contract

- Column is `bit NOT NULL DEFAULT 0` in `0011_tenant_policy.sql`.
- The flag governs outbound platform writes only.
- It must not alter scores, bands, browser decisions, Turnstile challenges, or observe-only behavior.
- Existing tenants default to unchanged behavior.
- Interim operational action: set all current tenants to `ApprovalQueue`.

## Acceptance criteria

- `ExternalAuthority=1` plus `AutoEnforce` still produces `pending`.
- Push workers only select rows transitioned to `approved` by the approval repository.
- `ExternalAuthority=0` preserves current behavior.
- Mid-band challenge behavior is unchanged in every combination.

