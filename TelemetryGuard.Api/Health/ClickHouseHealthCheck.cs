using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TelemetryGuard.Api.Health;

/// <summary>
/// Readiness check: HTTP GET /ping against the host/port parsed out of
/// Analytics:ClickHouse:ConnectionString (ANA-03/ANA-05 own that section —
/// there is deliberately NO separate "ClickHouse" config section).
/// Deliberately ClickHouse-specific (spec D7): no generic analytics-engine
/// health abstraction.
/// </summary>
public sealed class ClickHouseHealthCheck(IHttpClientFactory http, IConfiguration cfg) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            // Parse "Host=...;Port=...;Database=...;..." (ClickHouse.Client format, ANA-03).
            var cs = cfg["Analytics:ClickHouse:ConnectionString"] ?? "";
            var parts = cs.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .Where(kv => kv.Length == 2)
                .ToDictionary(kv => kv[0].Trim(), kv => kv[1].Trim(), StringComparer.OrdinalIgnoreCase);
            var host = parts.GetValueOrDefault("Host", "localhost");
            var port = parts.GetValueOrDefault("Port", "8123");
            var resp = await http.CreateClient("clickhouse-health")
                .GetAsync($"http://{host}:{port}/ping", ct);
            return resp.IsSuccessStatusCode
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"clickhouse status {(int)resp.StatusCode}");
        }
        catch (Exception ex) { return HealthCheckResult.Unhealthy("clickhouse", ex); }
    }
}
