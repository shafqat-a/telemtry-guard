using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TelemetryGuard.RiskEngine.Contracts;
using TelemetryGuard.RiskEngine.Enrichment;
using TelemetryGuard.RiskEngine.Features;
using TelemetryGuard.RiskEngine.Rules;
using TelemetryGuard.RiskEngine.Scoring;
using TelemetryGuard.RiskEngine.Velocity;

namespace TelemetryGuard.RiskEngine.Pipeline;

public static class PipelineServiceCollectionExtensions
{
    /// <summary>
    /// Registers the full scoring pipeline (RSK-07): session-state reader, whitelist
    /// check (+ TryAdd no-op cache rebuilder — the API host overrides it with the DAT-07
    /// adapter), cached enrichment decoration, campaign provider (TryAdd null impl — the
    /// API host wires the DAT-05-backed one), and IScoringPipeline itself. Also applies
    /// the earlier AddIpEnrichment/AddVelocityStore/AddFeatureExtraction/AddT1Rules/
    /// AddHeuristicScorer registrations when the host has not already, guarded so
    /// double-registration is harmless and pre-registered fakes win (testability).
    /// </summary>
    public static IServiceCollection AddScoringPipeline(this IServiceCollection services, IConfiguration config)
    {
        // Enrichment: keep RSK-02's AddIpEnrichment, then override the interface
        // registration so the pipeline receives the memory-cached decorator over the
        // concrete service. A pre-registered fake IIpEnrichmentService (without the
        // concrete) is left untouched.
        if (!IsRegistered(services, typeof(IIpEnrichmentService))
            && !IsRegistered(services, typeof(IpEnrichmentService)))
        {
            services.AddIpEnrichment(config);
        }

        if (IsRegistered(services, typeof(IpEnrichmentService)))
        {
            services.Replace(ServiceDescriptor.Singleton<IIpEnrichmentService>(
                sp => new CachedIpEnrichmentService(sp.GetRequiredService<IpEnrichmentService>())));
        }

        if (!IsRegistered(services, typeof(IVelocityStore)))
        {
            services.AddVelocityStore(config);
        }

        if (!IsRegistered(services, typeof(IFeatureExtractor)))
        {
            services.AddFeatureExtraction(config);
        }

        if (!IsRegistered(services, typeof(IT1RuleEngine)))
        {
            services.AddT1Rules(config);
        }

        if (!IsRegistered(services, typeof(IScorer)))
        {
            services.AddHeuristicScorer(config);
        }

        services.TryAddScoped<ISessionStateStore, RedisSessionStateStore>();
        services.TryAddSingleton<IWhitelistCacheRebuilder, NoOpWhitelistCacheRebuilder>();
        services.TryAddScoped<IWhitelistCheck, RedisWhitelistCheck>();
        services.TryAddSingleton<ICampaignContextProvider, NullCampaignContextProvider>();
        services.TryAddScoped<IScoringPipeline, ScoringPipeline>();
        return services;
    }

    private static bool IsRegistered(IServiceCollection services, Type serviceType)
        => services.Any(d => d.ServiceType == serviceType);
}
