namespace TelemetryGuard.Analytics.Kusto;

/// <summary>
/// Options for the Kusto analytics provider (Azure Data Explorer AND Fabric
/// Eventhouse — D6: one provider, two targets, differing only in the connection
/// string). Bound to config section <c>Analytics:Kusto</c> by the composition-root
/// switch (P2-05 step 9). Sink tuning names mirror
/// <c>ClickHouseAnalyticsOptions</c> on purpose.
/// </summary>
public sealed class KustoAnalyticsOptions
{
    /// <summary>Engine endpoint KCSB, e.g. "Data Source=https://&lt;cluster&gt;.&lt;region&gt;.kusto.windows.net;Fed=true"
    /// or, for the local emulator, "Data Source=http://localhost:8080;Federated Security=False".
    /// NEVER logged — may carry an app key.</summary>
    public string ConnectionString { get; init; } = "";

    /// <summary>Data-management (ingest-) endpoint KCSB. Required only when
    /// <see cref="IngestMode"/> is "Queued". Deliberately explicit: no silent
    /// "ingest-" prefixing of the engine URI.</summary>
    public string IngestConnectionString { get; init; } = "";

    public string Database { get; init; } = "telemetry_guard";

    /// <summary>"Queued" (production default — D7's weak eventual guarantee) or
    /// "Streaming" (emulator/dev; the Kusto emulator has no DM service).</summary>
    public string IngestMode { get; init; } = "Queued";

    // event sink
    public int EventQueueCapacity { get; init; } = 100_000;
    public int EventMaxBatchSize { get; init; } = 5_000;
    public double EventMaxBatchAgeSeconds { get; init; } = 2.0;
    // label sink
    public int LabelQueueCapacity { get; init; } = 10_000;
    public int LabelMaxBatchSize { get; init; } = 500;
    public double LabelMaxBatchAgeSeconds { get; init; } = 5.0;
    // flush retry (both sinks)
    public int FlushMaxRetries { get; init; } = 3;
    public double FlushRetryBaseDelayMs { get; init; } = 200;
    public double ShutdownDrainTimeoutSeconds { get; init; } = 10;
    // queries
    public double QueryTimeoutSeconds { get; init; } = 30;
    // retention (D20 — see step 8)
    public bool RetentionSweepEnabled { get; init; } = true;
    public double RetentionSweepIntervalHours { get; init; } = 24;

    public const string QueuedMode = "Queued";
    public const string StreamingMode = "Streaming";
}
