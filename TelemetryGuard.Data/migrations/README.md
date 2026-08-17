# SQL Server migrations (DbUp)

- Naming: `NNNN_description.sql` (zero-padded 4-digit ordinal, ascending, never reused,
  never edited after merge — fix mistakes with a NEW script).
- Scripts are EmbeddedResources; DbUp applies pending scripts in name order,
  one transaction per script, journaled in `dbo.SchemaVersions`.
- `GO` batch separators are supported (required around CREATE SCHEMA / CREATE FUNCTION).
- RLS RULE (see DAT-03): every migration that creates a new tenant-scoped table MUST
  `ALTER SECURITY POLICY rls.TenantIsolationPolicy` to add FILTER + BLOCK predicates for
  that table in the same migration. Only the resolution tables (dbo.ApiKeys, dbo.Sites)
  are exempt.

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
| 0010 | Score histogram (ScoreBucket00..100 + ScoreSumSq) on VerdictDailySummaries, FlaggedSourcesDaily, PublisherDailySummaries, SiteDailySummaries | REQ-01 |
