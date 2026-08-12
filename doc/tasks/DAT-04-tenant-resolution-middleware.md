---
id: DAT-04
title: Tenant resolution middleware
phase: 1
workstream: data
depends_on: [DAT-03]
size: M
spec_refs: [D11, D22, "§4 components 1–3"]
detail_level: full
---

# DAT-04: Tenant resolution middleware

## Objective
Implement `TenantResolutionMiddleware` for `TelemetryGuard.Api` plus the `ITenantResolver` service it uses: resolve the tenant from (1) an `X-Api-Key` header (SHA-256 → `dbo.ApiKeys` lookup) or (2) a site key in query param `k` on the ingestion GET routes `/c` and `/p.gif`, populate the scoped tenant context from FND-04, and give unresolved requests anti-probing responses (401 problem+json for admin/API routes; success-shaped drops for ingestion routes). Beacon routes (`/i`, `/i/init`) carry their key in the body, which middleware must not read — those endpoints resolve via `ITenantResolver` themselves (hybrid, documented here).

## Spec context (self-contained)
- **D11 — Tenant resolution**: "`TenantResolutionMiddleware` early in the pipeline — API key (dashboard/API) or the site key embedded in the JS snippet (beacon/click traffic) — populates a scoped `ITenantContext` consumed by SQL, Redis, and analytics layers alike." `TenantId` is never optional: downstream layers may assume a resolved context or an explicit unresolved state, never a silently-empty one.
- **D22**: the JS snippet carries `data-site-key`; the pixel is `<img src=".../p.gif?k=…">`; the click tracker is `GET /c?cid=…&k=…`. Site keys are public identifiers (they appear in page source) — possession of a site key must never grant read access to anything; it only attributes ingested traffic.
- **Resolution tables are RLS-exempt** (DAT-02/DAT-03): `dbo.ApiKeys` and `dbo.Sites` are readable on an *unstamped* connection (`IResolutionConnectionFactory`). Every OTHER table returns zero rows on that connection — therefore the resolver must not join `dbo.Tenants` or any RLS-protected table.
- **Anti-probing**: unknown keys on ingestion routes must not be distinguishable from success by an attacker enumerating keys: `/p.gif` always returns the 1×1 GIF; `/i` endpoints return 204 regardless (enforced endpoint-side in API-04); `/c` returns a plain 404 (a redirect is impossible without a campaign, and 404 reveals nothing about which part was invalid). Admin/API routes return 401 `application/problem+json`.
- **Middleware cannot read the request body cheaply** (buffering every beacon POST would tax the hot path), so `/i` and `/i/init` resolution is deliberately deferred to the endpoint (API-04), which calls `ITenantResolver.ResolveSiteKeyAsync` with the body-borne key and drops unresolved beacons with a 204. This hybrid is part of this task's contract.
- Raw API keys are never stored or logged; `dbo.ApiKeys.KeyHash` is `binary(32)` = SHA-256 of the raw key.

