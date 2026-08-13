------------------------------------------------------------------------------
-- 0003_summaries_exclusions.sql  (DAT-06)
-- D23 split: these are SQL Server AGGREGATE/STATE tables. Raw events live in
-- ClickHouse only. All four tables are tenant-scoped => RLS FILTER + BLOCK below.
------------------------------------------------------------------------------

-- Per-tenant/campaign/day verdict counts. Written idempotently (absolute values)
-- by the ANA-07 rollup via IVerdictSummaryRepository.UpsertDailySummaryAsync.
CREATE TABLE dbo.VerdictDailySummaries
(
    TenantId   uniqueidentifier NOT NULL,
    CampaignId uniqueidentifier NOT NULL,
    [Date]     date             NOT NULL,
    Allowed    int              NOT NULL CONSTRAINT DF_VDS_Allowed DEFAULT (0),      -- score 0-30
    Challenged int              NOT NULL CONSTRAINT DF_VDS_Challenged DEFAULT (0),   -- score 31-70
    Blocked    int              NOT NULL CONSTRAINT DF_VDS_Blocked DEFAULT (0),      -- score 71-100
    ScoreSum   bigint           NOT NULL CONSTRAINT DF_VDS_ScoreSum DEFAULT (0),     -- sum of scores (mean = ScoreSum/Events)
    Events     int              NOT NULL CONSTRAINT DF_VDS_Events DEFAULT (0),
    UpdatedUtc datetime2(3)     NOT NULL CONSTRAINT DF_VDS_UpdatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_VerdictDailySummaries PRIMARY KEY CLUSTERED (TenantId, CampaignId, [Date])
);
GO

-- Per-tenant/day flagged sources (top-N feeds for dashboards/API-07).
-- SourceType: 'ip' | 'placement' | 'device_id' | 'fingerprint'
CREATE TABLE dbo.FlaggedSourcesDaily
(
    TenantId     uniqueidentifier NOT NULL,
    [Date]       date             NOT NULL,
    SourceType   varchar(16)      NOT NULL,
    Value        varchar(256)     NOT NULL,
    FlaggedCount int              NOT NULL CONSTRAINT DF_FSD_FlaggedCount DEFAULT (0), -- challenged + blocked
    BlockedCount int              NOT NULL CONSTRAINT DF_FSD_BlockedCount DEFAULT (0),
    ScoreSum     bigint           NOT NULL CONSTRAINT DF_FSD_ScoreSum DEFAULT (0),
    UpdatedUtc   datetime2(3)     NOT NULL CONSTRAINT DF_FSD_UpdatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_FlaggedSourcesDaily PRIMARY KEY CLUSTERED (TenantId, [Date], SourceType, Value),
    CONSTRAINT CK_FSD_SourceType CHECK (SourceType IN ('ip', 'placement', 'device_id', 'fingerprint'))
);
GO

-- Exclusion-sync queue (D21). Written by API-06 (verdict finalization), consumed by
-- INT-02 (approval flow: pending -> approved | rejected) and the per-platform sync
-- workers INT-03 (Platform='google') / INT-04 (Platform='meta'): approved -> pushed | failed | unsupported.
-- AutoEnforce tenants may enqueue directly as 'approved'.
-- AUTHORITATIVE SHAPE NOTE (binding on INT-02/INT-03/INT-04, whose task files say
-- "adapt to DAT-06's actual names"): the key is (TenantId, Id bigint IDENTITY) —
-- there is NO ExclusionId uniqueidentifier — and Status is these varchar literals,
-- NOT a tinyint encoding. Platform routes each row to its sync worker; UpdatedUtc
-- and LastError are written by INT-02/03/04 on every transition/push attempt.
CREATE TABLE dbo.ExclusionQueue
(
    TenantId      uniqueidentifier NOT NULL,
    Id            bigint IDENTITY(1,1) NOT NULL,
    Platform      varchar(16)      NOT NULL,               -- 'google' | 'meta' | 'tiktok' | 'other' (from the campaign's Platform; 'other' when campaign-less — API-06)
    SourceType    varchar(16)      NOT NULL,               -- 'ip' | 'placement'
    Value         varchar(256)     NOT NULL,               -- the IP or placement id
    Reason        nvarchar(400)    NOT NULL,               -- e.g. N'score=87 rule=ip_datacenter_asn'
    Status        varchar(16)      NOT NULL CONSTRAINT DF_EQ_Status DEFAULT ('pending'),
    CampaignScope uniqueidentifier NULL,                   -- NULL = tenant-wide, else a CampaignId
    CreatedUtc    datetime2(3)     NOT NULL CONSTRAINT DF_EQ_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    UpdatedUtc    datetime2(3)     NULL,                   -- set by INT-02 transitions and INT-03/04 pushes
    PushedUtc     datetime2(3)     NULL,
    LastError     nvarchar(2000)   NULL,                   -- push failure detail (INT-03/INT-04)
    CONSTRAINT PK_ExclusionQueue PRIMARY KEY CLUSTERED (TenantId, Id),
    CONSTRAINT CK_EQ_Platform CHECK (Platform IN ('google', 'meta', 'tiktok', 'other')),
    CONSTRAINT CK_EQ_SourceType CHECK (SourceType IN ('ip', 'placement')),
    CONSTRAINT CK_EQ_Status CHECK (Status IN ('pending', 'approved', 'rejected', 'pushed', 'failed', 'unsupported'))
);
GO
CREATE NONCLUSTERED INDEX IX_ExclusionQueue_TenantPlatformStatus
    ON dbo.ExclusionQueue (TenantId, Platform, Status)
    INCLUDE (SourceType, Value, CampaignScope, CreatedUtc);
GO

-- Rollup progress per tenant and named rollup (ANA-07). WatermarkUtc = exclusive
-- upper bound of raw-event time already materialized into summaries.
CREATE TABLE dbo.RollupWatermarks
(
    TenantId     uniqueidentifier NOT NULL,
    RollupName   varchar(64)      NOT NULL,   -- e.g. 'verdict_daily', 'flagged_sources_daily'
    WatermarkUtc datetime2(3)     NOT NULL,
    UpdatedUtc   datetime2(3)     NOT NULL CONSTRAINT DF_RW_UpdatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_RollupWatermarks PRIMARY KEY CLUSTERED (TenantId, RollupName)
);
GO

-- D11 RULE: new tenant tables join the RLS policy in their own migration.
ALTER SECURITY POLICY rls.TenantIsolationPolicy
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.VerdictDailySummaries,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.VerdictDailySummaries,
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.FlaggedSourcesDaily,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.FlaggedSourcesDaily,
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.ExclusionQueue,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.ExclusionQueue,
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.RollupWatermarks,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.RollupWatermarks;
GO
