namespace TelemetryGuard.Portal.Api;

// MIRRORS of TelemetryGuard.Api/Endpoints/AdminModels.cs + EnforcementAdminModels.cs.
// Re-declared (not project-referenced) so the portal keeps zero project references
// (D23). PortalAdminApiContractTests round-trips every one of these against the real
// API test host — if a field name drifts, that test fails, not production.
public sealed record CampaignSummaryDto(Guid CampaignId, string Platform, string? ExternalCampaignId, byte Status);
public sealed record CampaignListDto(IReadOnlyList<CampaignSummaryDto> Campaigns);

public sealed record SummaryDayDto(DateOnly Date, int Events, int Allowed, int Challenged, int Blocked, double? AvgScore);
public sealed record SummaryReportDto(DateOnly From, DateOnly To, Guid CampaignId, IReadOnlyList<SummaryDayDto> Rows);

public sealed record SiteIntegrationStatusDto(
    string SiteKey, string Domain, string ConfiguredMode, DateTime? LastBeaconAt, string EffectiveLevel);
public sealed record IntegrationStatusReportDto(IReadOnlyList<SiteIntegrationStatusDto> Sites);

public sealed record FlaggedSourceDto(
    DateOnly Date, string SourceType, string Value, int FlaggedCount, int BlockedCount, long ScoreSum);
public sealed record FlaggedSourcesReportDto(DateOnly From, DateOnly To, IReadOnlyList<FlaggedSourceDto> Sources);

public sealed record WhitelistEntryDto(
    long Id, string Type, string Value, string? Reason, string Source,
    string? CreatedBy, DateTime CreatedUtc, DateTime? ExpiresUtc);
public sealed record AddWhitelistRequestDto(string Type, string Value, string? Reason, string Source, string? SessionId);
public sealed record AddWhitelistResponseDto(long Id);

public sealed record ExclusionQueueEntryDto(
    long Id, string Platform, string SourceType, string Value, Guid? CampaignScope,
    string Reason, string Status, DateTime CreatedUtc, DateTime? UpdatedUtc);
public sealed record EnforcementBatchRequestDto(long[] Ids, string? Note);
public sealed record EnforcementApproveResponseDto(int Requested, int Approved);
public sealed record EnforcementRejectResponseDto(int Requested, int Rejected);
