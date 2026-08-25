namespace TelemetryGuard.Analytics.ClickHouse;

/// <summary>
/// Options for the ClickHouse analytics provider. Bound to config section
/// <c>Analytics:ClickHouse</c> by ANA-05.
/// </summary>
public sealed class ClickHouseAnalyticsOptions
{
    public string ConnectionString { get; init; } = "";
    /// <summary>Optional least-privilege connection used by the read/query API. When
    /// empty, the provider connection is used for backward-compatible deployments;
    /// production should set this to a ClickHouse user with SELECT-only grants.</summary>
    public string ReadConnectionString { get; init; } = "";
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
}
