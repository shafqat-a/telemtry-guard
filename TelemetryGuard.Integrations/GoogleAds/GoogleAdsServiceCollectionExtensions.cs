using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TelemetryGuard.Integrations.GoogleAds;

/// <summary>
/// Registers GoogleAdsOptions + the real <see cref="IGoogleAdsGateway"/> only.
///
/// SEAM NOTE: unlike API-06's/ANA-07's hosted workers, the actual BackgroundService
/// (<c>GoogleAdsExclusionSyncService</c>) is NOT registered here — it lives in
/// TelemetryGuard.Api/Workers (house precedent: RollupService, VerdictFinalizerService),
/// not in TelemetryGuard.Integrations, because it needs Dapper + ISystemConnectionFactory
/// (TelemetryGuard.Data) which this project deliberately does not reference. Program.cs
/// calls both this method AND <c>builder.Services.AddHostedService&lt;GoogleAdsExclusionSyncService&gt;()</c>.
/// INT-04 (Meta) follows the same split for consistency.
/// </summary>
public static class GoogleAdsServiceCollectionExtensions
{
    public static IServiceCollection AddGoogleAdsGateway(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<GoogleAdsOptions>(configuration.GetSection(GoogleAdsOptions.SectionName));
        services.AddSingleton<IGoogleAdsGateway, GoogleAdsGateway>();
        return services;
    }
}
