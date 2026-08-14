using System.Security.Cryptography;
using System.Text;
using TelemetryGuard.Api.Auth;
using TelemetryGuard.Data.Repositories;

namespace TelemetryGuard.Api.Endpoints;

/// <summary>
/// INT-02: the `/admin/enforcement` route group (spec D21/D19/§6.3) — the
/// tenant-facing half of the exclusion-queue approval flow. API-06 already wrote
/// dbo.ExclusionQueue rows with Status chosen by the tenant's EnforcementMode
/// (AutoEnforce -&gt; 'approved' immediately, ApprovalQueue -&gt; 'pending'); these
/// endpoints complete the pending -&gt; approved | rejected transition. The sync
/// workers INT-03/INT-04 own approved -&gt; pushed | failed | unsupported — nothing
/// here ever pushes to an ad platform.
///
/// Gated by <see cref="AdminScopeFilter"/>, hung off the same `/admin` prefix
/// AdminEndpoints (API-07) uses — DAT-04's tenant-resolution middleware
/// guarantees only X-Api-Key-resolved requests ever reach an /admin/* handler.
///
/// Untouchable invariant (D21): the 31-70 mid-band prompt is ALWAYS automatic in
/// both enforcement modes. API-06 only ever enqueues BLOCK-band (71-100) rows
/// into dbo.ExclusionQueue, so nothing reachable from this file queues, gates, or
/// delays that mid-band prompt — there is no such code path here at all.
/// </summary>
public static class EnforcementAdminEndpoints
{
    private const string ApiKeyHeader = "X-Api-Key";
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;
    private const int MaxBatchSize = 500;
    private const int MaxNoteLength = 400;

    public static IEndpointRouteBuilder MapEnforcementAdminEndpoints(this IEndpointRouteBuilder app)
    {
        // Hung off the SAME "/admin" prefix AdminEndpoints (API-07) uses, gated by
        // the same AdminScopeFilter backstop. Nested "/enforcement" subgroup so the
        // list route can use MapGet("", ...) — MapGet("/", ...) on a group maps only
        // the trailing-slash variant, not the bare "/admin/enforcement" the spec calls for.
        var admin = app.MapGroup("/admin").AddEndpointFilter<AdminScopeFilter>();
        var group = admin.MapGroup("/enforcement");

        group.MapGet("", ListAsync);
        group.MapPost("/approve", ApproveAsync);
        group.MapPost("/reject", RejectAsync);
        return app;
    }

    // -------------------------------------------------------------- list --

    private static async Task<IResult> ListAsync(
        string? status, int? limit, IEnforcementQueueRepository repo, CancellationToken ct)
    {
        if (!TryParseStatus(status, out var resolvedStatus))
        {
            return ValidationProblem(
                "status", $"status must be one of: {string.Join(", ", ExclusionStatuses.All)}.");
        }

        var effectiveLimit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        var entries = await repo.ListAsync(resolvedStatus, effectiveLimit, ct);
        return Results.Ok(entries.Select(ToResponse));
    }

    // ----------------------------------------------------------- approve --

    private static async Task<IResult> ApproveAsync(
        EnforcementBatchRequest req, HttpContext http, IEnforcementQueueRepository repo, CancellationToken ct)
    {
        if (!TryValidateIds(req.Ids, out var ids, out var idsProblem))
        {
            return idsProblem!;
        }

        var actorKeyHash = HashApiKey(http);
        var approved = await repo.ApproveAsync(ids, actorKeyHash, ct);
        return Results.Ok(new EnforcementApproveResponse(ids.Count, approved));
    }

    // ------------------------------------------------------------ reject --

    private static async Task<IResult> RejectAsync(
        EnforcementBatchRequest req, HttpContext http, IEnforcementQueueRepository repo, CancellationToken ct)
    {
        if (!TryValidateIds(req.Ids, out var ids, out var idsProblem))
        {
            return idsProblem!;
        }

        if (req.Note is { Length: > MaxNoteLength })
        {
            return ValidationProblem("note", $"note must be at most {MaxNoteLength} characters.");
        }

        var actorKeyHash = HashApiKey(http);
        var rejected = await repo.RejectAsync(ids, req.Note, actorKeyHash, ct);
        return Results.Ok(new EnforcementRejectResponse(ids.Count, rejected));
    }

    // ------------------------------------------------------------- helpers --

    private static bool TryParseStatus(string? raw, out string status)
    {
        var candidate = raw ?? ExclusionStatuses.Pending;
        var match = ExclusionStatuses.All.FirstOrDefault(
            s => string.Equals(s, candidate, StringComparison.OrdinalIgnoreCase));
        status = match ?? candidate;
        return match is not null;
    }

    private static bool TryValidateIds(long[]? ids, out IReadOnlyList<long> validated, out IResult? problem)
    {
        if (ids is null || ids.Length == 0)
        {
            validated = [];
            problem = ValidationProblem("ids", "ids is required and must contain at least 1 entry.");
            return false;
        }

        if (ids.Length > MaxBatchSize)
        {
            validated = [];
            problem = ValidationProblem("ids", $"ids must contain at most {MaxBatchSize} entries.");
            return false;
        }

        validated = ids;
        problem = null;
        return true;
    }

    /// <summary>SHA-256 of the raw X-Api-Key header value — identical hashing to
    /// DAT-04's SqlTenantResolver.ResolveApiKeyAsync, so the value lines up with
    /// dbo.ApiKeys.KeyHash for any future join. Header absent (should not happen
    /// behind AdminScopeFilter) -&gt; null, recorded as a system action.</summary>
    private static byte[]? HashApiKey(HttpContext http)
        => http.Request.Headers.TryGetValue(ApiKeyHeader, out var values) && values.Count > 0
            ? SHA256.HashData(Encoding.UTF8.GetBytes(values.ToString()))
            : null;

    private static ExclusionQueueEntryResponse ToResponse(ExclusionQueueEntry e)
        => new(e.Id, e.Platform, e.SourceType, e.Value, e.CampaignScope, e.Reason, e.Status, e.CreatedUtc, e.UpdatedUtc);

    private static IResult ValidationProblem(string field, string message)
        => Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
