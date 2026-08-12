---
id: DAT-03
title: Row-Level Security and tenant-bound connections
phase: 1
workstream: data
depends_on: [DAT-02, FND-04]
size: L
spec_refs: [D9, D11, "§9"]
detail_level: full
---

# DAT-03: Row-Level Security and tenant-bound connections

## Objective
Make tenant isolation a database-enforced property rather than developer discipline: migration `0002_rls_policy.sql` installs a SQL Server Row-Level Security predicate keyed on `SESSION_CONTEXT(N'TenantId')` with FILTER + BLOCK predicates on every tenant-scoped table (resolution tables exempt), plus a well-known SYSTEM sentinel tenant id for background jobs. On the C# side, `TelemetryGuard.Data` gains the three connection factories that are the *only* ways any code obtains a SQL connection: tenant-stamped (request path), unscoped resolution-only (tenant resolution middleware), and system/per-tenant (background jobs).

## Spec context (self-contained)
- **D11 — RLS is the PRIMARY tenant-isolation enforcement** (promoted from backstop because Dapper has no EF global query filters). A forgotten `WHERE TenantId=…` must return *empty*, not another tenant's rows — a bug, not a breach. Even raw SQL, Grafana connections, or future bugs must be unable to leak cross-tenant rows.
- **D11 — Tenant-bound connections only.** Repositories may only obtain connections from a factory that stamps the session context. There must be no code path in repositories to an unscoped connection. The spec's reference implementation (reproduce it exactly, adapting only the FND-04 type names):
  ```csharp
  public sealed class TenantConnectionFactory(ITenantContext tenant, IConfiguration cfg)
      : ITenantConnectionFactory
  {
      public async Task<SqlConnection> OpenAsync(CancellationToken ct)
      {
          var conn = new SqlConnection(cfg.GetConnectionString("Main"));
          await conn.OpenAsync(ct);
          await conn.ExecuteAsync(
              "EXEC sp_set_session_context @key = N'TenantId', @value = @tid, @read_only = 1",
              new { tid = tenant.TenantId });
          return conn;
      }
  }
  ```
