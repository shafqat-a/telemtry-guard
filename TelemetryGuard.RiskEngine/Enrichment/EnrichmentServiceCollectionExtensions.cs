using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TelemetryGuard.RiskEngine.Enrichment.Providers;

namespace TelemetryGuard.RiskEngine.Enrichment;

public static class EnrichmentServiceCollectionExtensions
{
    /// <summary>Registers the configured IP-intelligence provider (D24),
    /// IIpEnrichmentService (singleton, memory-mapped lookups) and the background refresh
    /// service. Binds IpEnrichmentOptions from the "IpEnrichment" configuration section.
    /// Safe to call with no database files present — enrichment degrades to null fields.
    ///
    /// "IpEnrichment:Provider" selects the dataset ("Iplegence" default, "MaxMind");
    /// an unrecognized value throws here, at composition time, so a typo fails startup
    /// instead of silently enriching nothing.</summary>
    public static IServiceCollection AddIpEnrichment(this IServiceCollection services, IConfiguration config)
    {
        var section = config.GetSection(IpEnrichmentOptions.SectionName);
        services.Configure<IpEnrichmentOptions>(section);

        var provider = section[nameof(IpEnrichmentOptions.Provider)];
        if (string.IsNullOrWhiteSpace(provider))
            provider = IpIntelligenceProviders.Iplegence;

        if (provider.Equals(IpIntelligenceProviders.Iplegence, StringComparison.OrdinalIgnoreCase))
            services.TryAddSingleton<IIpIntelligenceProvider, IplegenceIpIntelligenceProvider>();
        else if (provider.Equals(IpIntelligenceProviders.MaxMind, StringComparison.OrdinalIgnoreCase))
            services.TryAddSingleton<IIpIntelligenceProvider, MaxMindIpIntelligenceProvider>();
        else
            throw new InvalidOperationException(
                $"Unknown IP intelligence provider '{provider}' — expected "
                + $"'{IpIntelligenceProviders.Iplegence}' or '{IpIntelligenceProviders.MaxMind}' "
                + $"in {IpEnrichmentOptions.SectionName}:{nameof(IpEnrichmentOptions.Provider)}.");

        services.AddSingleton<IpEnrichmentService>();
        services.AddSingleton<IIpEnrichmentService>(sp => sp.GetRequiredService<IpEnrichmentService>());
        services.AddHostedService<GeoDataRefreshService>();
        return services;
    }
}
