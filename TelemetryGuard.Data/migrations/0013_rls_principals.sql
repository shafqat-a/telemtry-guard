------------------------------------------------------------------------------
-- 0013_rls_principals.sql  (D11 hardening — principal-bound RLS bypass)
--
-- Before this script the SYSTEM sentinel ('00000000-0000-0000-0000-000000000001')
-- in SESSION_CONTEXT was enough to see every tenant's rows: anything that could run
-- sp_set_session_context on the application login had cross-tenant read/write, and
-- the only thing stopping the request path was a C# `if`. That is exactly the
-- "isolation depends on developer discipline" the spec forbids (goal #4, D11).
--
-- After this script:
--   * the sentinel bypass is honoured ONLY for members of the `tg_system` role (or a
--     db_owner — the migrator/DBA), so a request-path connection that stamps it sees
--     nothing;
--   * two least-privilege roles exist: `tg_app` (request path) and `tg_system`
--     (background jobs, training). Both get DML on dbo and nothing else — they cannot
--     alter tables, the rls schema, or any security policy;
--   * the resolution tables dbo.ApiKeys / dbo.Sites keep their FILTER exemption
--     (resolution happens before a tenant context exists) but gain a BLOCK predicate:
--     writes must carry the stamped tenant's id, so no tenant connection can mint a
--     key or a site for another tenant.
--
-- Database USERS are not created here — a migration must never carry a password.
-- Create them once per environment with
--   MigrationRunner provision create-db-user --name tg_app    --role tg_app    --password-env TG_APP_DB_PASSWORD
--   MigrationRunner provision create-db-user --name tg_system --role tg_system --password-env TG_SYSTEM_DB_PASSWORD
-- and point ConnectionStrings:Main at tg_app and ConnectionStrings:System at tg_system.
--
-- RULE (unchanged): every migration that creates a new tenant-scoped table MUST
--   ALTER SECURITY POLICY rls.TenantIsolationPolicy ADD FILTER ... , ADD BLOCK ...
-- in the same migration. tests/…/Sql/RlsPrincipalTests.cs asserts this structurally.
------------------------------------------------------------------------------

-- 1. Roles (idempotent — re-runnable by hand if a script is ever replayed).
IF DATABASE_PRINCIPAL_ID(N'tg_app') IS NULL
    CREATE ROLE tg_app;
IF DATABASE_PRINCIPAL_ID(N'tg_system') IS NULL
    CREATE ROLE tg_system;
GO

-- 2. Least privilege: DML on dbo only. Schema-level grants cover tables added by
--    later migrations. No ALTER anywhere, no touching the RLS objects, no writing the
--    DbUp journal.
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::dbo TO tg_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::dbo TO tg_system;
GRANT SELECT ON rls.fn_tenantPredicate TO tg_app;
GRANT SELECT ON rls.fn_tenantPredicate TO tg_system;
DENY ALTER ON SCHEMA::dbo TO tg_app;
DENY ALTER ON SCHEMA::dbo TO tg_system;
DENY ALTER ON SCHEMA::rls TO tg_app;
DENY ALTER ON SCHEMA::rls TO tg_system;
DENY ALTER ANY SECURITY POLICY TO tg_app;
DENY ALTER ANY SECURITY POLICY TO tg_system;
DENY INSERT, UPDATE, DELETE ON dbo.SchemaVersions TO tg_app;
DENY INSERT, UPDATE, DELETE ON dbo.SchemaVersions TO tg_system;
GO

-- 3. Rebuild the predicate. It is schema-bound and referenced by the policy, so the
--    policy is dropped and recreated around the ALTER FUNCTION (same transaction).
DROP SECURITY POLICY rls.TenantIsolationPolicy;
GO

ALTER FUNCTION rls.fn_tenantPredicate(@TenantId uniqueidentifier)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN
    SELECT 1 AS fn_result
    WHERE @TenantId = CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier)
       OR (
            CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier)
                = CAST('00000000-0000-0000-0000-000000000001' AS uniqueidentifier)
            -- The sentinel is only a bypass for principals allowed to hold it: the
            -- background-job role, or a db_owner (migrator, DBA, test harness).
            -- The request-path role (tg_app) stamping it sees zero rows.
            AND (IS_MEMBER(N'tg_system') = 1 OR IS_MEMBER(N'db_owner') = 1)
          );
GO

-- 4. Recreate the policy: FILTER + BLOCK on every tenant table (0002–0012), plus
--    BLOCK-only on the two resolution tables.
CREATE SECURITY POLICY rls.TenantIsolationPolicy
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.Tenants,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.Tenants,
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.Campaigns,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.Campaigns,
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.VerdictDailySummaries,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.VerdictDailySummaries,
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.FlaggedSourcesDaily,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.FlaggedSourcesDaily,
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.ExclusionQueue,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.ExclusionQueue,
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.RollupWatermarks,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.RollupWatermarks,
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.WhitelistEntries,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.WhitelistEntries,
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.EnforcementAudit,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.EnforcementAudit,
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.GoogleAdsPushedExclusions,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.GoogleAdsPushedExclusions,
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.PublisherDailySummaries,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.PublisherDailySummaries,
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.SiteDailySummaries,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.SiteDailySummaries,
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.TenantPolicyAudit,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.TenantPolicyAudit,
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.LabelSubmissions,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.LabelSubmissions,
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.WebhookOutbox,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.WebhookOutbox,
    -- Resolution tables: readable before a tenant context exists (no FILTER), but a
    -- write must match the stamped tenant (BLOCK). Provisioning stamps per tenant.
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.ApiKeys,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.Sites
WITH (STATE = ON, SCHEMABINDING = ON);
GO
