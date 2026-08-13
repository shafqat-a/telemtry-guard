namespace TelemetryGuard.Data.Models;

public sealed record VerdictDailySummaryRow(
    Guid TenantId, Guid CampaignId, DateOnly Date,
    int Allowed, int Challenged, int Blocked, long ScoreSum, int Events);

public sealed record FlaggedSourceDailyRow(
    Guid TenantId, DateOnly Date, string SourceType, string Value,
    int FlaggedCount, int BlockedCount, long ScoreSum);
