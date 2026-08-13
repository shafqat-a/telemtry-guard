using System.Diagnostics;
using TelemetryGuard.Core.Tenancy;

namespace TelemetryGuard.Api.Middleware;

/// <summary>
/// Enriches the current OpenTelemetry Activity with TelemetryGuard tags:
/// the client IP (post-ForwardedHeaders), the presented site key (?k=), and —
/// after the rest of the pipeline ran — the resolved tenant id. In-memory tag
/// writes only; this middleware must never do I/O (hot-path budget, spec D3).
/// </summary>
public sealed class OtelEnrichmentMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        var activity = Activity.Current;
        if (activity is not null)
        {
            activity.SetTag("tg.client_ip", ctx.Connection.RemoteIpAddress?.ToString());
            if (ctx.Request.Query.TryGetValue("k", out var k))
                activity.SetTag("tg.site_key", k.ToString());
        }
        await next(ctx);
        if (activity is not null)
        {
            var tenant = ctx.RequestServices.GetService<ITenantContext>();
            if (tenant?.IsResolved == true)
                activity.SetTag("tg.tenant_id", tenant.TenantId.Value.ToString("D"));
        }
    }
}
