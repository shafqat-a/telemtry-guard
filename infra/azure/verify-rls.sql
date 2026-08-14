-- P2-04: re-prove D11 RLS isolation on Azure SQL. Read-only except for two rows it
-- inserts under the SYSTEM sentinel and deletes at the end. Run once per environment:
--   sqlcmd -S <fqdn> -d TelemetryGuard -U tgadmin -P <password> -i infra/azure/verify-rls.sql
DECLARE @A uniqueidentifier = 'aaaa1111-0000-0000-0000-00000000000a';
DECLARE @B uniqueidentifier = 'bbbb2222-0000-0000-0000-00000000000b';

-- dbo.Tenants (migration 0001): Status tinyint (0 = active), RetentionDays int
-- (CHECK 30..180), EnforcementMode tinyint (0 = AutoEnforce, 1 = ApprovalQueue).
-- These are NUMERIC columns — never pass the string 'AutoEnforce' here.
EXEC sp_set_session_context @key = N'TenantId', @value = '00000000-0000-0000-0000-000000000001';  -- SYSTEM
INSERT dbo.Tenants (TenantId, Name, Status, RetentionDays, EnforcementMode)
SELECT @A, N'rls-probe-A', 0, 90, 0 WHERE NOT EXISTS (SELECT 1 FROM dbo.Tenants WHERE TenantId = @A);
INSERT dbo.Tenants (TenantId, Name, Status, RetentionDays, EnforcementMode)
SELECT @B, N'rls-probe-B', 0, 90, 0 WHERE NOT EXISTS (SELECT 1 FROM dbo.Tenants WHERE TenantId = @B);

-- 1. Stamped as A: sees A, never B.
EXEC sp_set_session_context @key = N'TenantId', @value = @A;
SELECT 'expect 1' AS check_name, COUNT(*) AS n FROM dbo.Tenants WHERE TenantId = @A;
SELECT 'expect 0' AS check_name, COUNT(*) AS n FROM dbo.Tenants WHERE TenantId = @B;

-- 2. Unstamped session: sees nothing at all.
EXEC sp_set_session_context @key = N'TenantId', @value = NULL;
SELECT 'expect 0' AS check_name, COUNT(*) AS n FROM dbo.Tenants WHERE TenantId IN (@A, @B);

-- cleanup
EXEC sp_set_session_context @key = N'TenantId', @value = '00000000-0000-0000-0000-000000000001';
DELETE FROM dbo.Tenants WHERE TenantId IN (@A, @B);
