using TelemetryGuard.Core.Tenancy;  // ITenantContext, TenantId, TenantContext — per FND-04
using TelemetryGuard.Data.Tenancy;  // ITenantResolver

namespace TelemetryGuard.Api.Tenancy;

/// <summary>
/// Resolves the tenant early in the pipeline (spec D11): X-Api-Key header for
/// dashboard/admin/API traffic, ?k= site key for the ingestion GETs /c and /p.gif.
/// Unresolved requests get anti-probing responses: success-shaped drops on ingestion
/// routes (1x1 GIF for /p.gif, plain 404 for /c) and 401 problem+json elsewhere.
/// Beacon routes (/i, /i/init) carry their key in the JSON body which middleware must
/// not buffer — their endpoints (API-04) resolve via ITenantResolver themselves and
/// drop unknown keys with a 204; this middleware passes them through unresolved.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    private const string ApiKeyHeader = "X-Api-Key";
    private const string SiteKeyParam = "k";

    // 43-byte transparent 1x1 GIF — success-shaped drop for /p.gif (anti-probing).
    private static readonly byte[] TransparentGif = Convert.FromBase64String(
        "R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAICRAEAOw==");

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
        p.StartsWithSegments("/healthz") || p.StartsWithSegments("/alive")
        || p.StartsWithSegments("/ready"); // API-01's readiness endpoint

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
