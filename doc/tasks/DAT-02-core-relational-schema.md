---
id: DAT-02
title: Core relational schema migration
phase: 1
workstream: data
depends_on: [DAT-01]
size: M
spec_refs: [D8, D11, D20, D21, D22, D23, "§6.1"]
detail_level: full
---

# DAT-02: Core relational schema migration

## Objective
Add migration `0001_core_schema.sql` to `TelemetryGuard.Data/migrations/` creating the core SQL Server config tables — `Tenants`, `ApiKeys`, `Sites`, `Campaigns` — with tenant-leading composite keys, the retention and enforcement-mode settings mandated by the spec, and the campaign `LandingUrl` column that is the sole source of click-redirect destinations. `ApiKeys` and `Sites` are explicitly designated *resolution tables* that DAT-03 will exempt from Row-Level Security.

## Spec context (self-contained)
- **D8/D23 — SQL Server owns small relational config data**: tenants, API keys, sites, campaign config. Raw click/event rows go to ClickHouse, never here.
- **D11 — Multi-tenancy: shared database, shared schema, `TenantId` on every row.** Composite primary keys and indexes lead with `TenantId` (e.g. `(TenantId, CampaignId)`), so uniqueness is per-tenant and lookups partition naturally. `TenantId` is NEVER optional/nullable on a tenant-scoped table.
- **Resolution-table exception**: `ApiKeys` (looked up by key hash) and `Sites` (looked up by site key) are read by tenant-resolution middleware *before* any tenant context exists, so their lookup indexes cannot lead with `TenantId` and DAT-03 must NOT put them under the RLS policy. Mark both with a header comment in the SQL.
- **D20 — Retention**: per-tenant raw-event retention is configurable between 30 and 180 days; default 90. Stored as `Tenants.RetentionDays int NOT NULL DEFAULT 90 CHECK (30..180)`. (ClickHouse denormalizes this value per row at ingest — not this task's concern.)
- **D21 — EnforcementMode**: per-tenant `tinyint`; `0 = AutoEnforce` (default — block band and exclusion pushes execute automatically), `1 = ApprovalQueue` (those actions queue for tenant approval). Challenge band (31–70) is always automatic regardless.
- **D22 — Integration modes**: each Site is `'js'` (full SDK snippet) or `'pixel'` (HTTP-only web pixel). Stored per site.
- **Open-redirect guardrail (consumed by API-02)**: the click tracker `GET /c` 302-redirects ONLY to `Campaigns.LandingUrl` looked up by `(TenantId, CampaignId)`. It is `NOT NULL` and it is the ONLY place a redirect destination may come from — never from a query parameter. The schema comment must say so.
- **D10** — this is a plain numbered DbUp SQL script; `GO` separators are supported; the script is applied in its own transaction and journaled in `dbo.SchemaVersions`.

## Prerequisites
DAT-01 exists: `TelemetryGuard.MigrationRunner` applies embedded scripts from `TelemetryGuard.Data/migrations/NNNN_description.sql` (the csproj already embeds `migrations\**\*.sql`), journaling to `dbo.SchemaVersions`, connection via env `MIGRATIONS_CONNECTIONSTRING`. A `migrations/README.md` number registry exists — append `0001` to it.

## Implementation steps

1. Create `TelemetryGuard.Data/migrations/0001_core_schema.sql` with exactly this content:

```sql
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
```

2. Append to the number registry table in `TelemetryGuard.Data/migrations/README.md`:
   ```
   | 0001 | Core config schema (Tenants, ApiKeys, Sites, Campaigns) | DAT-02 |
   ```

3. Verify the file is picked up as an embedded resource (`dotnet build`, then confirm via `dotnet run --project TelemetryGuard.MigrationRunner` against a disposable server — see Acceptance criteria). The csproj glob from DAT-01 (`migrations\**\*.sql`) requires no change.

## Files to create or modify
- `TelemetryGuard.Data/migrations/0001_core_schema.sql` (new)
- `TelemetryGuard.Data/migrations/README.md` (modify: registry row)

## Acceptance criteria
With a disposable SQL Server (`docker run -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='Your_password123' -p 1433:1433 -d mcr.microsoft.com/mssql/server:2022-latest`) and `MIGRATIONS_CONNECTIONSTRING="Server=localhost,1433;Database=TelemetryGuard;User Id=sa;Password=Your_password123;TrustServerCertificate=True"`:
- `dotnet run --project TelemetryGuard.MigrationRunner` exits 0 and `dbo.SchemaVersions` contains one row ending in `0001_core_schema.sql`.
- A second run exits 0 and applies nothing.
- `INSERT INTO dbo.Tenants (TenantId, Name) VALUES (NEWID(), N'T1')` succeeds and the row shows `Status=0, RetentionDays=90, EnforcementMode=0`.
- `INSERT ... RetentionDays = 29` and `RetentionDays = 181` both fail with a CHECK violation; `30` and `180` succeed.
- Inserting a tenant with `TenantId = '00000000-0000-0000-0000-000000000001'` fails (sentinel CHECK).
- Two `dbo.Sites` rows with the same `SiteKey` under different tenants fail on `UQ_Sites_SiteKey`.
- Inserting a `dbo.Campaigns` row with `LandingUrl = NULL` fails; with `GeoTargets = N'not json'` fails; with `GeoTargets = N'["US"]'` succeeds.
- `Platform = 'bing'` fails the CHECK; `'google'`, `'meta'`, `'tiktok'`, `'other'` succeed.

## Testing
No test-project code in this task. The schema is exercised by manual `sqlcmd` checks above and, definitively, by DAT-08's Testcontainers harness (which runs this migration on every test run). Keep constraint names exactly as written — DAT-08 asserts on error text containing them.

## Out of scope / guardrails
- Do NOT create the RLS schema, predicate function, or security policy here — that is DAT-03's migration `0002`. This script must not reference the `rls` schema.
- Do NOT create summary/verdict/exclusion/whitelist tables (DAT-06, DAT-07) or any raw-event tables (raw events live in ClickHouse only — D23).
- Do NOT add seed data; tests and environments seed their own tenants.
- Do NOT make `TenantId` nullable anywhere, and do not create any tenant-scoped index that fails to lead with `TenantId` (the two documented resolution-table exceptions aside) — D11.
- Do NOT store raw API keys — only the SHA-256 hash (`KeyHash binary(32)`).
- No EF, no generated migrations — hand-written T-SQL in a DbUp script only (D9/D10).
- Never edit this script after it has been applied anywhere; subsequent changes are new numbered scripts.
