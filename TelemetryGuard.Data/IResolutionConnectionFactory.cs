using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace TelemetryGuard.Data;

/// <summary>
/// UNSCOPED connection factory — no session context is stamped, therefore every
/// RLS-protected table returns ZERO rows on these connections. Usable ONLY to read
/// the RLS-exempt resolution tables dbo.ApiKeys and dbo.Sites during tenant
/// resolution (DAT-04). NEVER inject this into a repository.
/// </summary>
public interface IResolutionConnectionFactory
{
    Task<SqlConnection> OpenAsync(CancellationToken ct);
}

internal sealed class ResolutionConnectionFactory(IConfiguration cfg) : IResolutionConnectionFactory
{
    public async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(SqlConnectionStrings.Main(cfg));
        await conn.OpenAsync(ct);
        return conn;
    }
}
