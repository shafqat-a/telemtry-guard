using Kusto.Data;
using Kusto.Data.Common;
using Kusto.Data.Ingestion;
using Kusto.Ingest;
using Microsoft.Extensions.Options;

namespace TelemetryGuard.Analytics.Kusto;

/// <summary>
/// Production ingestion path (D7's weak eventual guarantee): queues the payload
/// with the Data Management service, which relays it into the engine — typically
/// seconds of added latency, comfortably inside the ANA-06 5 s contract window
/// with <see cref="KustoQueuedIngestionProperties.FlushImmediately"/> set. The
/// Kusto emulator has no DM service, so this transport is never used against it
/// (see <see cref="StreamingKustoIngestTransport"/>).
/// </summary>
public sealed class QueuedKustoIngestTransport : IKustoIngestTransport, IDisposable
{
    private readonly KustoAnalyticsOptions _opts;
    private readonly Lazy<IKustoQueuedIngestClient> _client;

    public QueuedKustoIngestTransport(IOptions<KustoAnalyticsOptions> opts)
    {
        _opts = opts.Value;
        _client = new Lazy<IKustoQueuedIngestClient>(() =>
            KustoIngestFactory.CreateQueuedIngestClient(new KustoConnectionStringBuilder(_opts.IngestConnectionString)));
    }

    public async Task IngestAsync(Stream multiJson, string table, string mappingName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var props = new KustoQueuedIngestionProperties(_opts.Database, table)
        {
            Format = DataSourceFormat.multijson,
            IngestionMapping = new IngestionMapping
            {
                IngestionMappingReference = mappingName,
                IngestionMappingKind = IngestionMappingKind.Json
            },
            FlushImmediately = true, // seconds, not minutes — still "eventual" (D7)
            ReportLevel = IngestionReportLevel.FailuresOnly,
            ReportMethod = IngestionReportMethod.Queue
        };
        await _client.Value.IngestFromStreamAsync(multiJson, props, new StreamSourceOptions { LeaveOpen = true })
            .WaitAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_client.IsValueCreated) _client.Value.Dispose();
    }
}
