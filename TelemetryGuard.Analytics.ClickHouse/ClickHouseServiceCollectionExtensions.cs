using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TelemetryGuard.Analytics.Abstractions;

namespace TelemetryGuard.Analytics.ClickHouse;

public static class ClickHouseServiceCollectionExtensions
{
    /// <summary>
    /// Registers the ClickHouse analytics provider. Called ONLY from the D7 provider
    /// switch in the composition root (TelemetryGuard.Api/Analytics/
    /// AnalyticsServiceCollectionExtensions.cs) — moved out of this project by P2-05
    /// so that neither provider references the other (ANA-05's own instruction).
    /// </summary>
    public static IServiceCollection AddClickHouseAnalytics(
        this IServiceCollection s, IConfiguration cfg)
    {
        s.AddOptions<ClickHouseAnalyticsOptions>()
            .Bind(cfg.GetSection("Analytics:ClickHouse"))
            .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString),
                "Analytics:ClickHouse:ConnectionString is required when Analytics:Provider is 'ClickHouse'.")
            .Validate(o => o.EventQueueCapacity > 0 && o.EventMaxBatchSize > 0
                           && o.EventMaxBatchAgeSeconds > 0 && o.FlushMaxRetries >= 0,
                "Analytics:ClickHouse sink tuning values must be positive.")
            .ValidateOnStart();

        // Concrete sinks are registered ONCE and forwarded so IEventSink/ILabelSink
        // and IHostedService resolve the SAME instance — two instances would split
        // the queue from the flusher.
        return s
            .AddSingleton<ClickHouseEventSink>()
            .AddSingleton<IEventSink>(sp => sp.GetRequiredService<ClickHouseEventSink>())
            .AddSingleton<IHostedService>(sp => sp.GetRequiredService<ClickHouseEventSink>())
            .AddSingleton<ClickHouseLabelSink>()
            .AddSingleton<ILabelSink>(sp => sp.GetRequiredService<ClickHouseLabelSink>())
            .AddSingleton<IHostedService>(sp => sp.GetRequiredService<ClickHouseLabelSink>())
            // Scoped (not singleton as in the D7 sketch): consumes scoped ITenantContext (D11).
            .AddScoped<IAnalyticsQueries, ClickHouseAnalyticsQueries>();
    }
}
