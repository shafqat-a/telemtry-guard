namespace TelemetryGuard.Data.Models;

public sealed record WhitelistEntry(
    long Id, Guid TenantId, string SourceType, string Value, string? Reason,
    string Source, string? CreatedBy, DateTime CreatedUtc, DateTime? ExpiresUtc);

/// <summary>Input for IWhitelistRepository.AddAsync (Id/CreatedUtc are DB-assigned).
/// SessionId: the session the review screen marked as "real customer" — used only
/// for the D19 negative training label (labels join raw events by session id);
/// not persisted to dbo.WhitelistEntries.</summary>
public sealed record NewWhitelistEntry(
    string SourceType, string Value, string? Reason, string Source,
    string? CreatedBy, DateTime? ExpiresUtc, string? SessionId = null);
