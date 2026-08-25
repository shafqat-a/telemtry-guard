CREATE TABLE dbo.ConversionGoals
(
    TenantId uniqueidentifier NOT NULL,
    GoalId uniqueidentifier NOT NULL,
    SiteKey varchar(64) NOT NULL,
    Name nvarchar(160) NOT NULL,
    TriggerType varchar(32) NOT NULL,
    PagePathsJson nvarchar(2048) NOT NULL CONSTRAINT DF_ConversionGoals_Paths DEFAULT N'[]',
    Selector nvarchar(512) NULL,
    MinimumSeconds int NULL,
    IsPrimary bit NOT NULL CONSTRAINT DF_ConversionGoals_Primary DEFAULT 0,
    SendMarketIq bit NOT NULL CONSTRAINT DF_ConversionGoals_Miq DEFAULT 1,
    SendMeta bit NOT NULL CONSTRAINT DF_ConversionGoals_Meta DEFAULT 0,
    SendGoogleAds bit NOT NULL CONSTRAINT DF_ConversionGoals_Google DEFAULT 0,
    SendGa4 bit NOT NULL CONSTRAINT DF_ConversionGoals_Ga4 DEFAULT 0,
    SendTikTok bit NOT NULL CONSTRAINT DF_ConversionGoals_Tiktok DEFAULT 0,
    IsActive bit NOT NULL CONSTRAINT DF_ConversionGoals_Active DEFAULT 1,
    CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_ConversionGoals_Created DEFAULT SYSUTCDATETIME(),
    UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_ConversionGoals_Updated DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_ConversionGoals PRIMARY KEY (TenantId,GoalId),
    CONSTRAINT FK_ConversionGoals_Sites FOREIGN KEY (TenantId,SiteKey) REFERENCES dbo.Sites(TenantId,SiteKey),
    CONSTRAINT CK_ConversionGoals_Trigger CHECK (TriggerType IN ('time_on_page','link_click','button_click','form_submitted','server')),
    CONSTRAINT CK_ConversionGoals_Paths CHECK (ISJSON(PagePathsJson)=1),
    CONSTRAINT CK_ConversionGoals_Seconds CHECK (MinimumSeconds IS NULL OR MinimumSeconds BETWEEN 1 AND 86400)
);
GO

CREATE TABLE dbo.ConversionEvents
(
    TenantId uniqueidentifier NOT NULL,
    EventId uniqueidentifier NOT NULL,
    GoalId uniqueidentifier NOT NULL,
    SiteKey varchar(64) NOT NULL,
    SessionId varchar(64) NULL,
    VisitId varchar(64) NULL,
    PageUrl nvarchar(2048) NULL,
    OccurredUtc datetime2(3) NOT NULL,
    PageToConversionMs bigint NULL,
    Verified bit NOT NULL,
    EvidenceSource varchar(16) NOT NULL,
    Value decimal(18,4) NULL,
    Currency char(3) NULL,
    CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_ConversionEvents_Created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_ConversionEvents PRIMARY KEY (TenantId,EventId),
    CONSTRAINT FK_ConversionEvents_Goals FOREIGN KEY (TenantId,GoalId) REFERENCES dbo.ConversionGoals(TenantId,GoalId),
    CONSTRAINT CK_ConversionEvents_Source CHECK (EvidenceSource IN ('browser','server')),
    CONSTRAINT CK_ConversionEvents_Duration CHECK (PageToConversionMs IS NULL OR PageToConversionMs>=0)
);
GO

CREATE INDEX IX_ConversionGoals_Site ON dbo.ConversionGoals(TenantId,SiteKey,IsActive);
CREATE INDEX IX_ConversionEvents_Visit ON dbo.ConversionEvents(TenantId,SiteKey,VisitId,OccurredUtc);
GO

ALTER SECURITY POLICY rls.TenantIsolationPolicy ADD
    FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.ConversionGoals,
    BLOCK PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.ConversionGoals,
    FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.ConversionEvents,
    BLOCK PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.ConversionEvents;
GO

GRANT SELECT,INSERT,UPDATE,DELETE ON dbo.ConversionGoals TO tg_app;
GRANT SELECT,INSERT ON dbo.ConversionEvents TO tg_app;
GRANT SELECT,INSERT,UPDATE,DELETE ON dbo.ConversionGoals TO tg_system;
GRANT SELECT,INSERT,UPDATE,DELETE ON dbo.ConversionEvents TO tg_system;
GO
