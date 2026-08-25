namespace TelemetryGuard.Data.Models;

public sealed record ConversionGoalRecord(
    Guid TenantId, Guid GoalId, string SiteKey, string Name, string TriggerType,
    string PagePathsJson, string? Selector, int? MinimumSeconds, bool IsPrimary,
    bool SendMarketIq, bool SendMeta, bool SendGoogleAds, bool SendGa4, bool SendTikTok,
    bool IsActive, DateTime CreatedUtc, DateTime UpdatedUtc);

public sealed record ConversionEventRecord(
    Guid TenantId, Guid EventId, Guid GoalId, string SiteKey, string? SessionId,
    string? VisitId, string? PageUrl, DateTime OccurredUtc, long? PageToConversionMs,
    bool Verified, string EvidenceSource, decimal? Value, string? Currency);
