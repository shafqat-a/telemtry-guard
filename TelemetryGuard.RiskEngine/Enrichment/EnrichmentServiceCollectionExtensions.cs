using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TelemetryGuard.RiskEngine.Enrichment;

public static class EnrichmentServiceCollectionExtensions
{
    /// <summary>Registers IIpEnrichmentService (singleton, memory-mapped lookups) and the
    /// background refresh service. Binds IpEnrichmentOptions from the "IpEnrichment"
    /// configuration section. Safe to call with no database files present — enrichment
    /// degrades to null fields.</summary>
    public static IServiceCollection AddIpEnrichment(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<IpEnrichmentOptions>(config.GetSection("IpEnrichment"));
        services.AddSingleton<IpEnrichmentService>();
        services.AddSingleton<IIpEnrichmentService>(sp => sp.GetRequiredService<IpEnrichmentService>());
        services.AddHostedService<GeoDataRefreshService>();
        return services;
    }
}
