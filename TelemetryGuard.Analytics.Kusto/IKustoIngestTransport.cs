namespace TelemetryGuard.Analytics.Kusto;

/// <summary>
/// The seam that makes queued-vs-streaming ingestion a config choice (and makes
/// the Kusto emulator, which has no DM/queued-ingestion service, usable in tests).
/// </summary>
public interface IKustoIngestTransport
{
    /// <summary>Ingests one already-serialized multijson payload into <paramref name="table"/>
    /// using the named ingestion mapping. Eventual visibility only (D7).</summary>
    Task IngestAsync(Stream multiJson, string table, string mappingName, CancellationToken ct);
}
