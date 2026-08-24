# SQL Server migrations (DbUp)

- Naming: `NNNN_description.sql` (zero-padded 4-digit ordinal, ascending, never reused,
  never edited after merge — fix mistakes with a NEW script).
- Scripts are EmbeddedResources; DbUp applies pending scripts in name order,
  one transaction per script, journaled in `dbo.SchemaVersions`.
- `GO` batch separators are supported (required around CREATE SCHEMA / CREATE FUNCTION).
- RLS RULE (see DAT-03): every migration that creates a new tenant-scoped table MUST
  `ALTER SECURITY POLICY rls.TenantIsolationPolicy` to add FILTER + BLOCK predicates for
  that table in the same migration. The resolution tables (dbo.ApiKeys, dbo.Sites) carry
  a BLOCK predicate only (0013). `tests/…/Sql/RlsPrincipalTests.cs` asserts this
  structurally against `sys.security_predicates` — a table with a `TenantId` column and
  no predicates fails the integration suite.
- PRINCIPALS (0013): the app connects as a member of `tg_app` (request path) or
  `tg_system` (background jobs); the SYSTEM sentinel is a bypass only for `tg_system` /
  db_owner. Migrations never carry passwords — users are created with
  `MigrationRunner provision create-db-user`. Never add a GRANT that lets `tg_app` ALTER
  anything.

## Number registry (append when adding a script)
| NNNN | Description | Task |
|------|-------------|------|
| 0001 | Core config schema (Tenants, ApiKeys, Sites, Campaigns) | DAT-02 |
| 0002 | RLS schema, predicate, security policy | DAT-03 |
| 0003 | Verdict summaries, flagged sources, exclusion queue, rollup watermarks | DAT-06 |
| 0004 | Whitelist entries + RLS | DAT-07 |
| 0005 | EnforcementAudit (approval-queue audit trail) + RLS | INT-02 |
| 0006 | Tenants.GoogleAdsCustomerId + GoogleAdsPushedExclusions (LRU state) + RLS | INT-03 |
| 0007 | Tenants.MetaBusinessId + Tenants.MetaBlockListId (Meta sync state) | INT-04 |
| 0008 | PublisherDailySummaries + SiteDailySummaries (publisher/site aggregates) + RLS | P2-01 |
| 0009 | ModelRegistry (model lifecycle: candidate/shadow/active/rejected/retired) — platform table, deliberately outside RLS | P2-02 |
| 0010 | Score histograms and sum-of-squares on all four daily rollups | REQ-01 |
| 0011 | Per-tenant policy overrides, ExternalAuthority, and policy audit | REQ-07 / REQ-06 |
| 0012 | LabelSubmissions + WebhookOutbox + RLS | REQ-08 / REQ-09 |
| 0013 | Principal-bound RLS bypass, tg_app / tg_system roles, BLOCK on ApiKeys/Sites | D11 hardening (build review 2026-08-24) |
