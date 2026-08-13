using TelemetryGuard.Data.Models;

namespace TelemetryGuard.Data.Repositories;

public interface ITenantRepository
{
    /// <summary>Row for the ambient tenant; null if missing (should not happen post-resolution).</summary>
    Task<TenantRecord?> GetCurrentAsync(CancellationToken ct);
    /// <summary>D20: throws ArgumentOutOfRangeException outside 30..180. Returns false when no row updated.</summary>
    Task<bool> UpdateRetentionDaysAsync(int retentionDays, CancellationToken ct);
    /// <summary>D21: 0=AutoEnforce, 1=ApprovalQueue; throws ArgumentOutOfRangeException otherwise.</summary>
    Task<bool> UpdateEnforcementModeAsync(byte enforcementMode, CancellationToken ct);
}

public interface ISiteRepository
{
    Task CreateAsync(SiteRecord site, CancellationToken ct);
    Task<SiteRecord?> GetBySiteKeyAsync(string siteKey, CancellationToken ct);
    Task<IReadOnlyList<SiteRecord>> ListAsync(CancellationToken ct);
    /// <summary>integrationMode must be "js" or "pixel" (D22); throws ArgumentException otherwise.</summary>
    Task<bool> UpdateAsync(string siteKey, string domain, string integrationMode, CancellationToken ct);
    Task<bool> DeleteAsync(string siteKey, CancellationToken ct);
}

public interface ICampaignRepository
{
    Task CreateAsync(CampaignRecord campaign, CancellationToken ct);
    Task<CampaignRecord?> GetAsync(Guid campaignId, CancellationToken ct);
    /// <summary>/c HOT PATH: single clustered-PK seek returning only LandingUrl + Status.
    /// The clustered PK (TenantId, CampaignId) covers this — do NOT add another index.</summary>
    Task<CampaignRedirect?> GetRedirectAsync(Guid campaignId, CancellationToken ct);
    Task<IReadOnlyList<CampaignRecord>> ListAsync(CancellationToken ct);
    Task<bool> UpdateAsync(CampaignRecord campaign, CancellationToken ct);
}
