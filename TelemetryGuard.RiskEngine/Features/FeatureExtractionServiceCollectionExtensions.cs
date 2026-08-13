using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TelemetryGuard.RiskEngine.Features;

public static class FeatureExtractionServiceCollectionExtensions
{
    /// <summary>Registers the pure feature extractor (singleton — deterministic, thread-safe,
    /// synchronous per D3) and binds <see cref="FeatureExtractionOptions"/> from the
    /// "FeatureExtraction" configuration section via IOptionsMonitor so the tls_ua_mismatch
    /// gate can be flipped without redeploy once INT-05 lands (D13).</summary>
    public static IServiceCollection AddFeatureExtraction(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<FeatureExtractionOptions>(config.GetSection("FeatureExtraction"));
        services.AddSingleton<IFeatureExtractor, FeatureExtractor>();
        return services;
    }
}
