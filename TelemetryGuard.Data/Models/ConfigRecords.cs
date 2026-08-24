namespace TelemetryGuard.Data.Models;

public sealed record TenantRecord
{
    public TenantRecord() { }

    public TenantRecord(
        Guid tenantId, string name, byte status, int retentionDays,
        byte enforcementMode, DateTime createdUtc)
    {
        TenantId = tenantId;
        Name = name;
        Status = status;
        RetentionDays = retentionDays;
        EnforcementMode = enforcementMode;
        CreatedUtc = createdUtc;
    }

    public Guid TenantId { get; init; }
    public string Name { get; init; } = string.Empty;
    public byte Status { get; init; }
    public int RetentionDays { get; init; }
    public byte EnforcementMode { get; init; }
    public DateTime CreatedUtc { get; init; }
    public byte? AllowMax { get; init; }
    public byte? ChallengeMax { get; init; }
    public bool? ObserveOnly { get; init; }
    public bool ExternalAuthority { get; init; }
    public DateTime? PolicyUpdatedUtc { get; init; }
}

public sealed record SiteRecord(
    Guid TenantId, string SiteKey, string Domain, string IntegrationMode, DateTime CreatedUtc,
    bool MarketIqEnabled = false, int? MarketIqCompanyId = null,
    string? MarketIqCollectUrl = null, string? MarketIqHealthUrl = null,
    string? MarketIqHealthTokenRef = null, string? MarketIqRelayKeyRef = null);

public sealed record CampaignRecord(
    Guid TenantId, Guid CampaignId, string Platform, string? ExternalCampaignId,
    string LandingUrl, string? GeoTargets, byte Status, DateTime CreatedUtc);

/// <summary>Hot-path projection for the /c redirect (API-02). Status: 0=Active.</summary>
public sealed record CampaignRedirect(string LandingUrl, byte Status);
