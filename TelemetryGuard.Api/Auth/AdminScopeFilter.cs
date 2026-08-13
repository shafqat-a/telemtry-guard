using TelemetryGuard.Core.Tenancy;

namespace TelemetryGuard.Api.Auth;

/// <summary>
/// API-07: defense-in-depth gate for the <c>/admin</c> route group. By DAT-04's
/// construction, site keys only ever resolve <c>/c</c> and <c>/p.gif</c> — every
/// request reaching an <c>/admin/*</c> handler was necessarily resolved via
/// <c>X-Api-Key</c>, and an unresolved request already got a 401 problem+json from
/// <see cref="TelemetryGuard.Api.Tenancy.TenantResolutionMiddleware"/> before it
/// ever reaches routing. This filter is the BACKSTOP, not the primary gate: it
/// re-checks that the ambient tenant is resolved and (belt-and-braces) that it was
/// NOT resolved via a site key, and 403s otherwise.
///
/// Scope enforcement: DAT-04's <c>ResolvedTenant.Scopes</c> (from <c>dbo.ApiKeys</c>)
/// is not yet copied into ambient state by
/// <see cref="TelemetryGuard.Api.Tenancy.TenantResolutionMiddleware"/> — check the
/// repo before assuming otherwise. Until it is, any API-key-authenticated request
/// passes this filter.
/// TODO(DAT-04): enforce the 'admin' scope once scopes are exposed on ITenantContext.
/// </summary>
public sealed class AdminScopeFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var tenant = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();

        // Unresolved (should be unreachable — the middleware already 401'd it) or
        // resolved via a site key (should never happen on /admin/* by DAT-04's
        // construction) are both refused here as a backstop.
        if (!tenant.IsResolved || tenant.SiteKey is not null)
        {
            return ValueTask.FromResult<object?>(Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Admin scope required",
                type: "https://telemetryguard.dev/errors/admin-scope-required"));
        }

        return next(context);
    }
}
