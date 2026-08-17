namespace TelemetryGuard.Api.Endpoints;

// API-07 request/response DTOs for the /admin route group. Serialized with the
// app's default (camelCase) System.Text.Json options.

/// <summary>REQ-02: the score histogram attached to every /admin/reports/* row.
/// Edges/Counts are parallel eleven-entry arrays — Counts[i] is the number of
/// scored events with score in [Edges[i], Edges[i] + BucketWidth), except the
/// last bucket, which is the exact count of score == 100. Any consumer (e.g.
/// MarketIQ's 30/60 bands) can recompute exact tier counts under its OWN
/// thresholds from these deciles without touching ClickHouse.</summary>
public sealed record ScoreHistogramResponse(
    int BucketWidth,
    IReadOnlyList<int> Edges,
    IReadOnlyList<int> Counts,
    long SumSq);

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
    DateOnly Date, int Events, int Allowed, int Challenged, int Blocked, double? AvgScore,
    ScoreHistogramResponse ScoreHistogram);

/// <summary>REQ-03: CampaignId is null when the request omitted campaignId — the
/// response then covers the tenant-wide daily summary (every campaign plus
/// campaign-less traffic) instead of one campaign.</summary>
public sealed record SummaryReportResponse(
    DateOnly From, DateOnly To, Guid? CampaignId, IReadOnlyList<SummaryDayResponse> Rows);

public sealed record SiteIntegrationStatusResponse(
    string SiteKey, string Domain, string ConfiguredMode, DateTime? LastBeaconAt, string EffectiveLevel);

public sealed record IntegrationStatusReportResponse(IReadOnlyList<SiteIntegrationStatusResponse> Sites);

/// <summary>GET /admin/campaigns row. dbo.Campaigns has no Name column; callers
/// label a campaign from Platform + ExternalCampaignId + CampaignId.
/// Status: 0=Active, 1=Paused, 2=Archived (DAT-02).</summary>
public sealed record CampaignSummaryResponse(
    Guid CampaignId, string Platform, string? ExternalCampaignId, byte Status);

public sealed record CampaignListResponse(IReadOnlyList<CampaignSummaryResponse> Campaigns);

/// <summary>GET /admin/reports/flagged-sources row (dbo.FlaggedSourcesDaily).
/// ScoreSum is returned RAW and no average is computed: the denominator (scored
/// events for that source) is not stored on this table, and §7's missing-≠-zero
/// rule forbids inventing one. Contrast SummaryDayResponse.AvgScore, where
/// Events IS the denominator.</summary>
public sealed record FlaggedSourceResponse(
    DateOnly Date, string SourceType, string Value,
    int FlaggedCount, int BlockedCount, long ScoreSum,
    ScoreHistogramResponse ScoreHistogram);

public sealed record FlaggedSourcesReportResponse(
    DateOnly From, DateOnly To, IReadOnlyList<FlaggedSourceResponse> Sources);

public sealed record PublisherReportRowResponse(
    string Placement, int Events, int Allowed, int Challenged, int Blocked,
    int Flagged,               // Challenged + Blocked
    double? FlaggedRatio,      // Flagged / Events; null when Events == 0 (missing != zero)
    double? AvgScore,          // ScoreSum / Events; null when Events == 0
    int NoJsBeaconCount,
    bool LowVolume,            // Events < LowVolumePlacementEvents — read the ratio with care
    DateOnly FirstDay, DateOnly LastDay,
    ScoreHistogramResponse ScoreHistogram);

public sealed record PublisherReportResponse(
    DateOnly From, DateOnly To, IReadOnlyList<PublisherReportRowResponse> Rows);

public sealed record SiteReportDayResponse(
    DateOnly Date, string SiteKey, int TotalEvents, int Events,
    int Allowed, int Challenged, int Blocked, double? AvgScore, int NoJsBeaconCount,
    ScoreHistogramResponse ScoreHistogram);

public sealed record SiteReportResponse(
    DateOnly From, DateOnly To, IReadOnlyList<SiteReportDayResponse> Rows);
