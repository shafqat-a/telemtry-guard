namespace TelemetryGuard.Api.Endpoints;

// INT-02 request/response DTOs for the /admin/enforcement route group.
// Serialized with the app's default (camelCase) System.Text.Json options.

/// <summary>GET /admin/enforcement row shape. Includes Platform (not part of
/// DAT-06/API-06's original ExclusionQueueInsert contract) so tenants/operators
/// can distinguish e.g. Meta 'unsupported' rows (INT-04) at a glance.</summary>
public sealed record ExclusionQueueEntryResponse(
    long Id, string Platform, string SourceType, string Value,
    Guid? CampaignScope, string Reason, string Status,
    DateTime CreatedUtc, DateTime? UpdatedUtc);

/// <summary>POST /admin/enforcement/approve and /reject body. Ids are the
/// bigint dbo.ExclusionQueue.Id values (NOT a GUID — DAT-06 has no ExclusionId).
/// Note is only meaningful on /reject; ignored (must still be ≤400 chars if
/// present) on /approve.</summary>
public sealed record EnforcementBatchRequest(long[]? Ids, string? Note);

public sealed record EnforcementApproveResponse(int Requested, int Approved);

public sealed record EnforcementRejectResponse(int Requested, int Rejected);
