------------------------------------------------------------------------------
-- Per-site MarketIQ trusted relay authentication.
-- Secret values stay in deployment configuration; SQL stores only lookup refs.
------------------------------------------------------------------------------
ALTER TABLE dbo.Sites ADD MarketIqRelayKeyRef varchar(128) NULL;
GO

ALTER TABLE dbo.MarketIqOutbox ADD RelayKeyRef varchar(128) NULL;
GO

-- Existing queued BU deliveries must also use the trusted relay header.
UPDATE o
SET RelayKeyRef = s.MarketIqRelayKeyRef
FROM dbo.MarketIqOutbox o
JOIN dbo.Sites s ON s.TenantId = o.TenantId AND s.SiteKey = o.SiteKey
WHERE o.RelayKeyRef IS NULL;
GO
