using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TelemetryGuard.Integrations.Meta;

/// <summary>
/// Registers MetaOptions + the real <see cref="IMetaMarketingClient"/> only.
///
/// SEAM NOTE (mirrors <c>GoogleAdsServiceCollectionExtensions</c>, INT-03 — "do
/// not diverge" is the explicit house rule here): unlike API-06's/ANA-07's hosted
/// workers, the actual BackgroundService (<c>TelemetryGuard.Api.Workers.MetaExclusionSyncService</c>)
/// is NOT registered here — it lives in TelemetryGuard.Api/Workers because it
/// needs Dapper + ISystemConnectionFactory (TelemetryGuard.Data), which this
/// project deliberately does not reference (D15: keep the Meta wrapper thin —
/// only an HttpClient + System.Text.Json, nothing SQL-shaped). Program.cs calls
/// both this method AND
/// <c>builder.Services.AddHostedService&lt;MetaExclusionSyncService&gt;()</c>.
/// </summary>
public static class MetaServiceCollectionExtensions
{
    public static IServiceCollection AddMetaMarketingClient(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MetaOptions>(configuration.GetSection(MetaOptions.SectionName));
        services.AddHttpClient<IMetaMarketingClient, MetaMarketingClient>(c =>
        {
            // Per-attempt timeout is enforced inside the client via a linked CTS
            // (MetaOptions.TimeoutSeconds); this outer timeout is a safety net only —
            // matches TurnstileServiceCollectionExtensions (INT-01).
            c.Timeout = TimeSpan.FromSeconds(30);
        });
        return services;
    }
}
