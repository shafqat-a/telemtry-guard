------------------------------------------------------------------------------
-- 0006_google_ads_sync.sql  (INT-03)
-- Per-tenant Google Ads account id + pushed-criteria state (exclusion-sync
-- state is SQL Server's job per D8; rows live while the exclusion is active, D20).
--
-- NOTE: dbo.ExclusionQueue's Platform (varchar(16), NOT NULL, CK 'google'|'meta'|
-- 'tiktok'|'other', no default) and LastError (nvarchar(2000) NULL) columns
-- already shipped in 0003_summaries_exclusions.sql (DAT-06) — this migration
-- does NOT touch dbo.ExclusionQueue at all. VerdictFinalizer.cs already resolves
-- Platform from dbo.Campaigns.Platform (falling back to 'other' for
-- campaign-less sessions), so no backfill is needed here either.
------------------------------------------------------------------------------
ALTER TABLE dbo.Tenants ADD
    GoogleAdsCustomerId varchar(10) NULL;  -- 10 digits, no dashes; NULL = sync disabled for tenant
GO

-- State of every negative campaign criterion this worker has pushed to Google.
-- One row per (TenantId, CriterionResourceName). The LRU eviction strategy
-- (SyncPlanner) scans this per campaign, oldest PushedUtc first, to stay under
-- Google's ~500-IP-exclusions-per-campaign cap.
CREATE TABLE dbo.GoogleAdsPushedExclusions
(
    TenantId              uniqueidentifier NOT NULL,
    CriterionResourceName nvarchar(256)    NOT NULL,  -- customers/{cid}/campaignCriteria/{campaignId}~{criterionId}
    GoogleCampaignId      varchar(32)      NOT NULL,
    SourceType            varchar(16)      NOT NULL,  -- 'ip' | 'placement'
    SourceValue           nvarchar(512)    NOT NULL,
    ExclusionQueueId      bigint           NOT NULL,  -- originating dbo.ExclusionQueue.Id
    PushedUtc             datetime2(3)     NOT NULL,
    CONSTRAINT PK_GoogleAdsPushedExclusions PRIMARY KEY CLUSTERED (TenantId, CriterionResourceName),
    CONSTRAINT CK_GAPE_SourceType CHECK (SourceType IN ('ip', 'placement'))
);
GO
-- LRU scan path: oldest pushed ip criteria per campaign.
CREATE NONCLUSTERED INDEX IX_GAPE_Lru
    ON dbo.GoogleAdsPushedExclusions (TenantId, GoogleCampaignId, SourceType, PushedUtc);
GO

-- D11 RULE: new tenant tables join the RLS policy in their own migration.
ALTER SECURITY POLICY rls.TenantIsolationPolicy
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.GoogleAdsPushedExclusions,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.GoogleAdsPushedExclusions;
GO
