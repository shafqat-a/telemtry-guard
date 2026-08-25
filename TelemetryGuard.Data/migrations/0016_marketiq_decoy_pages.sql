------------------------------------------------------------------------------
-- Exact per-site paths that are controlled decoy pages for MarketIQ evidence.
-- JSON array example: ["/tg-decoy/a7f91","/internal/trap"]
------------------------------------------------------------------------------
ALTER TABLE dbo.Sites ADD MarketIqDecoyPathsJson nvarchar(2048) NULL;
GO

ALTER TABLE dbo.Sites ADD CONSTRAINT CK_Sites_MarketIqDecoyPathsJson
CHECK (MarketIqDecoyPathsJson IS NULL OR ISJSON(MarketIqDecoyPathsJson) = 1);
GO

UPDATE dbo.Sites
SET MarketIqDecoyPathsJson = N'["/tg-decoy/catalog-preview","/tg-decoy/private-offer","/tg-decoy/archive-index"]'
WHERE Domain = N'bu.edu.bd';
GO
