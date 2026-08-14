using System.Text;

namespace TelemetryGuard.Analytics.Kusto;

/// <summary>
/// Emulator/dev ingestion path (step-0 Q1 — see README.md): the Kusto emulator has
/// no DM/queued-ingestion service, and this provider's step-0 reconnaissance found
/// the Ingest SDK's <c>KustoIngestFactory.CreateStreamingIngestClient</c> unusable
/// against the <c>kustainer-linux</c> image (it reports
/// <c>BadRequest_StreamingIngestionPolicyNotEnabled</c> even with the table,
/// database AND cluster streaming-ingestion policies all explicitly enabled —
/// Q1-fallback applies). Instead this transport issues the payload through
/// <c>.ingest inline into table &lt;table&gt; with (format='multijson',
/// ingestionMappingReference='&lt;mapping&gt;') &lt;| &lt;payload&gt;</c> via
/// <see cref="IKustoQueryExecutor.ExecuteControlCommandAsync"/> — verified against
/// the running emulator during step 0 to ingest a JSON-array multijson payload
/// (the exact shape <c>KustoEventSink.WriteBatchJson</c> produces) with correct
/// NaN round-tripping (Q2 primary: a JSON string "NaN" reads back as a real NaN).
/// <remarks>
/// Emulator/dev only, NEVER production. A production deployment MUST use
/// <see cref="QueuedKustoIngestTransport"/> (<c>IngestMode=Queued</c>) — this
/// control-command path embeds the whole batch as KQL text, which does not scale
/// to production ingestion volumes or the DM service's queueing/backpressure.
/// </remarks>
/// </summary>
public sealed class StreamingKustoIngestTransport(IKustoQueryExecutor executor) : IKustoIngestTransport
{
    public async Task IngestAsync(Stream multiJson, string table, string mappingName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var reader = new StreamReader(multiJson, Encoding.UTF8, leaveOpen: true);
        var payload = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        var command =
            $".ingest inline into table {table} with (format='multijson', ingestionMappingReference='{mappingName}') <|\n{payload}";
        await executor.ExecuteControlCommandAsync(command, ct).ConfigureAwait(false);
    }
}
