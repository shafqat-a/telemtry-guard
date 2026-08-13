using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using TelemetryGuard.Core.Tenancy;

namespace TelemetryGuard.Data;

public sealed class TenantConnectionFactory(ITenantContext tenant, IConfiguration cfg)
    : ITenantConnectionFactory
{
    public async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var tid = tenant.TenantId.Value;
        if (tid == Guid.Empty)
            throw new InvalidOperationException(
                "Tenant context is not resolved; refusing to open a tenant-scoped connection.");
        if (tid == WellKnownTenants.System)
            throw new InvalidOperationException(
                "The SYSTEM sentinel may only be stamped via ISystemConnectionFactory.");

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
