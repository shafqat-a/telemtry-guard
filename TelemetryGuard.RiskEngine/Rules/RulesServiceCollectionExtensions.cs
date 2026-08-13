using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TelemetryGuard.RiskEngine.Rules;

public static class RulesServiceCollectionExtensions
{
    /// <summary>Registers the deterministic T1 rule engine (singleton — pure and thread-safe)
    /// and binds <see cref="RulesOptions"/> from the "Rules" configuration section via
    /// IOptionsMonitor so unpinned thresholds are tunable without redeploy (D18).</summary>
    public static IServiceCollection AddT1Rules(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<RulesOptions>(config.GetSection("Rules"));
        services.AddSingleton<IT1RuleEngine, T1RuleEngine>();
        return services;
    }
}
