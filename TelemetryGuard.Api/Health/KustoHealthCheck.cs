using Microsoft.Extensions.Diagnostics.HealthChecks;
using TelemetryGuard.Analytics.Kusto;

namespace TelemetryGuard.Api.Health;

/// <summary>
/// Readiness check: a "print 1" round trip against the configured Kusto engine
/// endpoint (Analytics:Kusto:ConnectionString — ANA-05/P2-05 own that section,
/// there is deliberately NO separate top-level "Kusto" section).
/// Deliberately Kusto-specific (spec D7): no generic analytics-engine health
/// abstraction — see ClickHouseHealthCheck for the same reasoning on the other side.
/// </summary>
public sealed class KustoHealthCheck(IKustoQueryExecutor executor) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            return await executor.PingAsync(ct)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("kusto ping failed");
        }
        catch (Exception ex) { return HealthCheckResult.Unhealthy("kusto", ex); }
    }
}
