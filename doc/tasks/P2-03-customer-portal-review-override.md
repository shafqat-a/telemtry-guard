---
id: P2-03
title: Customer portal with review/override screen (outline)
phase: 2
workstream: api
depends_on: [API-07, INT-02]
size: L
spec_refs: [D17, D19, D21, D22, D23]
detail_level: outline
---

# P2-03: Customer portal with review/override screen (outline)

> **OUTLINE ONLY — expand into a full task file before implementation.** The Blazor-vs-SPA framework decision is deliberately deferred (D17) and must be made at expansion time as a numbered spec amendment (D24+).

## Objective

Ship the tenant-facing portal: dashboards over the SQL summary tables, the review/override screen for challenged/blocked events where "this was a real customer" simultaneously whitelists the source AND writes a negative training label (D19 — the override loop IS the labeling loop), the enforcement approval queue UI (INT-02's endpoints), and per-site integration-level display so tenants understand their detection strength (D22).

## Spec context (self-contained)

- D17: customer-facing portal is deferred-until-productization; front-end is unavoidably JS/TS (same exception class as the D2 SDK — still no server-side Python/Node); candidates are ASP.NET Core + Blazor or a static SPA against the API.
- D19: every challenged/blocked event visible for review; marking "real customer" reverses the action (whitelist via API-07's manual whitelist API) *and* writes a `LabelEvent(Legit, ReviewScreen)` negative label (ANA-01 `ILabelSink`). Until this ships, Grafana + whitelist API are the override path — the portal replaces that stopgap.
- D21: ApprovalQueue tenants approve/reject exclusion pushes — the portal front-ends INT-02's `/admin/enforcement` endpoints (pending list, batch approve/reject; `unsupported`/`failed` visibility).
- D22: dashboards must display each site's integration level (`js` vs `pixel` from `dbo.Sites.IntegrationMode`, plus observed `has_js_beacon` rates) so tenants understand degraded detection on pixel-only sites.
- **D23 (hard)**: the portal reads ONLY SQL Server summary/breakdown tables (RLS-protected) — it never queries ClickHouse directly, and never through a new "generic" query path (D7).
- §11.5: if tenant demand appears early, the review screen alone may advance into phase 1.5 — keep it separable.

## Prerequisites

API-07 (admin + whitelist API, admin-scope auth pattern, API-key handling), INT-02 (enforcement endpoints), DAT-06/ANA-07 summary tables the read side will serve, ANA-01 `ILabelSink`. Read those files for exact endpoint shapes and table names.

## Implementation steps (outline)

1. Decide framework (Blazor Server/WASM vs static SPA + minimal-API BFF) — record as spec amendment; drives project layout (`TelemetryGuard.Portal`).
2. Portal auth: tenant users (new `dbo.Users` + sessions) vs API-key-only start — open decision; must resolve to a `TenantId` and flow through the standard `ITenantContext` path (D11; never a tenant picker over unscoped data).
3. Read-side endpoints over summary tables (verdict summaries, campaign breakdowns, site integration levels) — new intent-named repository methods on tenant-stamped connections.
4. Review screen: paged challenged/blocked verdict summaries; "mark real customer" action → API-07 whitelist call + `ILabelSink` negative label + audit; visibly idempotent.
5. Enforcement queue screen: INT-02 endpoints (pending/approve/reject, unsupported/failed views with the INT-04 Meta honesty message shown verbatim).
6. Integration-level widget per site (D22) + "what pixel mode cannot see" explainer.

## Files to create or modify (outline)

- New portal project + solution entry, portal read endpoints/repositories in Api/Data, migration(s) for portal users/sessions (if chosen), e2e tests.

## Acceptance criteria (outline)

- Portal renders solely from SQL summaries (assert zero ClickHouse connections from portal paths); mark-real-customer produces BOTH the whitelist entry and the negative label atomically-enough (documented ordering/failure semantics); cross-tenant isolation proven at the portal layer (tenant A cannot fetch B's summaries); integration level visible per site.

## Testing (outline)

Playwright e2e against a seeded stack (compose), endpoint auth matrix, RLS-backed isolation tests, label-write verification via a capturing sink.

## Out of scope / guardrails

- **Never read ClickHouse from the portal** (D23) and no generic cross-engine query layer (D7).
- Overrides whitelist + label; they do NOT lower scores or rewrite verdict history (rules only raise scores; verdicts are immutable records).
- Challenge band remains automatic (D21) — no portal control may disable challenges per-event.
- Dapper on tenant-stamped connections, RLS primary, explicit TenantId in WHERE, tenant_id never optional (D9/D11). No server-side Python/Node — SPA build tooling stays build-time only (D1/D2 exception class).

## Open decisions (resolve when expanding)

1. Blazor vs SPA (and Server vs WASM if Blazor) — the deferred D17 decision.
2. Portal identity: username/password + sessions, external IdP, or magic links; relation to `dbo.ApiKeys`.
3. Whether the review screen advances to 1.5 (spec §11.5) — if yes, split it out as its own task.
4. Hosting/domain and CSP for the portal vs the tracking domains.
5. Whitelist-then-label failure semantics (outbox? best-effort with retry?).