- **D9 — Dapper, not EF Core.** The stamping call uses Dapper's `ExecuteAsync` over `Microsoft.Data.SqlClient`.
- **Resolution tables are exempt**: `dbo.ApiKeys` and `dbo.Sites` (marked in DAT-02) are read before tenant context exists and must NOT be in the security policy.
- **Rule (document + enforce in review)**: every migration that creates a new tenant-scoped table MUST add FILTER + BLOCK predicates for it to `rls.TenantIsolationPolicy` via `ALTER SECURITY POLICY` in that same migration (DAT-06 and DAT-07 do this).
- **SYSTEM sentinel**: background jobs (e.g. ANA-07 rollups) must enumerate all tenants. A well-known sentinel TenantId `00000000-0000-0000-0000-000000000001` is allowed by the predicate to see all rows; it is obtainable ONLY via the system connection factory, is never a real tenant (DAT-02's `CK_Tenants_NotSystemSentinel`), and must never be written into a tenant row.
- **Connection pooling fact**: `sp_reset_connection` (issued on pooled-connection reuse) clears `SESSION_CONTEXT`, so stamping after *every* `Open` is both required and safe. Never cache an open `SqlConnection` across requests or tenants. `@read_only = 1` makes the stamped value immutable for the lifetime of that session — nothing downstream can re-stamp it.

## Prerequisites
- **DAT-02**: tables `dbo.Tenants`, `dbo.ApiKeys` (RLS-exempt), `dbo.Sites` (RLS-exempt), `dbo.Campaigns` exist via migration `0001_core_schema.sql`; migration runner + README registry from DAT-01.
- **FND-04**: the core-primitives project `TelemetryGuard.Core` (namespace `TelemetryGuard.Core`) provides:
  - `ITenantContext` with `TenantId TenantId { get; }` and `string? SiteKey { get; }`,
  - `readonly record struct TenantId(Guid Value)` (so `.Value` below is correct),
  - the scoped `TenantContext` implementation whose `TenantId` getter **throws `TenantNotResolvedException` when unresolved** — so in practice the `Guid.Empty` guard below is defense-in-depth that surfaces only if a different `ITenantContext` implementation is substituted.

## Implementation steps

1. **NuGet packages** on `TelemetryGuard.Data`. FND-01 already added `Dapper 2.1.35` and `Microsoft.Data.SqlClient 5.2.2` and the project reference to `TelemetryGuard.Core` — verify they are present rather than re-adding. Then add what is missing:
   ```bash
   dotnet add TelemetryGuard.Data package Microsoft.Extensions.Configuration.Abstractions
   dotnet add TelemetryGuard.Data package Microsoft.Extensions.DependencyInjection.Abstractions
   ```

2. **Create `TelemetryGuard.Data/migrations/0002_rls_policy.sql`:**

```sql
------------------------------------------------------------------------------
-- 0002_rls_policy.sql  (DAT-03)
-- Row-Level Security: PRIMARY tenant-isolation enforcement (spec D11).
--
-- RULE: every migration that creates a new tenant-scoped table MUST add both a
-- FILTER and a BLOCK predicate for that table via
--   ALTER SECURITY POLICY rls.TenantIsolationPolicy ADD ... ;
-- in the SAME migration. Resolution tables (dbo.ApiKeys, dbo.Sites) are
-- deliberately EXEMPT — they are read to establish tenant context before it exists.
--
-- SYSTEM sentinel: SESSION_CONTEXT stamped with 00000000-0000-0000-0000-000000000001
-- passes the predicate for ALL rows. Only ISystemConnectionFactory may stamp it.
------------------------------------------------------------------------------
CREATE SCHEMA rls;
GO

CREATE FUNCTION rls.fn_tenantPredicate(@TenantId uniqueidentifier)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN
    SELECT 1 AS fn_result
    WHERE @TenantId = CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier)
       OR CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier)
          = CAST('00000000-0000-0000-0000-000000000001' AS uniqueidentifier);
GO

CREATE SECURITY POLICY rls.TenantIsolationPolicy
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.Tenants,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.Tenants,
    ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.Campaigns,
    ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.Campaigns
WITH (STATE = ON, SCHEMABINDING = ON);
GO
```

   Semantics to be aware of (and rely on in tests):
   - Unstamped session → `SESSION_CONTEXT` is NULL → both comparisons are NULL → predicate returns no row → **SELECTs see zero rows** and **INSERT/UPDATE/DELETE are blocked** (error 33504 family).
   - Stamped as tenant A, inserting a row with `TenantId = B` → BLOCK predicate rejects with a `SqlException`.
   - FK checks are not filtered by RLS, so `dbo.Campaigns → dbo.Tenants` referential integrity still works under a stamped session.
   - Append registry row to `migrations/README.md`: `| 0002 | RLS schema, predicate, security policy | DAT-03 |`.

3. **Create `TelemetryGuard.Data/WellKnownTenants.cs`:**
   ```csharp
   namespace TelemetryGuard.Data;

   public static class WellKnownTenants
   {
       /// <summary>
       /// SYSTEM sentinel tenant id ("all-zeros-1"). Allowed by rls.fn_tenantPredicate to
       /// see ALL tenants' rows. Only ISystemConnectionFactory may stamp it. It is never a
       /// real tenant (DAT-02 CHECK constraint) and must never be written into a tenant row.
       /// </summary>
       public static readonly Guid System = new("00000000-0000-0000-0000-000000000001");
   }
   ```

4. **Create `TelemetryGuard.Data/ITenantConnectionFactory.cs`:**
   ```csharp
   using Microsoft.Data.SqlClient;

   namespace TelemetryGuard.Data;

   /// <summary>
   /// The ONLY way request-path code (repositories) obtains a SQL connection. Every
   /// connection is opened and stamped with SESSION_CONTEXT(N'TenantId') = the ambient
   /// tenant from ITenantContext, so RLS scopes every statement on it. Callers dispose
   /// the connection per unit of work (`await using`); pooling makes this cheap, and
   /// sp_reset_connection clears the session context on pool reuse.
   /// </summary>
   public interface ITenantConnectionFactory
   {
       Task<SqlConnection> OpenAsync(CancellationToken ct);
   }
   ```

5. **Create `TelemetryGuard.Data/TenantConnectionFactory.cs`** (spec D11 code plus two guards):
   ```csharp
   using Dapper;
   using Microsoft.Data.SqlClient;
   using Microsoft.Extensions.Configuration;
   using TelemetryGuard.Core; // adjust to FND-04's actual namespace

   namespace TelemetryGuard.Data;

   public sealed class TenantConnectionFactory(ITenantContext tenant, IConfiguration cfg)
       : ITenantConnectionFactory
   {
       public async Task<SqlConnection> OpenAsync(CancellationToken ct)
       {
           var tid = tenant.TenantId.Value; // if FND-04's TenantId is a plain Guid, use tenant.TenantId
           if (tid == Guid.Empty)
               throw new InvalidOperationException(
                   "Tenant context is not resolved; refusing to open a tenant-scoped connection.");
           if (tid == WellKnownTenants.System)
               throw new InvalidOperationException(
                   "The SYSTEM sentinel may only be stamped via ISystemConnectionFactory.");

           var conn = new SqlConnection(cfg.GetConnectionString("Main"));
           try
           {
               await conn.OpenAsync(ct);
               await conn.ExecuteAsync(
                   "EXEC sp_set_session_context @key = N'TenantId', @value = @tid, @read_only = 1",
                   new { tid });
               return conn;
           }
           catch
           {
               await conn.DisposeAsync();
               throw;
           }
       }
   }
   ```

6. **Create `TelemetryGuard.Data/IResolutionConnectionFactory.cs`** (interface public, implementation internal):
   ```csharp
   using Microsoft.Data.SqlClient;
   using Microsoft.Extensions.Configuration;

   namespace TelemetryGuard.Data;

   /// <summary>
   /// UNSCOPED connection factory — no session context is stamped, therefore every
   /// RLS-protected table returns ZERO rows on these connections. Usable ONLY to read
   /// the RLS-exempt resolution tables dbo.ApiKeys and dbo.Sites during tenant
   /// resolution (DAT-04). NEVER inject this into a repository.
   /// </summary>
   public interface IResolutionConnectionFactory
   {
       Task<SqlConnection> OpenAsync(CancellationToken ct);
   }

   internal sealed class ResolutionConnectionFactory(IConfiguration cfg) : IResolutionConnectionFactory
   {
       public async Task<SqlConnection> OpenAsync(CancellationToken ct)
       {
           var conn = new SqlConnection(cfg.GetConnectionString("Main"));
           await conn.OpenAsync(ct);
           return conn;
       }
   }
   ```

7. **Create `TelemetryGuard.Data/ISystemConnectionFactory.cs`:**
   ```csharp
   using Dapper;
   using Microsoft.Data.SqlClient;
   using Microsoft.Extensions.Configuration;

   namespace TelemetryGuard.Data;

   /// <summary>
   /// Background-job connection factory. NEVER use in request-path code.
   ///
   /// Per-tenant stamping pattern for background jobs (e.g. ANA-07 rollups):
   /// <code>
   /// await using var sys = await systemFactory.OpenSystemAsync(ct);
   /// var tenantIds = await sys.QueryAsync&lt;Guid&gt;(
   ///     "SELECT TenantId FROM dbo.Tenants WHERE Status = 0");
   /// foreach (var tid in tenantIds)
   /// {
   ///     await using var conn = await systemFactory.OpenForTenantAsync(tid, ct);
   ///     // every read/write on `conn` is RLS-scoped to that one tenant
   /// }
   /// </code>
   /// Alternative for reusing repositories in jobs: create a DI scope per tenant, set the
   /// scoped TenantContext (FND-04) to that tenant, resolve repositories inside the scope.
   /// </summary>
   public interface ISystemConnectionFactory
   {
       /// <summary>Stamped with the SYSTEM sentinel — sees ALL tenants' rows.
       /// Use only to enumerate tenants / cross-tenant admin reads.</summary>
       Task<SqlConnection> OpenSystemAsync(CancellationToken ct);

       /// <summary>Stamped for one explicit tenant — the per-tenant unit of work
       /// inside a background job.</summary>
       Task<SqlConnection> OpenForTenantAsync(Guid tenantId, CancellationToken ct);
   }

   internal sealed class SystemConnectionFactory(IConfiguration cfg) : ISystemConnectionFactory
   {
       public Task<SqlConnection> OpenSystemAsync(CancellationToken ct)
           => OpenStampedAsync(WellKnownTenants.System, ct);

       public Task<SqlConnection> OpenForTenantAsync(Guid tenantId, CancellationToken ct)
       {
           if (tenantId == Guid.Empty || tenantId == WellKnownTenants.System)
               throw new ArgumentOutOfRangeException(nameof(tenantId),
                   "OpenForTenantAsync requires a real tenant id.");
           return OpenStampedAsync(tenantId, ct);
       }

       private async Task<SqlConnection> OpenStampedAsync(Guid tid, CancellationToken ct)
       {
           var conn = new SqlConnection(cfg.GetConnectionString("Main"));
           try
           {
               await conn.OpenAsync(ct);
               await conn.ExecuteAsync(
                   "EXEC sp_set_session_context @key = N'TenantId', @value = @tid, @read_only = 1",
                   new { tid });
               return conn;
           }
           catch
           {
               await conn.DisposeAsync();
               throw;
           }
       }
   }
   ```

8. **Create `TelemetryGuard.Data/DataServiceCollectionExtensions.cs`:**
   ```csharp
   using Microsoft.Extensions.DependencyInjection;
   using Microsoft.Extensions.DependencyInjection.Extensions;

   namespace TelemetryGuard.Data;

   public static class DataServiceCollectionExtensions
   {
       /// <summary>Registers the connection factories. Repositories (DAT-05/06/07)
       /// extend this method with their own registrations.</summary>
       public static IServiceCollection AddTelemetryGuardData(this IServiceCollection services)
       {
           services.TryAddScoped<ITenantConnectionFactory, TenantConnectionFactory>();
           services.TryAddSingleton<IResolutionConnectionFactory, ResolutionConnectionFactory>();
           services.TryAddSingleton<ISystemConnectionFactory, SystemConnectionFactory>();
           return services;
       }
   }
   ```
   Note: `TenantConnectionFactory` is **scoped** (it depends on the scoped `ITenantContext`); the other two are singletons.

9. **Config key**: the connection string is read from `ConnectionStrings:Main` (`cfg.GetConnectionString("Main")`). Do not introduce any other connection-string key for SQL Server.

## Files to create or modify
- `TelemetryGuard.Data/migrations/0002_rls_policy.sql` (new)
- `TelemetryGuard.Data/migrations/README.md` (modify: registry row)
- `TelemetryGuard.Data/WellKnownTenants.cs` (new)
- `TelemetryGuard.Data/ITenantConnectionFactory.cs` (new)
- `TelemetryGuard.Data/TenantConnectionFactory.cs` (new)
- `TelemetryGuard.Data/IResolutionConnectionFactory.cs` (new)
- `TelemetryGuard.Data/ISystemConnectionFactory.cs` (new)
- `TelemetryGuard.Data/DataServiceCollectionExtensions.cs` (new)
- `TelemetryGuard.Data/TelemetryGuard.Data.csproj` (modify: packages, project reference to the FND-04 core project)
- `tests/TelemetryGuard.Tests.Integration/Data/RlsSmokeTests.cs` (new — see Testing)

## Acceptance criteria
- `dotnet build TelemetryGuard.sln` succeeds.
- Migration applies cleanly on a fresh database (`dotnet run --project TelemetryGuard.MigrationRunner` → exit 0, journal now lists `0002_rls_policy.sql`), and a second run is a no-op.
- Manual RLS proof via `sqlcmd` against the migrated database (seed two tenants first under the sentinel):
  ```sql
  EXEC sp_set_session_context @key = N'TenantId', @value = '00000000-0000-0000-0000-000000000001';
  INSERT INTO dbo.Tenants (TenantId, Name) VALUES ('11111111-1111-1111-1111-111111111111', N'A');
  INSERT INTO dbo.Tenants (TenantId, Name) VALUES ('22222222-2222-2222-2222-222222222222', N'B');
  SELECT COUNT(*) FROM dbo.Tenants;  -- 2 (sentinel sees all)
  ```
  Then on a NEW session:
  - Unstamped: `SELECT COUNT(*) FROM dbo.Tenants` returns **0**; any INSERT into `dbo.Campaigns` fails.
  - Stamped as A (`sp_set_session_context N'TenantId', '11111111-...'`): sees exactly 1 tenant row; inserting a `dbo.Campaigns` row with `TenantId = '22222222-...'` throws (BLOCK predicate); with `TenantId = '11111111-...'` succeeds.
  - `dbo.ApiKeys` / `dbo.Sites` remain readable and writable on an unstamped session (resolution exemption).
- `TenantConnectionFactory.OpenAsync` throws `InvalidOperationException` when the ambient tenant id is `Guid.Empty` or the sentinel.
- `SystemConnectionFactory.OpenForTenantAsync` throws `ArgumentOutOfRangeException` for `Guid.Empty` and the sentinel.
- `grep -r "new SqlConnection" TelemetryGuard.Data/` matches only the three factory classes.

## Testing
- **Automated RLS smoke test (this task — the migration's behavior must be self-verifiable, not manual-only)**: add `tests/TelemetryGuard.Tests.Integration/Data/RlsSmokeTests.cs` (Testcontainers SQL Server + `Migrations.RunSqlServer` from DAT-01): stamp the sentinel session context → insert tenants A and B → open a NEW unstamped connection: `SELECT COUNT(*) FROM dbo.Tenants` returns **0** → open a connection stamped as A: COUNT returns **1**, and an INSERT into `dbo.Campaigns` with `TenantId = B` throws `SqlException` (BLOCK predicate) while `TenantId = A` succeeds. Mark it with a comment as the seed DAT-08 later extends into the full harness (per-method coverage, cross-tenant reads).
- The full automated proof matrix stays in DAT-08 (cross-tenant read returns empty, unstamped sees zero rows, sentinel sees all, every repository method).
- Supplementary: run the manual `sqlcmd` sequence above once against a disposable container and paste the output into the PR description.
- Keep the exact predicate/policy names (`rls.fn_tenantPredicate`, `rls.TenantIsolationPolicy`) — DAT-06/DAT-07 `ALTER` them by name and DAT-08 asserts against them.

## Out of scope / guardrails
- Do NOT add `dbo.ApiKeys` or `dbo.Sites` to the security policy — they are the documented resolution exemption; adding them breaks tenant resolution entirely.
- Do NOT create repositories here (DAT-05/06/07) or middleware (DAT-04).
- Do NOT expose any factory or helper that returns an unstamped connection to general code — `IResolutionConnectionFactory` exists solely for DAT-04's resolver, and its XML doc must keep saying so.
- RLS is the primary enforcement, but repositories still write explicit `WHERE TenantId = @TenantId` (D11, for index seeks) — do not "simplify" that away later, and never treat RLS as a reason to accept an optional tenant id anywhere.
- Dapper + `Microsoft.Data.SqlClient` only; no EF Core (D9). No server-side Python/Node (D1).
- Never write a row whose `TenantId` is the SYSTEM sentinel; it is a session-context value, not a data value.
- Migration `0002` must not touch ClickHouse or Redis — SQL Server only (D23 data split).
