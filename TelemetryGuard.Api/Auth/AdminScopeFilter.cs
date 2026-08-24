using TelemetryGuard.Core.Tenancy;

namespace TelemetryGuard.Api.Auth;

/// <summary>
/// API-07: authorization gate for the <c>/admin</c> route group.
///
/// Authentication happened upstream: <see cref="TelemetryGuard.Api.Tenancy.TenantResolutionMiddleware"/>
/// resolved <c>X-Api-Key</c> and copied the key's granted scopes onto the ambient
/// <see cref="ITenantContext"/>; an unresolved request already received a 401. This filter
/// decides what a resolved key may do:
/// <list type="bullet">
///   <item>GET/HEAD (reports, listings, evidence) — <see cref="ApiKeyScopes.Admin"/> or
///   <see cref="ApiKeyScopes.Report"/>;</item>
///   <item>anything else (policy PUT, enforcement approve/reject, whitelist writes,
///   labels) — <see cref="ApiKeyScopes.Admin"/> only.</item>
/// </list>
/// A key issued with <c>ingest</c> alone, or resolved via a site key, is refused with a
/// 403 problem+json. Deciding this from the key's scopes rather than "some key of this
/// tenant exists" is the whole point: a leaked ingest key must not be able to switch
/// enforcement off or push exclusions to Google Ads.
/// </summary>
public sealed class AdminScopeFilter : IEndpointFilter
{
    private const string ProblemType = "https://telemetryguard.dev/errors/admin-scope-required";

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var tenant = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();

        // Unresolved (should be unreachable — the middleware already 401'd it) or
        // resolved via a site key (site keys are public and never grant admin access).
        if (!tenant.IsResolved || tenant.SiteKey is not null)
            return ValueTask.FromResult<object?>(Forbidden("Admin scope required"));

        var method = context.HttpContext.Request.Method;
        var readOnly = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        if (!ApiKeyScopes.Allows(tenant.Scopes, readOnly))
        {
            var needed = readOnly
                ? $"'{ApiKeyScopes.Admin}' or '{ApiKeyScopes.Report}'"
                : $"'{ApiKeyScopes.Admin}'";
            return ValueTask.FromResult<object?>(Forbidden(
                "Insufficient API key scope",
                $"This route requires the {needed} scope; the presented key grants " +
                (tenant.Scopes.Count == 0 ? "none." : $"[{string.Join(" ", tenant.Scopes)}].")));
        }

        return next(context);
    }

    private static IResult Forbidden(string title, string? detail = null)
        => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: title, detail: detail, type: ProblemType);
}
