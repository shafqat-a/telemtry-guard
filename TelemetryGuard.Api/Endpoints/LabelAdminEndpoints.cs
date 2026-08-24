using System.Text.RegularExpressions;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Api.Auth;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Data.Repositories;

namespace TelemetryGuard.Api.Endpoints;

public static partial class LabelAdminEndpoints
{
    private const float MaxWeight = 100f;

    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex SidShape();

    public static IEndpointRouteBuilder MapLabelAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/admin").AddEndpointFilter<AdminScopeFilter>()
            .MapPost("/labels", PostAsync);
        return app;
    }

    private static async Task<IResult> PostAsync(
        LabelRequest request,
        IAnalyticsQueries queries,
        ILabelSubmissionRepository submissions,
        ILabelSink sink,
        ITenantContext tenant,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.SessionId is null || !SidShape().IsMatch(request.SessionId))
            errors["sessionId"] = ["Invalid session id."];
        if (request.Label is not (LabelValues.Fraud or LabelValues.Legit))
            errors["label"] = ["label must be fraud or legit."];
        if (request.Source != LabelSources.MarketIqReview)
            errors["source"] = ["source must be marketiq_review."];
        if (!float.IsFinite(request.Weight) || request.Weight <= 0 || request.Weight > MaxWeight)
            errors["weight"] = [$"weight must be finite, positive, and no greater than {MaxWeight}."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        if (await queries.GetVerdictEvidenceAsync(request.SessionId!, ct) is null)
            return Results.NotFound();

        var row = await submissions.UpsertAsync(request.SessionId!, request.Label!, request.Source!, request.Weight, ct);
        await sink.WriteAsync(new LabelEvent(tenant.TenantId, row.SessionId, row.Label, row.Source,
            row.UpdatedUtc, row.Weight), ct);
        await submissions.MarkDeliveredAsync(row.SessionId, row.Source, row.Version, ct);
        return Results.Accepted($"/admin/sessions/{row.SessionId}/verdict", new { row.SessionId, row.Label, row.Source, row.Weight, row.Version });
    }

    private sealed record LabelRequest(string? SessionId, string? Label, string? Source, float Weight = 1f);
}
