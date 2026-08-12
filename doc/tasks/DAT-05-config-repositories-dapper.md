---
id: DAT-05
title: Config repositories (Dapper)
phase: 1
workstream: data
depends_on: [DAT-03]
size: M
spec_refs: [D9, D11, D3, "§6.1", D21, D20]
detail_level: full
---

# DAT-05: Config repositories (Dapper)

## Objective
Implement the intent-named config repositories over the DAT-02 tables — `ITenantRepository`, `ISiteRepository`, `ICampaignRepository` — as hand-written Dapper SQL behind narrow interfaces in `TelemetryGuard.Data`. Every method opens its connection via `ITenantConnectionFactory` (tenant-stamped, RLS-scoped) and *additionally* writes an explicit `TenantId = @TenantId` predicate for index seeks. `ICampaignRepository.GetRedirectAsync` is the `/c` click-tracker hot path and must be a single clustered-index seek.

## Spec context (self-contained)
- **D9 — Dapper, not EF Core.** Explicit SQL behind repository-per-aggregate, intent-named interfaces. No LINQ providers, no change tracking, no EF packages.
- **D11 — layered tenant enforcement**: (1) RLS via `SESSION_CONTEXT('TenantId')` is primary; (2) connections only from `ITenantConnectionFactory`; (3) **explicit `TenantId` in every WHERE clause anyway** — for index seeks and readability, even though correctness no longer depends on it; (4) composite keys lead with `TenantId` (`PK_Campaigns (TenantId, CampaignId)`).
- **D3 / <50 ms budget**: the click tracker `GET /c` (API-02) resolves the campaign's landing URL in the request path before a 302. `GetRedirectAsync` must therefore be one round-trip performing a clustered PK seek on `(TenantId, CampaignId)` selecting only `LandingUrl, Status`. **No extra covering index is needed** — the clustered PK created in DAT-02 *is* the covering index; note this in code comments and do not add one.
- **Open-redirect guardrail**: `Campaigns.LandingUrl` is the ONLY source of redirect destinations; this repository is the only reader API-02 may use for it.
- **D20**: `RetentionDays` must stay within 30–180 (DB CHECK exists; validate in C# too for clean errors). **D21**: `EnforcementMode` is `0 = AutoEnforce`, `1 = ApprovalQueue`.
- Tenant id comes from the ambient `ITenantContext` (FND-04) — never from a method parameter and never optional.

## Prerequisites
- **DAT-03**: `ITenantConnectionFactory` (tenant-stamped `SqlConnection`, config key `ConnectionStrings:Main`), `AddTelemetryGuardData()` extension, Dapper + `Microsoft.Data.SqlClient` package references on `TelemetryGuard.Data`, and the FND-04 project reference (`ITenantContext` with `TenantId TenantId { get; }`; `TenantId.Value` is the `Guid` — adapt if FND-04 differs).
- **DAT-02** tables: `dbo.Tenants(TenantId, Name, Status, RetentionDays, EnforcementMode, CreatedUtc)`, `dbo.Sites(TenantId, SiteKey, Domain, IntegrationMode, CreatedUtc)`, `dbo.Campaigns(TenantId, CampaignId, Platform, ExternalCampaignId, LandingUrl, GeoTargets, Status, CreatedUtc)`.

## Implementation steps

1. **Records — `TelemetryGuard.Data/Models/ConfigRecords.cs`:**
   ```csharp
   namespace TelemetryGuard.Data.Models;

   public sealed record TenantRecord(
       Guid TenantId, string Name, byte Status, int RetentionDays, byte EnforcementMode, DateTime CreatedUtc);

   public sealed record SiteRecord(
       Guid TenantId, string SiteKey, string Domain, string IntegrationMode, DateTime CreatedUtc);

   public sealed record CampaignRecord(
       Guid TenantId, Guid CampaignId, string Platform, string? ExternalCampaignId,
       string LandingUrl, string? GeoTargets, byte Status, DateTime CreatedUtc);

   /// <summary>Hot-path projection for the /c redirect (API-02). Status: 0=Active.</summary>
   public sealed record CampaignRedirect(string LandingUrl, byte Status);
   ```

2. **Interfaces — `TelemetryGuard.Data/Repositories/IConfigRepositories.cs`:**
   ```csharp
   using TelemetryGuard.Data.Models;

   namespace TelemetryGuard.Data.Repositories;

   public interface ITenantRepository
   {
       /// <summary>Row for the ambient tenant; null if missing (should not happen post-resolution).</summary>
       Task<TenantRecord?> GetCurrentAsync(CancellationToken ct);
       /// <summary>D20: throws ArgumentOutOfRangeException outside 30..180. Returns false when no row updated.</summary>
       Task<bool> UpdateRetentionDaysAsync(int retentionDays, CancellationToken ct);
       /// <summary>D21: 0=AutoEnforce, 1=ApprovalQueue; throws ArgumentOutOfRangeException otherwise.</summary>
       Task<bool> UpdateEnforcementModeAsync(byte enforcementMode, CancellationToken ct);
   }

   public interface ISiteRepository
   {
       Task CreateAsync(SiteRecord site, CancellationToken ct);
       Task<SiteRecord?> GetBySiteKeyAsync(string siteKey, CancellationToken ct);
       Task<IReadOnlyList<SiteRecord>> ListAsync(CancellationToken ct);
       /// <summary>integrationMode must be "js" or "pixel" (D22); throws ArgumentException otherwise.</summary>
       Task<bool> UpdateAsync(string siteKey, string domain, string integrationMode, CancellationToken ct);
       Task<bool> DeleteAsync(string siteKey, CancellationToken ct);
   }

   public interface ICampaignRepository
   {
       Task CreateAsync(CampaignRecord campaign, CancellationToken ct);
       Task<CampaignRecord?> GetAsync(Guid campaignId, CancellationToken ct);
       /// <summary>/c HOT PATH: single clustered-PK seek returning only LandingUrl + Status.
       /// The clustered PK (TenantId, CampaignId) covers this — do NOT add another index.</summary>
       Task<CampaignRedirect?> GetRedirectAsync(Guid campaignId, CancellationToken ct);
       Task<IReadOnlyList<CampaignRecord>> ListAsync(CancellationToken ct);
       Task<bool> UpdateAsync(CampaignRecord campaign, CancellationToken ct);
   }
   ```

3. **Implementations.** One file per repository under `TelemetryGuard.Data/Repositories/`. Common pattern — constructor `(ITenantConnectionFactory connections, ITenantContext tenant)`; every method:
   `await using var conn = await connections.OpenAsync(ct);` then Dapper with `new { TenantId = tenant.TenantId.Value, ... }`. Exact SQL per method:

   **`TenantRepository.cs`**
   - `GetCurrentAsync`:
     ```sql
     SELECT TenantId, Name, Status, RetentionDays, EnforcementMode, CreatedUtc
     FROM dbo.Tenants WHERE TenantId = @TenantId;
     ```
     (`QuerySingleOrDefaultAsync<TenantRecord>`)
   - `UpdateRetentionDaysAsync` (guard `retentionDays is < 30 or > 180` → throw first):
     ```sql
     UPDATE dbo.Tenants SET RetentionDays = @RetentionDays WHERE TenantId = @TenantId;
     ```
     (`ExecuteAsync` → `rows == 1`)
   - `UpdateEnforcementModeAsync` (guard `enforcementMode > 1` → throw):
     ```sql
     UPDATE dbo.Tenants SET EnforcementMode = @EnforcementMode WHERE TenantId = @TenantId;
     ```

   **`SiteRepository.cs`**
   - `CreateAsync` (guard `site.IntegrationMode is "js" or "pixel"`, and `site.TenantId == tenant.TenantId.Value` — throw `InvalidOperationException` on mismatch rather than relying on the BLOCK predicate's opaque SqlException):
     ```sql
     INSERT INTO dbo.Sites (TenantId, SiteKey, Domain, IntegrationMode)
     VALUES (@TenantId, @SiteKey, @Domain, @IntegrationMode);
     ```
   - `GetBySiteKeyAsync`:
     ```sql
     SELECT TenantId, SiteKey, Domain, IntegrationMode, CreatedUtc
     FROM dbo.Sites WHERE TenantId = @TenantId AND SiteKey = @SiteKey;
     ```
   - `ListAsync`:
     ```sql
     SELECT TenantId, SiteKey, Domain, IntegrationMode, CreatedUtc
     FROM dbo.Sites WHERE TenantId = @TenantId ORDER BY CreatedUtc;
     ```
   - `UpdateAsync`:
     ```sql
     UPDATE dbo.Sites SET Domain = @Domain, IntegrationMode = @IntegrationMode
     WHERE TenantId = @TenantId AND SiteKey = @SiteKey;
     ```
   - `DeleteAsync`:
     ```sql
     DELETE FROM dbo.Sites WHERE TenantId = @TenantId AND SiteKey = @SiteKey;
     ```

   **`CampaignRepository.cs`**
   - `CreateAsync` (guard Platform in google/meta/tiktok/other; guard TenantId matches ambient; guard `LandingUrl` non-empty absolute `https://`/`http://` URI via `Uri.TryCreate(..., UriKind.Absolute)`):
     ```sql
     INSERT INTO dbo.Campaigns
         (TenantId, CampaignId, Platform, ExternalCampaignId, LandingUrl, GeoTargets, Status)
     VALUES (@TenantId, @CampaignId, @Platform, @ExternalCampaignId, @LandingUrl, @GeoTargets, @Status);
     ```
   - `GetAsync`:
     ```sql
     SELECT TenantId, CampaignId, Platform, ExternalCampaignId, LandingUrl, GeoTargets, Status, CreatedUtc
     FROM dbo.Campaigns WHERE TenantId = @TenantId AND CampaignId = @CampaignId;
     ```
   - `GetRedirectAsync` (**hot path** — comment it as such):
     ```sql
     SELECT LandingUrl, Status FROM dbo.Campaigns
     WHERE TenantId = @TenantId AND CampaignId = @CampaignId;
     ```
     (`QuerySingleOrDefaultAsync<CampaignRedirect>`; explicit TenantId + PK order gives a clustered seek.)
   - `ListAsync`:
     ```sql
     SELECT TenantId, CampaignId, Platform, ExternalCampaignId, LandingUrl, GeoTargets, Status, CreatedUtc
     FROM dbo.Campaigns WHERE TenantId = @TenantId ORDER BY CreatedUtc;
     ```
   - `UpdateAsync`:
     ```sql
     UPDATE dbo.Campaigns
     SET Platform = @Platform, ExternalCampaignId = @ExternalCampaignId,
         LandingUrl = @LandingUrl, GeoTargets = @GeoTargets, Status = @Status
     WHERE TenantId = @TenantId AND CampaignId = @CampaignId;
     ```

4. **DI registration** — extend `AddTelemetryGuardData` in `TelemetryGuard.Data/DataServiceCollectionExtensions.cs`:
   ```csharp
   services.TryAddScoped<Repositories.ITenantRepository, Repositories.TenantRepository>();
   services.TryAddScoped<Repositories.ISiteRepository, Repositories.SiteRepository>();
   services.TryAddScoped<Repositories.ICampaignRepository, Repositories.CampaignRepository>();
   ```
   All repositories are **scoped** (they depend on the scoped `ITenantContext`/factory). Implementations are `internal sealed`.

5. Positional-record mapping note for implementers: Dapper maps to constructor parameters by name for records; keep SELECT column aliases matching the record parameter names exactly (they already do).

## Files to create or modify
- `TelemetryGuard.Data/Models/ConfigRecords.cs` (new)
- `TelemetryGuard.Data/Repositories/IConfigRepositories.cs` (new)
- `TelemetryGuard.Data/Repositories/TenantRepository.cs` (new)
- `TelemetryGuard.Data/Repositories/SiteRepository.cs` (new)
- `TelemetryGuard.Data/Repositories/CampaignRepository.cs` (new)
- `TelemetryGuard.Data/DataServiceCollectionExtensions.cs` (modify: registrations)

## Acceptance criteria
- `dotnet build TelemetryGuard.sln` succeeds.
- Every repository method contains an explicit `TenantId = @TenantId` predicate (or inserts `@TenantId`) — verify: `grep -L "@TenantId" TelemetryGuard.Data/Repositories/*Repository.cs` returns nothing.
- No repository constructs a `SqlConnection` directly and none references `IResolutionConnectionFactory` / `ISystemConnectionFactory` — connections come only from `ITenantConnectionFactory`.
- `UpdateRetentionDaysAsync(29)` / `(181)` throw `ArgumentOutOfRangeException` without touching the database; `UpdateEnforcementModeAsync(2)` likewise.
- `CreateAsync` on sites/campaigns throws on ambient-tenant mismatch before executing SQL.
- `GetRedirectAsync`'s query plan against the migrated schema is a single Clustered Index Seek on `PK_Campaigns` (check once with `SET STATISTICS PROFILE ON` or the SSMS/Azure Data Studio estimated plan; no scan, no lookup).
- All methods execute successfully against a migrated database — proven by DAT-08's harness (one test per method listed there).

## Testing
No new test project here; DAT-08 owns the integration coverage (Testcontainers + migrations + one execution per method, cross-tenant isolation, and the guard-clause unit assertions). Keep interfaces/records exactly as specified — DAT-08 enumerates them by these names, and API-02/API-07 consume `ICampaignRepository`/`ITenantRepository`/`ISiteRepository` by these signatures.

## Out of scope / guardrails
- **No EF Core anywhere** (D9) — Dapper over `Microsoft.Data.SqlClient` only; no query builders, no LINQ-to-SQL layers.
- Do NOT add caching here (API-02 decides its own response/redirect caching); repositories stay stateless single-round-trip.
- Do NOT create tenants here — tenant provisioning is an admin/system concern outside this task (RLS BLOCK prevents inserting other tenants' rows anyway; creating the *current* tenant's row is meaningless pre-resolution).
- Do NOT touch verdict/summary/exclusion/whitelist tables (DAT-06/DAT-07 own those) and never query ClickHouse from this project — SQL Server config/summaries only (D23); no generic cross-engine query layer (D7).
- Tenant id parameter rules: never accept a `TenantId` method parameter, never make it optional — ambient `ITenantContext` only (D11).
- Do NOT drop the explicit `WHERE TenantId = @TenantId` on the theory that RLS covers it — index seeks depend on it (D11.3).
- Keep `GetRedirectAsync` to the two columns listed — it sits inside the <50 ms scoring/redirect budget (D3).
