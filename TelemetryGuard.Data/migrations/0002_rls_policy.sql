------------------------------------------------------------------------------
-- 0002_rls_policy.sql  (DAT-03)
-- Row-Level Security: PRIMARY tenant-isolation enforcement (spec D11).
--
-- RULE: every migration that creates a new tenant-scoped table MUST add both a
-- FILTER and a BLOCK predicate for that table via
--   ALTER SECURITY POLICY rls.TenantIsolationPolicy ADD ... ;
-- in the SAME migration. Resolution tables (dbo.ApiKeys, dbo.Sites) are
-- deliberately EXEMPT — they are read to establish tenant context before it exists.
--
-- SYSTEM sentinel: SESSION_CONTEXT stamped with 00000000-0000-0000-0000-000000000001
-- passes the predicate for ALL rows. Only ISystemConnectionFactory may stamp it.
------------------------------------------------------------------------------
CREATE SCHEMA rls;
GO

CREATE FUNCTION rls.fn_tenantPredicate(@TenantId uniqueidentifier)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN
    SELECT 1 AS fn_result
    WHERE @TenantId = CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier)
       OR CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier)
          = CAST('00000000-0000-0000-0000-000000000001' AS uniqueidentifier);
GO

CREATE SECURITY POLICY rls.TenantIsolationPolicy
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.Tenants,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.Tenants,
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.Campaigns,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.Campaigns
WITH (STATE = ON, SCHEMABINDING = ON);
GO
