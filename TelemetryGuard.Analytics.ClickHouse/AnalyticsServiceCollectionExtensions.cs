using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TelemetryGuard.Analytics.Abstractions;

namespace TelemetryGuard.Analytics.ClickHouse;

public static class AnalyticsServiceCollectionExtensions
{
    /// <summary>
    /// D7 provider switch. Lives in the ClickHouse project while it is the only
    /// implemented provider; when Kusto (P2-05) lands, move this switch to the
    /// composition root so neither provider references the other.
    /// Expects config shape:
    /// <c>"Analytics": { "Provider": "ClickHouse", "ClickHouse": { "ConnectionString": ..., ... } }</c>
    /// where the ClickHouse section binds to <see cref="ClickHouseAnalyticsOptions"/>.
    /// Unknown or missing <c>Analytics:Provider</c> aborts startup — silent
    /// defaults hide misconfiguration.
    /// </summary>
    public static IServiceCollection AddTelemetryGuardAnalytics(
        this IServiceCollection s, IConfiguration cfg)
    {
        s.AddOptions<ClickHouseAnalyticsOptions>()
            .Bind(cfg.GetSection("Analytics:ClickHouse"))
            .Validate(o => cfg["Analytics:Provider"] != "ClickHouse"
                           || !string.IsNullOrWhiteSpace(o.ConnectionString),
                "Analytics:ClickHouse:ConnectionString is required when Analytics:Provider is 'ClickHouse'.")
            .Validate(o => o.EventQueueCapacity > 0 && o.EventMaxBatchSize > 0
                           && o.EventMaxBatchAgeSeconds > 0 && o.FlushMaxRetries >= 0,
                "Analytics:ClickHouse sink tuning values must be positive.")
            .ValidateOnStart();

        _ = cfg["Analytics:Provider"] switch
        {
            // Kusto is deferred (D6 / P2-05); fail fast rather than register nothing.
            "Kusto" => throw new NotSupportedException(
                "Analytics provider 'Kusto' is not implemented yet (spec D6, task P2-05). Use 'ClickHouse'."),

            // Concrete sinks are registered ONCE and forwarded so IEventSink/ILabelSink
            // and IHostedService resolve the SAME instance — two instances would split
            // the queue from the flusher.
            "ClickHouse" => s
                .AddSingleton<ClickHouseEventSink>()
                .AddSingleton<IEventSink>(sp => sp.GetRequiredService<ClickHouseEventSink>())
                .AddSingleton<IHostedService>(sp => sp.GetRequiredService<ClickHouseEventSink>())
                .AddSingleton<ClickHouseLabelSink>()
                .AddSingleton<ILabelSink>(sp => sp.GetRequiredService<ClickHouseLabelSink>())
                .AddSingleton<IHostedService>(sp => sp.GetRequiredService<ClickHouseLabelSink>())
                // Scoped (not singleton as in the D7 sketch): consumes scoped ITenantContext (D11).
                .AddScoped<IAnalyticsQueries, ClickHouseAnalyticsQueries>(),

            var p => throw new InvalidOperationException($"Unknown analytics provider '{p}'")
        };
        return s;
    }
}
