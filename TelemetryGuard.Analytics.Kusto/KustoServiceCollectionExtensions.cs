using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TelemetryGuard.Analytics.Abstractions;

namespace TelemetryGuard.Analytics.Kusto;

public static class KustoServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Kusto analytics provider. Called ONLY from the D7 provider
    /// switch in the composition root (TelemetryGuard.Api/Analytics/
    /// AnalyticsServiceCollectionExtensions.cs, P2-05 step 9) — this project never
    /// references TelemetryGuard.Analytics.ClickHouse, and vice versa.
    /// </summary>
    public static IServiceCollection AddKustoAnalytics(this IServiceCollection s, IConfiguration cfg)
    {
        s.AddOptions<KustoAnalyticsOptions>()
            .Bind(cfg.GetSection("Analytics:Kusto"))
            .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString),
                "Analytics:Kusto:ConnectionString is required when Analytics:Provider is 'Kusto'.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Database),
                "Analytics:Kusto:Database is required when Analytics:Provider is 'Kusto'.")
            .Validate(o => o.IngestMode is KustoAnalyticsOptions.QueuedMode or KustoAnalyticsOptions.StreamingMode,
                "Analytics:Kusto:IngestMode must be 'Queued' or 'Streaming'.")
            .Validate(o => o.IngestMode != KustoAnalyticsOptions.QueuedMode
                           || !string.IsNullOrWhiteSpace(o.IngestConnectionString),
                "Analytics:Kusto:IngestConnectionString is required when IngestMode is 'Queued'.")
            .Validate(o => o.EventQueueCapacity > 0 && o.EventMaxBatchSize > 0
                           && o.EventMaxBatchAgeSeconds > 0 && o.FlushMaxRetries >= 0,
                "Analytics:Kusto sink tuning values must be positive.")
            .ValidateOnStart();

        s.AddSingleton<IKustoQueryExecutor, KustoQueryExecutor>();
        s.AddSingleton<IKustoIngestTransport>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<KustoAnalyticsOptions>>();
            return o.Value.IngestMode == KustoAnalyticsOptions.StreamingMode
                ? new StreamingKustoIngestTransport(sp.GetRequiredService<IKustoQueryExecutor>())
                : new QueuedKustoIngestTransport(o);
        });
        s.AddSingleton<KustoRetentionSweeper>();

        // Concrete sinks are registered ONCE and forwarded so IEventSink/ILabelSink
        // and IHostedService resolve the SAME instance — two instances would split
        // the queue from the flusher (ANA-05 precedent).
        return s
            .AddSingleton<KustoEventSink>()
            .AddSingleton<IEventSink>(sp => sp.GetRequiredService<KustoEventSink>())
            .AddSingleton<IHostedService>(sp => sp.GetRequiredService<KustoEventSink>())
            .AddSingleton<KustoLabelSink>()
            .AddSingleton<ILabelSink>(sp => sp.GetRequiredService<KustoLabelSink>())
            .AddSingleton<IHostedService>(sp => sp.GetRequiredService<KustoLabelSink>())
            // Scoped (not singleton): consumes scoped ITenantContext (D11).
            .AddScoped<IAnalyticsQueries, KustoAnalyticsQueries>();
    }
}
