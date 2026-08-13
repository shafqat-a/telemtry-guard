using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TelemetryGuard.Integrations.Turnstile;

public static class TurnstileServiceCollectionExtensions
{
    public static IServiceCollection AddTurnstileVerification(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TurnstileOptions>(configuration.GetSection(TurnstileOptions.SectionName));
        services.AddHttpClient<ITurnstileVerifier, TurnstileVerifier>(c =>
        {
            // Per-attempt timeout is enforced inside the verifier via a linked CTS;
            // this outer timeout is a safety net only.
            c.Timeout = TimeSpan.FromSeconds(10);
        });
        return services;
    }
}
