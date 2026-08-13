using Microsoft.Data.SqlClient;

namespace TelemetryGuard.Data;

/// <summary>
/// The ONLY way request-path code (repositories) obtains a SQL connection. Every
/// connection is opened and stamped with SESSION_CONTEXT(N'TenantId') = the ambient
/// tenant from ITenantContext, so RLS scopes every statement on it. Callers dispose
/// the connection per unit of work (`await using`); pooling makes this cheap, and
/// sp_reset_connection clears the session context on pool reuse.
/// </summary>
public interface ITenantConnectionFactory
{
    Task<SqlConnection> OpenAsync(CancellationToken ct);
}
