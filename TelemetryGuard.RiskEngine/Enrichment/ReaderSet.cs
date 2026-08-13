using MaxMind.GeoIP2;

namespace TelemetryGuard.RiskEngine.Enrichment;

/// <summary>Immutable holder for the currently open database readers, enabling an
/// atomic swap when the weekly refresh script replaces files on disk. Mtimes record
/// what was on disk at load time so the refresh service can detect changes.</summary>
internal sealed class ReaderSet : IDisposable
{
    public DatabaseReader? City { get; init; }     // opened with MaxMind.Db.FileAccessMode.MemoryMapped
    public DatabaseReader? Asn { get; init; }
    public IP2Proxy.Component? Proxy { get; init; }
    public DateTime CityMtimeUtc { get; init; }
    public DateTime AsnMtimeUtc { get; init; }
    public DateTime ProxyMtimeUtc { get; init; }

    public void Dispose()
    {
        City?.Dispose();
        Asn?.Dispose();
        Proxy?.Close(); // IP2Proxy.Component exposes Close(), not IDisposable
    }
}
