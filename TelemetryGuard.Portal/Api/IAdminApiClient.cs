namespace TelemetryGuard.Portal.Api;

/// <summary>Intent-named client of the admin API (D7: never a generic query
/// passthrough) — one method per screen action.</summary>
public interface IAdminApiClient
{
    /// <summary>Sign-in probe with an explicit key (no session yet): true iff
    /// GET /admin/campaigns returns 2xx.</summary>
    Task<bool> ValidateKeyAsync(string apiKey, CancellationToken ct);

    Task<AdminApiResult<CampaignListDto>> ListCampaignsAsync(CancellationToken ct);
    Task<AdminApiResult<SummaryReportDto>> GetSummaryAsync(Guid campaignId, DateOnly from, DateOnly to, CancellationToken ct);
    Task<AdminApiResult<IntegrationStatusReportDto>> GetIntegrationStatusAsync(CancellationToken ct);
    Task<AdminApiResult<FlaggedSourcesReportDto>> GetFlaggedSourcesAsync(DateOnly from, DateOnly to, int limit, CancellationToken ct);

    Task<AdminApiResult<IReadOnlyList<WhitelistEntryDto>>> ListWhitelistAsync(string? type, int offset, int limit, CancellationToken ct);
    Task<AdminApiResult<AddWhitelistResponseDto>> AddWhitelistAsync(AddWhitelistRequestDto request, CancellationToken ct);
    Task<AdminApiResult<NoBody>> DeleteWhitelistAsync(long id, CancellationToken ct);

    Task<AdminApiResult<IReadOnlyList<ExclusionQueueEntryDto>>> ListEnforcementAsync(string status, int limit, CancellationToken ct);
    Task<AdminApiResult<EnforcementApproveResponseDto>> ApproveEnforcementAsync(IReadOnlyList<long> ids, CancellationToken ct);
    Task<AdminApiResult<EnforcementRejectResponseDto>> RejectEnforcementAsync(IReadOnlyList<long> ids, string? note, CancellationToken ct);
}
