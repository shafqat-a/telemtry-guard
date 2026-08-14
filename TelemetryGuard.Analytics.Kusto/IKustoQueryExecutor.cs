using System.Data;
using Kusto.Data.Common;

namespace TelemetryGuard.Analytics.Kusto;

/// <summary>
/// The ONLY type in the solution that talks to a Kusto engine endpoint. KQL text
/// is always supplied by callers inside this assembly (D7: no cross-engine query
/// layer, no query strings crossing an assembly boundary).
/// </summary>
public interface IKustoQueryExecutor
{
    Task<IDataReader> ExecuteQueryAsync(string kql, ClientRequestProperties properties, CancellationToken ct);
    Task ExecuteControlCommandAsync(string command, CancellationToken ct);

    /// <summary>`print 1` round-trip for the API-01 readiness probe.</summary>
    Task<bool> PingAsync(CancellationToken ct);
}