## Prerequisites
- **DAT-03**: `IResolutionConnectionFactory` (public interface in `TelemetryGuard.Data`, unscoped, resolution-tables-only) and `DataServiceCollectionExtensions.AddTelemetryGuardData()`.
- **DAT-02**: `dbo.ApiKeys(KeyHash binary(32) PK, TenantId, Scopes nvarchar(400) space-separated, Status tinyint 0=Active)` and `dbo.Sites(TenantId, SiteKey varchar(64) unique, Domain, IntegrationMode 'js'|'pixel')`.
- **FND-04** (via DAT-03's dependency chain), project `TelemetryGuard.Core`: `ITenantContext` (`TenantId TenantId`, `string? SiteKey`), `readonly record struct TenantId(Guid Value)`, and the scoped `TenantContext` implementation with `bool IsResolved { get; }` and `void Resolve(TenantId tenantId, string? siteKey = null)` (callable exactly once per scope; throws `ArgumentException` on empty id). Reading `TenantId` before `Resolve` throws `TenantNotResolvedException` — downstream code never sees a silently-empty tenant.
- **FND-01**: `TelemetryGuard.Api` project skeleton and `tests/TelemetryGuard.Tests.Unit` exist. (API-01 finalizes the full middleware order later; this task registers the middleware and API-01 may re-order it.)

## Implementation steps

1. **Resolver contract — `TelemetryGuard.Data/Tenancy/ITenantResolver.cs`:**
   ```csharp
   namespace TelemetryGuard.Data.Tenancy;

   /// <summary>Result of tenant resolution. Scopes is empty for site-key resolutions;
   /// SiteKey/IntegrationMode are null for API-key resolutions.</summary>
   public sealed record ResolvedTenant(Guid TenantId, string[] Scopes, string? SiteKey, string? IntegrationMode);

   public interface ITenantResolver
   {
       /// <summary>Resolve by raw API key (X-Api-Key header). Returns null when unknown/revoked.</summary>
       Task<ResolvedTenant?> ResolveApiKeyAsync(string apiKey, CancellationToken ct);

       /// <summary>Resolve by public site key (?k= query param or beacon body). Returns null when unknown.</summary>
       Task<ResolvedTenant?> ResolveSiteKeyAsync(string siteKey, CancellationToken ct);
   }
   ```

2. **Resolver implementation — `TelemetryGuard.Data/Tenancy/SqlTenantResolver.cs`.** Add package `Microsoft.Extensions.Caching.Memory` to `TelemetryGuard.Data`.
   ```csharp
   using System.Security.Cryptography;
   using System.Text;
   using System.Text.RegularExpressions;
   using Dapper;
   using Microsoft.Extensions.Caching.Memory;

   namespace TelemetryGuard.Data.Tenancy;

   internal sealed partial class SqlTenantResolver(
       IResolutionConnectionFactory connections,
       IMemoryCache cache) : ITenantResolver
   {
       private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

       [GeneratedRegex("^[A-Za-z0-9_-]{4,64}$")]
       private static partial Regex SiteKeyShape();

       private sealed class ApiKeyRow { public Guid TenantId { get; set; } public string Scopes { get; set; } = ""; }
       private sealed class SiteRow { public Guid TenantId { get; set; } public string SiteKey { get; set; } = ""; public string IntegrationMode { get; set; } = "js"; }

       public async Task<ResolvedTenant?> ResolveApiKeyAsync(string apiKey, CancellationToken ct)
       {
           if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 256) return null;
           var hash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
           var cacheKey = "tg:res:api:" + Convert.ToHexString(hash);
           if (cache.TryGetValue(cacheKey, out ResolvedTenant? cached)) return cached;

           await using var conn = await connections.OpenAsync(ct);
           var row = await conn.QuerySingleOrDefaultAsync<ApiKeyRow>(
               "SELECT TenantId, Scopes FROM dbo.ApiKeys WHERE KeyHash = @hash AND Status = 0",
               new { hash });
           var resolved = row is null
               ? null
               : new ResolvedTenant(row.TenantId,
                   row.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                   null, null);
           cache.Set(cacheKey, resolved, CacheTtl); // negative results cached too (anti-probing)
           return resolved;
       }

       public async Task<ResolvedTenant?> ResolveSiteKeyAsync(string siteKey, CancellationToken ct)
       {
           if (string.IsNullOrWhiteSpace(siteKey) || !SiteKeyShape().IsMatch(siteKey)) return null;
           var cacheKey = "tg:res:site:" + siteKey;
           if (cache.TryGetValue(cacheKey, out ResolvedTenant? cached)) return cached;

           await using var conn = await connections.OpenAsync(ct);
           var row = await conn.QuerySingleOrDefaultAsync<SiteRow>(
               "SELECT TenantId, SiteKey, IntegrationMode FROM dbo.Sites WHERE SiteKey = @siteKey",
               new { siteKey });
           var resolved = row is null
               ? null
               : new ResolvedTenant(row.TenantId, Array.Empty<string>(), row.SiteKey, row.IntegrationMode);
           cache.Set(cacheKey, resolved, CacheTtl);
           return resolved;
       }
   }
   ```
   Register in `DataServiceCollectionExtensions.AddTelemetryGuardData` (DAT-03's file):
   ```csharp
   services.AddMemoryCache();
   services.TryAddSingleton<ITenantResolver, SqlTenantResolver>();
   ```

3. **Middleware — `TelemetryGuard.Api/Tenancy/TenantResolutionMiddleware.cs`** (full code; uses FND-04's `TenantContext.Resolve`):
   ```csharp
   using TelemetryGuard.Core;          // ITenantContext, TenantId, TenantContext — per FND-04
   using TelemetryGuard.Data.Tenancy;  // ITenantResolver

   namespace TelemetryGuard.Api.Tenancy;

   public sealed class TenantResolutionMiddleware(RequestDelegate next)
   {
       private const string ApiKeyHeader = "X-Api-Key";
       private const string SiteKeyParam = "k";

       // 43-byte transparent 1x1 GIF — success-shaped drop for /p.gif (anti-probing).
       private static readonly byte[] TransparentGif = Convert.FromBase64String(
           "R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==");

       public async Task InvokeAsync(HttpContext context, ITenantResolver resolver, TenantContext tenantContext)
       {
           var path = context.Request.Path;

           if (IsExempt(path)) { await next(context); return; }

           // /i and /i/init: site key is in the JSON body; middleware must not buffer the
           // body. API-04's endpoints resolve via ITenantResolver themselves and return 204
           // for unknown keys (success-shaped drop). Pass through with unresolved context.
           if (IsBeaconRoute(path)) { await next(context); return; }

           // Priority 1: X-Api-Key header (dashboard/admin/API traffic).
           if (context.Request.Headers.TryGetValue(ApiKeyHeader, out var apiKeyValues))
           {
               var byKey = await resolver.ResolveApiKeyAsync(apiKeyValues.ToString(), context.RequestAborted);
               if (byKey is not null)
               {
                   tenantContext.Resolve(new TenantId(byKey.TenantId));
                   await next(context);
                   return;
               }
               // Invalid header: for ingestion GETs fall through to the site key;
               // admin routes hit the 401 at the bottom.
           }

           // Priority 2: ?k= site key on /c and /p.gif.
           if (IsClickRoute(path) || IsPixelRoute(path))
           {
               ResolvedTenant? bySite = null;
               if (context.Request.Query.TryGetValue(SiteKeyParam, out var k))
                   bySite = await resolver.ResolveSiteKeyAsync(k.ToString(), context.RequestAborted);

               if (bySite is not null)
               {
                   tenantContext.Resolve(new TenantId(bySite.TenantId), bySite.SiteKey);
                   await next(context);
                   return;
               }

               if (IsPixelRoute(path)) { await WriteGifAsync(context); return; }   // success-shaped drop
               context.Response.StatusCode = StatusCodes.Status404NotFound;        // /c unknown key
               return;
           }

           // Everything else is an admin/API route: unresolved -> 401 problem+json.
           context.Response.StatusCode = StatusCodes.Status401Unauthorized;
           context.Response.ContentType = "application/problem+json";
           await context.Response.WriteAsync(
               """{"type":"https://httpstatuses.io/401","title":"Unauthorized","status":401,"detail":"A valid X-Api-Key header is required."}""",
               context.RequestAborted);
       }

       private static bool IsExempt(PathString p) =>
           p.StartsWithSegments("/healthz") || p.StartsWithSegments("/alive");

       private static bool IsClickRoute(PathString p) => p.StartsWithSegments("/c");
       private static bool IsPixelRoute(PathString p) => p.Equals("/p.gif", StringComparison.OrdinalIgnoreCase);
       private static bool IsBeaconRoute(PathString p) => p.StartsWithSegments("/i");

       private static async Task WriteGifAsync(HttpContext context)
       {
           context.Response.StatusCode = StatusCodes.Status200OK;
           context.Response.ContentType = "image/gif";
           context.Response.Headers.CacheControl = "no-store, private";
           await context.Response.Body.WriteAsync(TransparentGif, context.RequestAborted);
       }
   }
   ```

4. **Wire up in `TelemetryGuard.Api/Program.cs`** (create a minimal one if the scaffold's is empty; API-01 finalizes ordering and OpenTelemetry later):
   ```csharp
   builder.Services.AddTelemetryGuardData();
   // FND-04 registers the scoped TenantContext / ITenantContext; ensure that call is present.
   ...
   app.UseMiddleware<TelemetryGuard.Api.Tenancy.TenantResolutionMiddleware>(); // early, before endpoints
   ```

5. **Unit tests** in `tests/TelemetryGuard.Tests.Unit/Tenancy/TenantResolutionMiddlewareTests.cs` using `DefaultHttpContext`, a fake `ITenantResolver`, and the real (or a test) `TenantContext`:
   ```csharp
   private sealed class FakeResolver : ITenantResolver
   {
       public Dictionary<string, ResolvedTenant> ApiKeys { get; } = new();
       public Dictionary<string, ResolvedTenant> SiteKeys { get; } = new();
       public Task<ResolvedTenant?> ResolveApiKeyAsync(string apiKey, CancellationToken ct)
           => Task.FromResult(ApiKeys.GetValueOrDefault(apiKey));
       public Task<ResolvedTenant?> ResolveSiteKeyAsync(string siteKey, CancellationToken ct)
           => Task.FromResult(SiteKeys.GetValueOrDefault(siteKey));
   }
   ```
   Set `context.Response.Body = new MemoryStream()` in tests that assert on the body.

## Files to create or modify
- `TelemetryGuard.Data/Tenancy/ITenantResolver.cs` (new)
- `TelemetryGuard.Data/Tenancy/SqlTenantResolver.cs` (new)
- `TelemetryGuard.Data/DataServiceCollectionExtensions.cs` (modify: resolver + memory cache registration)
- `TelemetryGuard.Data/TelemetryGuard.Data.csproj` (modify: `Microsoft.Extensions.Caching.Memory`)
- `TelemetryGuard.Api/Tenancy/TenantResolutionMiddleware.cs` (new)
- `TelemetryGuard.Api/Program.cs` (modify: `AddTelemetryGuardData()` + `UseMiddleware`)
- `tests/TelemetryGuard.Tests.Unit/Tenancy/TenantResolutionMiddlewareTests.cs` (new)

## Acceptance criteria
- `dotnet build TelemetryGuard.sln` succeeds; `dotnet test tests/TelemetryGuard.Tests.Unit` passes.
- All of these unit tests exist and pass:
  1. Admin route (`/admin/whatever`) with valid `X-Api-Key` → tenant context set to the resolver's TenantId, `next` invoked.
  2. Admin route with no header → 401, `application/problem+json` content type, `next` NOT invoked.
  3. Admin route with unknown key → 401, `next` NOT invoked.
  4. `/c?cid=…&k=goodkey` → context set, `next` invoked.
  5. `/c?cid=…&k=badkey` → 404, empty body, `next` NOT invoked.
  6. `/c` with no `k` → 404.
  7. `/p.gif?k=goodkey` → context set, `next` invoked.
  8. `/p.gif?k=badkey` → 200, `image/gif`, body is exactly the 43-byte GIF, `Cache-Control: no-store, private`, `next` NOT invoked.
  9. `/i` and `/i/init` POST → `next` invoked, context remains unresolved (no resolver call for the body).
  10. Header wins: `/c?k=goodSiteKey` with a valid `X-Api-Key` for a different tenant → context set from the API key.
  11. `/healthz` → `next` invoked, no resolution attempted.
- `SqlTenantResolver` never logs or stores a raw API key (hash only in cache keys) — verify by inspection.
- The resolver queries ONLY `dbo.ApiKeys` and `dbo.Sites` (RLS-exempt); no join to `dbo.Tenants`.

## Testing
Unit tests as enumerated (fake resolver — no database). SQL-backed resolver behavior (hash lookup, 60 s cache, negative caching) is covered by DAT-08's integration harness, which may construct `SqlTenantResolver` via `AddTelemetryGuardData` against the Testcontainers database. No Playwright/end-to-end here.

## Out of scope / guardrails
- Do NOT read or buffer the request body in middleware — `/i` resolution is endpoint-side (API-04) by design; changing that blows the ingestion hot path.
- Do NOT implement the actual `/c`, `/p.gif`, `/i` endpoints (API-02/03/04) or scope-based authorization (API-07 consumes `ResolvedTenant.Scopes` later — this task only carries scopes through).
- Do NOT use `ITenantConnectionFactory` or `ISystemConnectionFactory` in the resolver — resolution runs before tenant context exists and must use `IResolutionConnectionFactory` only; conversely, never leak `IResolutionConnectionFactory` into any repository.
- Do NOT return different bodies/latencies for known-vs-unknown site keys on `/p.gif` (anti-probing), and never echo the presented key in any response.
- Tenant id is never optional downstream (D11): the context is either explicitly set or explicitly unresolved; nothing may default it to `Guid.Empty` silently.
- Dapper + `Microsoft.Data.SqlClient` only (D9); no EF. No server-side Python/Node (D1). Keep resolution O(1 cached lookup) — it sits inside the <50 ms scoring budget path for /c.
