using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TelemetryGuard.Api.Health;

/// <summary>
/// Readiness check: opens ConnectionStrings:Main and runs SELECT 1.
/// This is the ONLY place in the API allowed to open a raw (non-tenant-stamped)
/// SqlConnection — it touches no tenant tables. Every repository call goes
/// through TenantConnectionFactory (DAT-03) instead.
/// </summary>
public sealed class SqlHealthCheck(IConfiguration cfg) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new Microsoft.Data.SqlClient.SqlConnection(
                cfg.GetConnectionString("Main"));
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) { return HealthCheckResult.Unhealthy("sql", ex); }
    }
}
