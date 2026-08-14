------------------------------------------------------------------------------
-- 0007_meta_sync.sql  (INT-04)
-- Per-tenant Meta identifiers + the block-list id we manage for the tenant.
--
-- NOTE: dbo.ExclusionQueue's Platform (CK includes 'meta') and LastError columns
-- already shipped in 0003_summaries_exclusions.sql (DAT-06) — this migration
-- does NOT touch dbo.ExclusionQueue at all.
--
-- dbo.Tenants is already under RLS (see 0002_rls_policy.sql, FILTER + BLOCK on
-- dbo.Tenants) — adding nullable columns to an already-covered table needs no
-- security-policy change.
------------------------------------------------------------------------------
ALTER TABLE dbo.Tenants ADD
    MetaBusinessId       varchar(32)  NULL,  -- Business Manager id; NULL = Meta sync disabled
    MetaBlockListId      varchar(32)  NULL;  -- our managed publisher block list; set on first sync
GO
