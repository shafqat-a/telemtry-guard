namespace TelemetryGuard.Api.Endpoints;

// API-07 request/response DTOs for the /admin route group. Serialized with the
// app's default (camelCase) System.Text.Json options.

/// <summary>POST /admin/whitelist body. All fields are strings so validation can
/// produce field-level ValidationProblem messages instead of a raw 400 from failed
/// model binding.</summary>
public sealed record AddWhitelistRequest(
    string? Type, string? Value, string? Reason, string? Source, string? SessionId);

public sealed record AddWhitelistResponse(long Id);

public sealed record WhitelistEntryResponse(
    long Id, string Type, string Value, string? Reason, string Source,
    string? CreatedBy, DateTime CreatedUtc, DateTime? ExpiresUtc);

public sealed record SummaryDayResponse(
    DateOnly Date, int Events, int Allowed, int Challenged, int Blocked, double? AvgScore);

public sealed record SummaryReportResponse(
    DateOnly From, DateOnly To, Guid CampaignId, IReadOnlyList<SummaryDayResponse> Rows);

public sealed record SiteIntegrationStatusResponse(
    string SiteKey, string Domain, string ConfiguredMode, DateTime? LastBeaconAt, string EffectiveLevel);

public sealed record IntegrationStatusReportResponse(IReadOnlyList<SiteIntegrationStatusResponse> Sites);
