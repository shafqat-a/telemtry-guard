using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TelemetryGuard.Analytics.ClickHouse;
using TelemetryGuard.Analytics.Kusto;
using TelemetryGuard.Api.Health;
using TelemetryGuard.Api.Workers;

namespace TelemetryGuard.Api.Analytics;

public static class AnalyticsServiceCollectionExtensions
{
    /// <summary>
    /// The D7 provider switch (spec D6/D7), living in the composition root exactly as
    /// ANA-05 said it must once the second provider landed (P2-05): neither provider
    /// project references the other, and this is the ONLY place in the solution that
    /// branches on Analytics:Provider. Config shape:
    /// <c>"Analytics": { "Provider": "ClickHouse"|"Kusto", "ClickHouse": {...}, "Kusto": {...} }</c>
    /// Unknown or missing Analytics:Provider aborts startup — silent defaults hide
    /// misconfiguration. Each branch also owns its readiness check (deliberately
    /// engine-specific: there is no generic analytics-engine health abstraction).
    /// </summary>
    public static IServiceCollection AddTelemetryGuardAnalytics(
        this IServiceCollection s, IConfiguration cfg) =>
        cfg["Analytics:Provider"] switch
        {
            "ClickHouse" => s.AddClickHouseAnalytics(cfg)
                .AddHealthChecks().AddCheck<ClickHouseHealthCheck>("clickhouse", tags: ["ready"]).Services,

            // Kusto also owns D20 retention: Kusto has no per-row TTL, so the sweep
            // worker is part of the provider's contract, not an optional extra.
            "Kusto" => s.AddKustoAnalytics(cfg)
                .AddHealthChecks().AddCheck<KustoHealthCheck>("kusto", tags: ["ready"]).Services
                .AddHostedService<KustoRetentionService>(),

            var p => throw new InvalidOperationException($"Unknown analytics provider '{p}'")
        };
}
