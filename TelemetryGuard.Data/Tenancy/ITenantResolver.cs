namespace TelemetryGuard.Data.Tenancy;

/// <summary>Result of tenant resolution. Scopes is empty for site-key resolutions;
/// SiteKey/IntegrationMode are null for API-key resolutions.</summary>
public sealed record ResolvedTenant(Guid TenantId, string[] Scopes, string? SiteKey, string? IntegrationMode);

public interface ITenantResolver
{
    /// <summary>Resolve by raw API key (X-Api-Key header). Returns null when unknown/revoked.</summary>
    Task<ResolvedTenant?> ResolveApiKeyAsync(string apiKey, CancellationToken ct);

    /// <summary>Resolve by public site key (?k= query param or beacon body). Returns null when unknown.</summary>
    Task<ResolvedTenant?> ResolveSiteKeyAsync(string siteKey, CancellationToken ct);
}
