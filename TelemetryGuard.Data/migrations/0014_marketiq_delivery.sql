------------------------------------------------------------------------------
-- Per-site MarketIQ delivery configuration and durable visit outbox.
------------------------------------------------------------------------------
ALTER TABLE dbo.Sites ADD
    MarketIqEnabled        bit            NOT NULL CONSTRAINT DF_Sites_MiqEnabled DEFAULT (0),
    MarketIqCompanyId      int            NULL,
    MarketIqCollectUrl     nvarchar(2048) NULL,
    MarketIqHealthUrl      nvarchar(2048) NULL,
    MarketIqHealthTokenRef varchar(128)   NULL;
GO

ALTER TABLE dbo.Sites ADD CONSTRAINT CK_Sites_MiqConfig CHECK
(
    MarketIqEnabled = 0 OR
    (MarketIqCompanyId > 0 AND MarketIqCollectUrl IS NOT NULL)
);
GO

CREATE TABLE dbo.MarketIqOutbox
(
    TenantId       uniqueidentifier NOT NULL,
    DeliveryId     uniqueidentifier NOT NULL,
    SiteKey        varchar(64)      NOT NULL,
    EventId        varchar(64)      NOT NULL,
    DestinationUrl nvarchar(2048)   NOT NULL,
    PayloadJson    nvarchar(max)    NOT NULL,
    Status         tinyint          NOT NULL CONSTRAINT DF_MIO_Status DEFAULT (0),
    AttemptCount   int              NOT NULL CONSTRAINT DF_MIO_Attempts DEFAULT (0),
    NextAttemptUtc datetime2(3)     NOT NULL,
    CreatedUtc     datetime2(3)     NOT NULL,
    DeliveredUtc   datetime2(3)     NULL,
    LastError      nvarchar(1000)   NULL,
    CONSTRAINT PK_MarketIqOutbox PRIMARY KEY (TenantId, DeliveryId),
    CONSTRAINT UQ_MarketIqOutbox_Event UNIQUE (TenantId, SiteKey, EventId),
    CONSTRAINT CK_MIO_Payload CHECK (ISJSON(PayloadJson) = 1)
);
GO
CREATE INDEX IX_MarketIqOutbox_Due
    ON dbo.MarketIqOutbox (Status, NextAttemptUtc) INCLUDE (TenantId, DeliveryId);
GO

ALTER SECURITY POLICY rls.TenantIsolationPolicy
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.MarketIqOutbox,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.MarketIqOutbox;
GO

GRANT SELECT, INSERT ON dbo.MarketIqOutbox TO tg_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.MarketIqOutbox TO tg_system;
GO
