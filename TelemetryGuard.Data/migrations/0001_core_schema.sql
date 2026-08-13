------------------------------------------------------------------------------
-- 0001_core_schema.sql  (DAT-02)
-- Core config tables. Tenant-scoped tables lead every PK/index with TenantId (D11).
--
-- RESOLUTION TABLES: dbo.ApiKeys and dbo.Sites are read by tenant-resolution
-- middleware BEFORE tenant context exists. They are EXEMPT from the RLS policy
-- (added in 0002 by DAT-03) and their lookup indexes intentionally do not lead
-- with TenantId. Do not add them to rls.TenantIsolationPolicy.
------------------------------------------------------------------------------

-- Status enums (documented, enforced by CHECK where noted):
--   Tenants.Status:    0=Active, 1=Suspended, 2=Closed
--   ApiKeys.Status:    0=Active, 1=Revoked
--   Campaigns.Status:  0=Active, 1=Paused, 2=Archived
--   Tenants.EnforcementMode (D21): 0=AutoEnforce, 1=ApprovalQueue

CREATE TABLE dbo.Tenants
(
    TenantId        uniqueidentifier NOT NULL,
    Name            nvarchar(200)    NOT NULL,
    Status          tinyint          NOT NULL CONSTRAINT DF_Tenants_Status DEFAULT (0),
    RetentionDays   int              NOT NULL CONSTRAINT DF_Tenants_RetentionDays DEFAULT (90),
    EnforcementMode tinyint          NOT NULL CONSTRAINT DF_Tenants_EnforcementMode DEFAULT (0),
    CreatedUtc      datetime2(3)     NOT NULL CONSTRAINT DF_Tenants_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_Tenants PRIMARY KEY CLUSTERED (TenantId),
    -- D20: raw-event retention window, per tenant, 30..180 days, default 90.
    CONSTRAINT CK_Tenants_RetentionDays CHECK (RetentionDays BETWEEN 30 AND 180),
    CONSTRAINT CK_Tenants_EnforcementMode CHECK (EnforcementMode IN (0, 1)),
    -- The SYSTEM sentinel tenant id (DAT-03) must never be registered as a real tenant.
    CONSTRAINT CK_Tenants_NotSystemSentinel CHECK (TenantId <> '00000000-0000-0000-0000-000000000001')
);
GO

-- RESOLUTION TABLE (RLS-exempt, see header). Looked up by SHA-256 hash of the
-- presented API key; the raw key is never stored.
CREATE TABLE dbo.ApiKeys
(
    KeyHash    binary(32)       NOT NULL,   -- SHA-256 of the raw API key
    TenantId   uniqueidentifier NOT NULL,
    Scopes     nvarchar(400)    NOT NULL,   -- space-separated, e.g. N'admin ingest report'
    Status     tinyint          NOT NULL CONSTRAINT DF_ApiKeys_Status DEFAULT (0),
    CreatedUtc datetime2(3)     NOT NULL CONSTRAINT DF_ApiKeys_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_ApiKeys PRIMARY KEY CLUSTERED (KeyHash),
    CONSTRAINT FK_ApiKeys_Tenants FOREIGN KEY (TenantId) REFERENCES dbo.Tenants (TenantId)
);
GO
CREATE NONCLUSTERED INDEX IX_ApiKeys_TenantId ON dbo.ApiKeys (TenantId);
GO

-- RESOLUTION TABLE (RLS-exempt, see header). Looked up by SiteKey from the
-- ?k= query parameter / JS snippet data-site-key (D22).
CREATE TABLE dbo.Sites
(
    TenantId        uniqueidentifier NOT NULL,
    SiteKey         varchar(64)      NOT NULL,
    Domain          nvarchar(253)    NOT NULL,
    IntegrationMode varchar(8)       NOT NULL CONSTRAINT DF_Sites_IntegrationMode DEFAULT ('js'),
    CreatedUtc      datetime2(3)     NOT NULL CONSTRAINT DF_Sites_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_Sites PRIMARY KEY CLUSTERED (TenantId, SiteKey),
    -- Global uniqueness: the site key alone must identify the tenant (resolution path).
    CONSTRAINT UQ_Sites_SiteKey UNIQUE NONCLUSTERED (SiteKey),
    CONSTRAINT FK_Sites_Tenants FOREIGN KEY (TenantId) REFERENCES dbo.Tenants (TenantId),
    -- D22: 'js' = full SDK snippet; 'pixel' = HTTP-only web pixel (SDK features become NaN).
    CONSTRAINT CK_Sites_IntegrationMode CHECK (IntegrationMode IN ('js', 'pixel'))
);
GO

CREATE TABLE dbo.Campaigns
(
    TenantId           uniqueidentifier NOT NULL,
    CampaignId         uniqueidentifier NOT NULL,
    Platform           varchar(16)      NOT NULL,   -- 'google' | 'meta' | 'tiktok' | 'other'
    ExternalCampaignId varchar(64)      NULL,       -- the ad platform's own campaign id
    -- OPEN-REDIRECT GUARDRAIL: LandingUrl is the ONLY source of click-tracker (/c)
    -- redirect destinations. The tracker must NEVER redirect to a URL taken from a
    -- request parameter (see API-02). NOT NULL is deliberate.
    LandingUrl         nvarchar(2048)   NOT NULL,
    GeoTargets         nvarchar(max)    NULL,       -- JSON array, e.g. N'["US","CA"]'
    Status             tinyint          NOT NULL CONSTRAINT DF_Campaigns_Status DEFAULT (0),
    CreatedUtc         datetime2(3)     NOT NULL CONSTRAINT DF_Campaigns_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    -- D11: PK leads with TenantId; (TenantId, CampaignId) is the /c hot-path seek (DAT-05).
    CONSTRAINT PK_Campaigns PRIMARY KEY CLUSTERED (TenantId, CampaignId),
    CONSTRAINT FK_Campaigns_Tenants FOREIGN KEY (TenantId) REFERENCES dbo.Tenants (TenantId),
    CONSTRAINT CK_Campaigns_Platform CHECK (Platform IN ('google', 'meta', 'tiktok', 'other')),
    CONSTRAINT CK_Campaigns_GeoTargets CHECK (GeoTargets IS NULL OR ISJSON(GeoTargets) = 1)
);
GO
