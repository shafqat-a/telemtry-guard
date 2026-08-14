------------------------------------------------------------------------------
-- 0008_publisher_site_summaries.sql  (P2-01)
-- D23 split: two more SQL Server AGGREGATE tables materialized from ClickHouse
-- by the ANA-07 RollupService. Raw events stay in ClickHouse. Both tables are
-- tenant-scoped => RLS FILTER + BLOCK at the bottom (the DAT-03 rule).
--
-- MERGEABILITY CONTRACT: every numeric column here is summable across rows.
-- Averages/ratios are NEVER stored (readers compute ScoreSum/Events); distinct
-- counts are NEVER stored (they cannot be summed across days without lying).
------------------------------------------------------------------------------

-- Per-tenant/publisher-placement/day verdict counts. Placement = the normalized
-- publisher host (lowercase, leading "www." stripped) of the session's earliest
-- tracker/pixel referrer -- see IAnalyticsQueries.GetTopPlacementsDailyAsync.
-- A verdict with no resolvable placement produces NO ROW (spec §7: missing != zero);
-- the unattributed remainder is derivable from dbo.SiteDailySummaries.TotalEvents.
CREATE TABLE dbo.PublisherDailySummaries
(
    TenantId        uniqueidentifier NOT NULL,
    [Date]          date             NOT NULL,
    Placement       varchar(256)     NOT NULL,   -- host only; max DNS name is 253 chars
    Events          int              NOT NULL CONSTRAINT DF_PDS_Events DEFAULT (0),      -- scored (verdict) events
    Allowed         int              NOT NULL CONSTRAINT DF_PDS_Allowed DEFAULT (0),     -- score 0-30
    Challenged      int              NOT NULL CONSTRAINT DF_PDS_Challenged DEFAULT (0),  -- score 31-70
    Blocked         int              NOT NULL CONSTRAINT DF_PDS_Blocked DEFAULT (0),     -- score 71-100
    ScoreSum        bigint           NOT NULL CONSTRAINT DF_PDS_ScoreSum DEFAULT (0),    -- mean = ScoreSum/Events
    NoJsBeaconCount int              NOT NULL CONSTRAINT DF_PDS_NoJsBeacon DEFAULT (0),
    UpdatedUtc      datetime2(3)     NOT NULL CONSTRAINT DF_PDS_UpdatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_PublisherDailySummaries PRIMARY KEY CLUSTERED (TenantId, [Date], Placement),
    CONSTRAINT CK_PDS_Placement CHECK (LEN(Placement) > 0)
);
GO

-- Per-tenant/site/day counts: the NON-CAMPAIGN view (dbo.VerdictDailySummaries is
-- keyed on CampaignId and cannot express pixel-mode/organic traffic).
-- TotalEvents spans EVERY event kind; Events counts verdicts only.
CREATE TABLE dbo.SiteDailySummaries
(
    TenantId        uniqueidentifier NOT NULL,
    [Date]          date             NOT NULL,
    SiteKey         varchar(64)      NOT NULL,   -- matches dbo.Sites.SiteKey
    TotalEvents     int              NOT NULL CONSTRAINT DF_SDS_TotalEvents DEFAULT (0),
    Events          int              NOT NULL CONSTRAINT DF_SDS_Events DEFAULT (0),
    Allowed         int              NOT NULL CONSTRAINT DF_SDS_Allowed DEFAULT (0),
    Challenged      int              NOT NULL CONSTRAINT DF_SDS_Challenged DEFAULT (0),
    Blocked         int              NOT NULL CONSTRAINT DF_SDS_Blocked DEFAULT (0),
    ScoreSum        bigint           NOT NULL CONSTRAINT DF_SDS_ScoreSum DEFAULT (0),
    NoJsBeaconCount int              NOT NULL CONSTRAINT DF_SDS_NoJsBeacon DEFAULT (0),
    UpdatedUtc      datetime2(3)     NOT NULL CONSTRAINT DF_SDS_UpdatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_SiteDailySummaries PRIMARY KEY CLUSTERED (TenantId, [Date], SiteKey)
);
GO

-- D11 RULE: new tenant tables join the RLS policy in their own migration.
-- (dbo.Sites is RLS-EXEMPT as a resolution table -- that exemption does NOT
-- extend to these aggregates.)
ALTER SECURITY POLICY rls.TenantIsolationPolicy
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.PublisherDailySummaries,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.PublisherDailySummaries,
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.SiteDailySummaries,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.SiteDailySummaries;
GO
