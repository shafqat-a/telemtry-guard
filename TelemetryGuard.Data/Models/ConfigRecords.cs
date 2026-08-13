namespace TelemetryGuard.Data.Models;

public sealed record TenantRecord(
    Guid TenantId, string Name, byte Status, int RetentionDays, byte EnforcementMode, DateTime CreatedUtc);

public sealed record SiteRecord(
    Guid TenantId, string SiteKey, string Domain, string IntegrationMode, DateTime CreatedUtc);

public sealed record CampaignRecord(
    Guid TenantId, Guid CampaignId, string Platform, string? ExternalCampaignId,
    string LandingUrl, string? GeoTargets, byte Status, DateTime CreatedUtc);

/// <summary>Hot-path projection for the /c redirect (API-02). Status: 0=Active.</summary>
public sealed record CampaignRedirect(string LandingUrl, byte Status);
