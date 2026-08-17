------------------------------------------------------------------------------
-- 0011_tenant_policy.sql (REQ-07 foundation + REQ-06 schema)
-- NULL policy overrides inherit deployment configuration. ExternalAuthority is
-- non-null and defaults off so existing tenants retain their current behavior.
------------------------------------------------------------------------------

ALTER TABLE dbo.Tenants ADD
    AllowMax          tinyint      NULL,
    ChallengeMax      tinyint      NULL,
    ObserveOnly       bit          NULL,
    ExternalAuthority bit          NOT NULL CONSTRAINT DF_Tenants_ExternalAuthority DEFAULT (0),
    PolicyUpdatedUtc  datetime2(3) NULL;
GO

ALTER TABLE dbo.Tenants ADD
    CONSTRAINT CK_Tenants_AllowMax CHECK (AllowMax IS NULL OR AllowMax BETWEEN 0 AND 100),
    CONSTRAINT CK_Tenants_ChallengeMax CHECK (ChallengeMax IS NULL OR ChallengeMax BETWEEN 0 AND 100),
    CONSTRAINT CK_Tenants_PolicyBands CHECK
        (AllowMax IS NULL OR ChallengeMax IS NULL OR AllowMax < ChallengeMax);
GO

-- REQ-06 will write this table transactionally with the tenant policy update.
-- It is created with the columns now because DbUp migrations are immutable once
-- applied and REQ-07/REQ-06 deliberately share this schema boundary.
CREATE TABLE dbo.TenantPolicyAudit
(
    TenantId     uniqueidentifier NOT NULL,
    AuditId      uniqueidentifier NOT NULL,
    ActorKeyHash binary(32)       NULL,
    OldPolicy    nvarchar(2000)   NOT NULL,
    NewPolicy    nvarchar(2000)   NOT NULL,
    CreatedUtc   datetime2(3)     NOT NULL CONSTRAINT DF_TPA_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_TenantPolicyAudit PRIMARY KEY CLUSTERED (TenantId, AuditId),
    CONSTRAINT CK_TPA_OldPolicyJson CHECK (ISJSON(OldPolicy) = 1),
    CONSTRAINT CK_TPA_NewPolicyJson CHECK (ISJSON(NewPolicy) = 1)
);
GO

ALTER SECURITY POLICY rls.TenantIsolationPolicy
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.TenantPolicyAudit,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.TenantPolicyAudit;
GO

