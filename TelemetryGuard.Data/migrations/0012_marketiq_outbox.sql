------------------------------------------------------------------------------
-- Durable MarketIQ label submissions and outbound webhook outbox.
------------------------------------------------------------------------------
CREATE TABLE dbo.LabelSubmissions
(
    TenantId        uniqueidentifier NOT NULL,
    SessionId       varchar(64)      NOT NULL,
    LabelSource     varchar(64)      NOT NULL,
    LabelValue      varchar(16)      NOT NULL,
    Weight          real             NOT NULL,
    Version         bigint           NOT NULL,
    DeliveredVersion bigint          NOT NULL CONSTRAINT DF_LS_Delivered DEFAULT (0),
    UpdatedUtc      datetime2(3)     NOT NULL,
    CONSTRAINT PK_LabelSubmissions PRIMARY KEY (TenantId, SessionId, LabelSource),
    CONSTRAINT CK_LS_Label CHECK (LabelValue IN ('fraud','legit')),
    CONSTRAINT CK_LS_Weight CHECK (Weight > 0 AND Weight <= 100)
);
GO

CREATE TABLE dbo.WebhookOutbox
(
    TenantId       uniqueidentifier NOT NULL,
    DeliveryId     uniqueidentifier NOT NULL,
    EventType      varchar(64)      NOT NULL,
    DestinationUrl nvarchar(2048)   NOT NULL,
    SecretRef      varchar(128)     NOT NULL,
    PayloadJson    nvarchar(max)    NOT NULL,
    Status         tinyint          NOT NULL CONSTRAINT DF_WO_Status DEFAULT (0),
    AttemptCount   int              NOT NULL CONSTRAINT DF_WO_Attempts DEFAULT (0),
    NextAttemptUtc datetime2(3)     NOT NULL,
    CreatedUtc     datetime2(3)     NOT NULL,
    DeliveredUtc   datetime2(3)     NULL,
    LastError      nvarchar(1000)   NULL,
    CONSTRAINT PK_WebhookOutbox PRIMARY KEY (TenantId, DeliveryId),
    CONSTRAINT CK_WO_Payload CHECK (ISJSON(PayloadJson) = 1)
);
GO

ALTER SECURITY POLICY rls.TenantIsolationPolicy
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.LabelSubmissions,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.LabelSubmissions,
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.WebhookOutbox,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.WebhookOutbox;
GO
