using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace TelemetryGuard.Data;

/// <summary>
/// Background-job connection factory. NEVER use in request-path code.
///
/// Per-tenant stamping pattern for background jobs (e.g. ANA-07 rollups):
/// <code>
/// await using var sys = await systemFactory.OpenSystemAsync(ct);
/// var tenantIds = await sys.QueryAsync&lt;Guid&gt;(
///     "SELECT TenantId FROM dbo.Tenants WHERE Status = 0");
/// foreach (var tid in tenantIds)
/// {
///     await using var conn = await systemFactory.OpenForTenantAsync(tid, ct);
///     // every read/write on `conn` is RLS-scoped to that one tenant
/// }
/// </code>
/// Alternative for reusing repositories in jobs: create a DI scope per tenant, set the
/// scoped TenantContext (FND-04) to that tenant, resolve repositories inside the scope.
/// </summary>
public interface ISystemConnectionFactory
{
    /// <summary>Stamped with the SYSTEM sentinel — sees ALL tenants' rows.
    /// Use only to enumerate tenants / cross-tenant admin reads.</summary>
    Task<SqlConnection> OpenSystemAsync(CancellationToken ct);

    /// <summary>Stamped for one explicit tenant — the per-tenant unit of work
    /// inside a background job.</summary>
    Task<SqlConnection> OpenForTenantAsync(Guid tenantId, CancellationToken ct);
}

internal sealed class SystemConnectionFactory(IConfiguration cfg) : ISystemConnectionFactory
{
    public Task<SqlConnection> OpenSystemAsync(CancellationToken ct)
        => OpenStampedAsync(WellKnownTenants.System, ct);

    public Task<SqlConnection> OpenForTenantAsync(Guid tenantId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty || tenantId == WellKnownTenants.System)
            throw new ArgumentOutOfRangeException(nameof(tenantId),
                "OpenForTenantAsync requires a real tenant id.");
        return OpenStampedAsync(tenantId, ct);
    }

    private async Task<SqlConnection> OpenStampedAsync(Guid tid, CancellationToken ct)
    {
        var conn = new SqlConnection(cfg.GetConnectionString("Main"));
        try
        {
            await conn.OpenAsync(ct);
            await conn.ExecuteAsync(
                "EXEC sp_set_session_context @key = N'TenantId', @value = @tid, @read_only = 1",
                new { tid });
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }
}
