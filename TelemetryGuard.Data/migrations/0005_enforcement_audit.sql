------------------------------------------------------------------------------
-- 0005_enforcement_audit.sql  (INT-02)
-- Audit trail for approval-queue decisions (spec D21/D19). Tenant-scoped: RLS
-- predicates added below per the DAT-03 rule.
--
-- NOTE: DAT-06's 0003_summaries_exclusions.sql already shipped dbo.ExclusionQueue
-- with 'rejected' in its Status CHECK, a Platform column, and an UpdatedUtc
-- column (verify: `grep CK_EQ_Status 0003_summaries_exclusions.sql`) — this
-- migration does NOT touch dbo.ExclusionQueue at all. It only adds the new
-- EnforcementAudit table.
--
-- EnforcementAudit.Action: 0=approve, 1=reject
------------------------------------------------------------------------------
CREATE TABLE dbo.EnforcementAudit
(
    TenantId         uniqueidentifier NOT NULL,
    AuditId          uniqueidentifier NOT NULL,
    ExclusionQueueId bigint           NOT NULL,   -- originating dbo.ExclusionQueue.Id
    Action           tinyint          NOT NULL,
    ActorKeyHash binary(32)       NULL,       -- SHA-256 of the X-Api-Key that acted; NULL = system
    Note         nvarchar(400)    NULL,
    CreatedUtc   datetime2(3)     NOT NULL CONSTRAINT DF_EnforcementAudit_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_EnforcementAudit PRIMARY KEY CLUSTERED (TenantId, AuditId),
    CONSTRAINT CK_EnforcementAudit_Action CHECK (Action IN (0, 1))
);
GO
CREATE NONCLUSTERED INDEX IX_EnforcementAudit_Exclusion
    ON dbo.EnforcementAudit (TenantId, ExclusionQueueId);
GO

-- D11 RULE: new tenant tables join the RLS policy in their own migration.
ALTER SECURITY POLICY rls.TenantIsolationPolicy
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.EnforcementAudit,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.EnforcementAudit;
GO
