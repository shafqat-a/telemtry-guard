using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClickHouse.Client.ADO;
using ClickHouse.Client.Utility;
using Microsoft.Extensions.Options;
using TelemetryGuard.Analytics.ClickHouse;
using TelemetryGuard.Api.Auth;
using TelemetryGuard.Core.Tenancy;

namespace TelemetryGuard.Api.Endpoints;

/// <summary>Controlled ClickHouse read-through for tenant analytics consumers.
/// This is deliberately not a general SQL tunnel: callers must provide the tenant
/// parameter, only one SELECT/WITH statement is accepted, and dangerous namespaces
/// and multi-statements are rejected before a connection is opened.</summary>
public static partial class ClickHouseQueryEndpoints
{
    public sealed record QueryRequest(string? Sql, int? MaxRows = null);

    [GeneratedRegex(@"\{tenantId:UUID\}", RegexOptions.CultureInvariant)]
    private static partial Regex TenantParameter();
    [GeneratedRegex(@"\b(INSERT|ALTER|CREATE|DROP|TRUNCATE|DELETE|UPDATE|OPTIMIZE|SYSTEM|GRANT|REVOKE|ATTACH|DETACH|RENAME|KILL|SET)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WriteKeyword();

    public static IEndpointRouteBuilder MapClickHouseQueryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/admin/analytics/clickhouse").AddEndpointFilter<AdminScopeFilter>()
            .MapPost("/query", QueryAsync);
        return app;
    }

    private static async Task<IResult> QueryAsync(
        QueryRequest request, TenantContext tenant, IOptions<ClickHouseAnalyticsOptions> options,
        ILoggerFactory logs, CancellationToken ct)
    {
        var sql = request.Sql?.Trim();
        if (string.IsNullOrWhiteSpace(sql)) return Results.ValidationProblem(new Dictionary<string,string[]> { ["sql"]=["SQL is required."] });
        if (sql.Length > 32_000) return Results.ValidationProblem(new Dictionary<string,string[]> { ["sql"]=["SQL must be at most 32,000 characters."] });
        if (sql.Contains(';') || sql.Contains("--", StringComparison.Ordinal) || sql.Contains("/*", StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Only one comment-free SELECT statement is allowed." });
        if (!(sql.StartsWith("select ", StringComparison.OrdinalIgnoreCase) || sql.StartsWith("with ", StringComparison.OrdinalIgnoreCase)))
            return Results.BadRequest(new { error = "Only SELECT or WITH queries are allowed." });
        if (!TenantParameter().IsMatch(sql))
            return Results.BadRequest(new { error = "Queries must include the {tenantId:UUID} parameter for tenant isolation." });
        if (WriteKeyword().IsMatch(sql))
            return Results.BadRequest(new { error = "Write, DDL, system, and privilege statements are not allowed." });
        if (Regex.IsMatch(sql, @"\b(system|information_schema|INFORMATION_SCHEMA)\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return Results.BadRequest(new { error = "System and metadata namespaces are not available." });

        var maxRows = Math.Clamp(request.MaxRows ?? 10_000, 1, 10_000);
        var cs = options.Value.ReadConnectionString;
        if (string.IsNullOrWhiteSpace(cs)) cs = options.Value.ConnectionString;
        await using var conn = new ClickHouseConnection(cs);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // readonly=1 is enforced per query even when an older deployment has not yet
        // provisioned the separate ReadConnectionString account.
        cmd.CommandText = $"SELECT * FROM ({sql}) AS tg_query LIMIT {maxRows} SETTINGS readonly=1, max_execution_time=30, max_result_rows={maxRows}";
        cmd.CommandTimeout = 30;
        cmd.AddParameter("tenantId", tenant.TenantId.Value);
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<Dictionary<string, object?>>(Math.Min(maxRows, 1024));
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.GetValue(i);
                row[columns[i]] = value is DBNull ? null : value switch
                {
                    DateTime dt => dt.ToUniversalTime(),
                    DateTimeOffset dto => dto,
                    _ => value,
                };
            }
            rows.Add(row);
        }
        logs.CreateLogger("TelemetryGuard.ClickHouseQuery").LogInformation(
            "ClickHouse read query completed for tenant {TenantId}: {RowCount} rows", tenant.TenantId, rows.Count);
        return Results.Ok(new { columns, rows, truncated = rows.Count >= maxRows });
    }
}
