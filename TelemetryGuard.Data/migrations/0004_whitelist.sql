------------------------------------------------------------------------------
-- 0004_whitelist.sql  (DAT-07)
-- Tenant whitelists (D19). Source of truth for the Redis cache set
-- t:{tenantId}:wl:{sourceType} read by scoring (RSK-07).
------------------------------------------------------------------------------
CREATE TABLE dbo.WhitelistEntries
(
    TenantId   uniqueidentifier NOT NULL,
    Id         bigint IDENTITY(1,1) NOT NULL,
    SourceType varchar(16)      NOT NULL,   -- 'ip' | 'device_id' | 'fingerprint'
    Value      varchar(256)     NOT NULL,   -- the IP / device id / fingerprint hash
    Reason     nvarchar(400)    NULL,
    Source     varchar(16)      NOT NULL CONSTRAINT DF_WL_Source DEFAULT ('manual'), -- 'manual' | 'review_screen'
    CreatedBy  nvarchar(200)    NULL,       -- operator/user identifier from API-07
    CreatedUtc datetime2(3)     NOT NULL CONSTRAINT DF_WL_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    ExpiresUtc datetime2(3)     NULL,       -- NULL = never expires
    CONSTRAINT PK_WhitelistEntries PRIMARY KEY CLUSTERED (TenantId, Id),
    CONSTRAINT UQ_WhitelistEntries UNIQUE NONCLUSTERED (TenantId, SourceType, Value),
    CONSTRAINT CK_WL_SourceType CHECK (SourceType IN ('ip', 'device_id', 'fingerprint')),
    CONSTRAINT CK_WL_Source CHECK (Source IN ('manual', 'review_screen'))
);
GO

-- D11 RULE: new tenant tables join the RLS policy in their own migration.
ALTER SECURITY POLICY rls.TenantIsolationPolicy
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.WhitelistEntries,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.WhitelistEntries;
GO
