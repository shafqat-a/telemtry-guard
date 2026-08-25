using System.Text.Json;
using ClickHouse.Client.ADO;
using ClickHouse.Client.Utility;
using Microsoft.Extensions.Options;
using TelemetryGuard.Analytics.ClickHouse;
using TelemetryGuard.Api.Auth;
using TelemetryGuard.Core.Tenancy;

namespace TelemetryGuard.Api.Endpoints;

/// <summary>Semantic, SQL-free analytics reads. The allowlists are intentional: callers
/// can choose useful dimensions/measures without receiving arbitrary SQL privileges.</summary>
public static class SemanticAnalyticsEndpoints
{
    public sealed record AnalyticsTime(DateTimeOffset From, DateTimeOffset To, string? Timezone = null, string? Bucket = null);
    public sealed record AnalyticsFilter(string Field, string Operator, JsonElement Value);
    public sealed record AnalyticsOrder(string Field, string Direction = "desc");
    public sealed record AnalyticsQueryRequest(
        string[]? SiteKeys, AnalyticsTime Time, string[]? Dimensions, string[]? Metrics,
        AnalyticsFilter[]? Filters = null, AnalyticsOrder[]? OrderBy = null, int? Limit = null, int? Offset = null);

    private static readonly Dictionary<string,string> Dimensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["day"]="toDate(timestamp)", ["hour"]="toStartOfHour(timestamp)",
        ["utm_platform"]="utm_source", ["utm_source"]="utm_source",
        ["utm_medium"]="utm_medium", ["utm_campaign"]="utm_campaign",
        ["utm_campaign_id"]="utm_id", ["utm_content"]="utm_content",
        ["landing_path"]="landing_path", ["country"]="country",
        ["ip_type"]="asn_type", ["ga_status"]="ga_status",
        ["verdict_band"]="band", ["action"]="action"
    };
    private static readonly Dictionary<string,string> Metrics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["requests"]="count()", ["sessions"]="uniqExact(session_id)",
        ["page_views"]="count()", ["unique_visitors"]="uniqExact(ip)",
        ["avg_score"]="avgOrNull(score)", ["fraudulent_requests"]="countIf(score >= 70)",
        ["fraud_rate"]="if(count()=0,0,countIf(score >= 70)/count())",
        ["allowed_requests"]="countIf(action='allow')",
        ["challenged_requests"]="countIf(action='challenge')",
        ["blocked_requests"]="countIf(action='block')",
        ["ga_page_view_sent"]="countIf(ga_status IN ('page_view_sent','page_view_accepted'))",
        ["ga_page_view_accepted"]="countIf(ga_status='page_view_accepted')"
    };

    public static IEndpointRouteBuilder MapSemanticAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/admin/analytics").AddEndpointFilter<AdminScopeFilter>().MapPost("/query", QueryAsync);
        return app;
    }

    private static async Task<IResult> QueryAsync(
        AnalyticsQueryRequest request, TenantContext tenant, IOptions<ClickHouseAnalyticsOptions> options,
        CancellationToken ct)
    {
        if (request.Time is null || request.Time.To <= request.Time.From)
            return Results.BadRequest(new { error = "time.from and time.to are required and must be ordered." });
        if (request.Time.To - request.Time.From > TimeSpan.FromDays(366))
            return Results.BadRequest(new { error = "The maximum query range is 366 days." });
        var dimensions = (request.Dimensions ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var metrics = (request.Metrics ?? ["requests", "sessions"]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (dimensions.Length > 8 || dimensions.Any(d => !Dimensions.ContainsKey(d)))
            return Results.BadRequest(new { error = "One or more dimensions are not supported." });
        if (metrics.Length > 20 || metrics.Any(m => !Metrics.ContainsKey(m)))
            return Results.BadRequest(new { error = "One or more metrics are not supported." });
        var limit = Math.Clamp(request.Limit ?? 500, 1, 10_000);
        var offset = Math.Clamp(request.Offset ?? 0, 0, 100_000);
        var siteKeys = request.SiteKeys?.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToArray() ?? [];

        var where = new List<string> { "tenant_id = {tenantId:UUID}", "timestamp >= {fromTs:DateTime64(3)}", "timestamp < {toTs:DateTime64(3)}", "visit_id != ''" };
        var parameters = new List<(string Name, object Value)> { ("tenantId", tenant.TenantId.Value), ("fromTs", request.Time.From.UtcDateTime), ("toTs", request.Time.To.UtcDateTime) };
        if (siteKeys.Length > 0)
        {
            var names = new List<string>();
            for (var i = 0; i < siteKeys.Length; i++) { var n = $"site{i}"; names.Add($"{{{n}:String}}"); parameters.Add((n, siteKeys[i])); }
            where.Add($"site_key IN ({string.Join(',', names)})");
        }
        foreach (var filter in request.Filters ?? [])
        {
            if (!Dimensions.TryGetValue(filter.Field, out var field)) return Results.BadRequest(new { error = $"Filter field '{filter.Field}' is not supported." });
            var op = filter.Operator.ToLowerInvariant();
            if (op is not ("eq" or "neq" or "in" or "not_in" or "contains")) return Results.BadRequest(new { error = $"Filter operator '{filter.Operator}' is not supported." });
            var values = filter.Value.ValueKind == JsonValueKind.Array ? filter.Value.EnumerateArray().ToArray() : [filter.Value];
            if (values.Length == 0 || values.Length > 100) return Results.BadRequest(new { error = "Filters must contain 1-100 values." });
            var names = new List<string>();
            for (var i = 0; i < values.Length; i++) { var n = $"f{parameters.Count}_{i}"; names.Add($"{{{n}:String}}"); parameters.Add((n, values[i].GetString() ?? values[i].ToString())); }
            var expression = op switch { "eq" => $"{field} = {names[0]}", "neq" => $"{field} != {names[0]}", "in" => $"{field} IN ({string.Join(',', names)})", "not_in" => $"{field} NOT IN ({string.Join(',', names)})", _ => $"positionCaseInsensitive({field}, {names[0]}) > 0" };
            where.Add(expression);
        }

        var selectDimensions = dimensions.Select(d => $"{Dimensions[d]} AS `{d}`").ToArray();
        var selectMetrics = metrics.Select(m => $"{Metrics[m]} AS `{m}`").ToArray();
        var select = string.Join(", ", selectDimensions.Concat(selectMetrics));
        var group = dimensions.Length == 0 ? "" : $" GROUP BY {string.Join(',', dimensions.Select(d => $"`{d}`"))}";
        if (request.OrderBy is { Length: > 0 } && request.OrderBy.Any(o => !dimensions.Contains(o.Field, StringComparer.OrdinalIgnoreCase) && !metrics.Contains(o.Field, StringComparer.OrdinalIgnoreCase)))
            return Results.BadRequest(new { error = "orderBy fields must be selected dimensions or metrics." });
        var order = request.OrderBy is { Length: > 0 } ? " ORDER BY " + string.Join(',', request.OrderBy.Select(o => $"`{o.Field}` {(o.Direction.Equals("asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC")}".Trim())) : "";
        // LIMIT 1 BY collapses multiple beacon/verdict rows into one canonical visit,
        // preventing raw event multiplicity from inflating request/session metrics.
        var source = $"(SELECT * FROM telemetry_guard.tg_events WHERE {string.Join(" AND ", where)} ORDER BY timestamp DESC LIMIT 1 BY visit_id) AS v";
        var sql = $"SELECT {select} FROM {source}{group}{order} LIMIT {limit} OFFSET {offset} SETTINGS readonly=1, max_execution_time=30, max_result_rows={limit}";

        var cs = string.IsNullOrWhiteSpace(options.Value.ReadConnectionString) ? options.Value.ConnectionString : options.Value.ReadConnectionString;
        await using var conn = new ClickHouseConnection(cs); await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand(); cmd.CommandText = sql; cmd.CommandTimeout = 30;
        foreach (var p in parameters) cmd.AddParameter(p.Name, p.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<Dictionary<string,object?>>();
        while (await reader.ReadAsync(ct)) { var row = new Dictionary<string,object?>(); for (var i=0;i<reader.FieldCount;i++) row[columns[i]]=reader.IsDBNull(i)?null:reader.GetValue(i); rows.Add(row); }
        return Results.Ok(new { schema = new { dimensions, metrics }, columns, rows, limit, offset, truncated = rows.Count >= limit });
    }
}
