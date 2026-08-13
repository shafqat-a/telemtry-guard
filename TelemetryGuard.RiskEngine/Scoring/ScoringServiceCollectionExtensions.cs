using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TelemetryGuard.RiskEngine.Contracts;

namespace TelemetryGuard.RiskEngine.Scoring;

public static class ScoringServiceCollectionExtensions
{
    /// <summary>Registers the MVP heuristic scorer (singleton — pure and thread-safe) as the
    /// <see cref="IScorer"/> implementation (D18: heuristic at launch, model swap later is a
    /// config change) and binds <see cref="HeuristicWeights"/> from the "Scoring:Heuristic"
    /// configuration section via IOptionsMonitor so every weight is tunable without redeploy.</summary>
    public static IServiceCollection AddHeuristicScorer(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<HeuristicWeights>(config.GetSection("Scoring:Heuristic"));
        services.AddSingleton<IScorer, HeuristicScorer>();
        return services;
    }
}
