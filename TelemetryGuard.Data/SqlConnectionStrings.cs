using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace TelemetryGuard.Data;

/// <summary>
/// The two SQL Server connection strings the data layer knows about (0013):
/// <list type="bullet">
///   <item><c>ConnectionStrings:Main</c> — the request path. Its login should be a member of
///   the <c>tg_app</c> role: DML only, and the SYSTEM sentinel is NOT a bypass for it.</item>
///   <item><c>ConnectionStrings:System</c> — background jobs and tooling that enumerate
///   tenants under the sentinel. Its login should be a member of <c>tg_system</c>. Falls
///   back to <c>Main</c> when absent, which is only correct while Main is a db_owner
///   (local dev before <c>dev-seed.sh</c>, the test harness).</item>
/// </list>
/// Both get an <c>Application Name</c> when the configured string has none, so
/// <c>sys.dm_exec_sessions.program_name</c> tells the API, the migrator and the
/// training CLI apart.
/// </summary>
public static class SqlConnectionStrings
{
    public const string MainKey = "Main";
    public const string SystemKey = "System";

    public static string Main(IConfiguration cfg)
        => WithApplicationName(cfg.GetConnectionString(MainKey)
            ?? throw new InvalidOperationException("ConnectionStrings:Main is not configured."));

    public static string System(IConfiguration cfg)
    {
        var system = cfg.GetConnectionString(SystemKey);
        return WithApplicationName(!string.IsNullOrWhiteSpace(system)
            ? system
            : cfg.GetConnectionString(MainKey)
              ?? throw new InvalidOperationException("ConnectionStrings:System (or Main) is not configured."));
    }

    private static string WithApplicationName(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (!string.IsNullOrEmpty(builder.ApplicationName)
            && !string.Equals(builder.ApplicationName, ".Net SqlClient Data Provider", StringComparison.Ordinal)
            && !string.Equals(builder.ApplicationName, "Core Microsoft SqlClient Data Provider", StringComparison.Ordinal))
        {
            return connectionString; // operator-chosen name wins
        }

        builder.ApplicationName = Assembly.GetEntryAssembly()?.GetName().Name ?? "TelemetryGuard";
        return builder.ConnectionString;
    }
}
