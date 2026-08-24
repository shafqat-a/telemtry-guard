using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Api.Auth;
using TelemetryGuard.RiskEngine.Contracts;

namespace TelemetryGuard.Api.Endpoints;

public static partial class VerdictEvidenceEndpoints
{
    private const int MaxRangeDays = 31;

    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex SidShape();

    public static IEndpointRouteBuilder MapVerdictEvidenceEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/admin").AddEndpointFilter<AdminScopeFilter>();
        admin.MapGet("/sessions/{sid}/verdict", GetOneAsync).RequireRateLimiting("verdict-export");
        admin.MapGet("/verdicts", GetPageAsync).RequireRateLimiting("verdict-export");
        return app;
    }

    private static async Task<IResult> GetOneAsync(string sid, IAnalyticsQueries queries, CancellationToken ct)
    {
        if (!SidShape().IsMatch(sid))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["sid"] = ["Invalid session id."] });
        var item = await queries.GetVerdictEvidenceAsync(sid, ct);
        return item is null ? Results.NotFound() : Results.Ok(Project(item));
    }

    private static async Task<IResult> GetPageAsync(
        DateTime? from, DateTime? to, string? cursor, int? limit,
        IAnalyticsQueries queries, CancellationToken ct)
    {
        var toUtc = EnsureUtc(to ?? DateTime.UtcNow);
        var fromUtc = EnsureUtc(from ?? toUtc.AddDays(-1));
        if (toUtc <= fromUtc || toUtc - fromUtc > TimeSpan.FromDays(MaxRangeDays))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["from"] = ["Range must be positive and no longer than 31 days."] });
        if (!TryDecodeCursor(cursor, out var decoded))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["cursor"] = ["Invalid cursor."] });

        var take = Math.Clamp(limit ?? 100, 1, 1000);
        var page = await queries.GetVerdictEvidencePageAsync(new DateRange(fromUtc, toUtc), decoded, take, ct);
        var next = page.HasMore && page.Items.Count > 0 ? EncodeCursor(page.Items[^1]) : null;
        return Results.Ok(new { items = page.Items.Select(Project), nextCursor = next });
    }

    private static object Project(VerdictEvidence value) => new
    {
        timestamp = value.TimestampUtc,
        sessionId = value.SessionId,
        value.Score,
        value.Band,
        value.Action,
        value.RuleHits,
        value.ScorerVersion,
        value.FeatureSetVersion,
        value.ShadowScore,
        value.ShadowScorerVersion,
        features = SanitizeFeatures(value.FeaturesJson),
    };

    private static JsonNode SanitizeFeatures(string json)
    {
        JsonNode node;
        try
        {
            var vector = JsonSerializer.Deserialize<FraudFeatureVector>(
                string.IsNullOrWhiteSpace(json) ? "{}" : json, FraudFeatureVectorJson.Options)
                ?? new FraudFeatureVector();
            var options = new JsonSerializerOptions(FraudFeatureVectorJson.Options)
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            };
            node = JsonSerializer.SerializeToNode(vector, options) ?? new JsonObject();
        }
        catch (JsonException) { return new JsonObject(); }
        ReplaceNonFinite(node);
        return node;
    }

    private static void ReplaceNonFinite(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(x => x.Key).ToArray())
            {
                if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s)
                    && s is "NaN" or "Infinity" or "-Infinity") obj[key] = null;
                else if (obj[key] is { } child) ReplaceNonFinite(child);
            }
        }
        else if (node is JsonArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
                if (arr[i] is { } child) ReplaceNonFinite(child);
        }
    }

    private static string EncodeCursor(VerdictEvidence value) => Convert.ToBase64String(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new CursorPayload(1, value.TimestampUtc, value.SessionId))));

    private static bool TryDecodeCursor(string? cursor, out VerdictCursor? value)
    {
        value = null;
        if (string.IsNullOrEmpty(cursor)) return true;
        try
        {
            var payload = JsonSerializer.Deserialize<CursorPayload>(Convert.FromBase64String(cursor));
            if (payload is null || payload.V != 1 || !SidShape().IsMatch(payload.Sid)) return false;
            value = new VerdictCursor(EnsureUtc(payload.Ts), payload.Sid);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return false; }
    }

    private static DateTime EnsureUtc(DateTime value) => value.Kind == DateTimeKind.Utc
        ? value : value.ToUniversalTime();

    private sealed record CursorPayload(int V, DateTime Ts, string Sid);
}
