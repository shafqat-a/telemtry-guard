using TelemetryGuard.Portal.Options;

namespace TelemetryGuard.Portal.Api;

/// <summary>Mirrors the AddTurnstileVerification / AddGoogleAdsGateway registration
/// precedent: a typed HttpClient bound to Portal:ApiBaseUrl / Portal:ApiTimeoutSeconds.</summary>
public static class PortalServiceCollectionExtensions
{
    public static IServiceCollection AddAdminApiClient(
        this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(PortalOptions.SectionName).Get<PortalOptions>() ?? new PortalOptions();
        services.AddHttpClient<IAdminApiClient, PortalApiClient>(c =>
        {
            c.BaseAddress = new Uri(options.ApiBaseUrl);
            c.Timeout = TimeSpan.FromSeconds(options.ApiTimeoutSeconds);
        });
        return services;
    }
}
