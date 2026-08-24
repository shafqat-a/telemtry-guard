namespace TelemetryGuard.Core.Tenancy;

/// <summary>
/// Ambient tenant identity for the current logical operation (one HTTP request,
/// one job execution). Populated exactly once by tenant resolution (DAT-04);
/// consumed by SQL session-context stamping (DAT-03), Redis key prefixing (RSK-03),
/// and analytics query scoping (ANA-04).
/// Reading TenantId or SiteKey before resolution throws TenantNotResolvedException.
/// </summary>
public interface ITenantContext
{
    TenantId TenantId { get; }

    /// <summary>The public site key the tenant embedded in their snippet/pixel,
    /// when resolution happened via site key; null when resolved via API key.</summary>
    string? SiteKey { get; }

    /// <summary>Scopes granted to the API key this request was resolved with
    /// (<see cref="ApiKeyScopes"/>); empty for site-key resolutions and background
    /// job scopes. Authorization on <c>/admin/*</c> is decided from this, never from
    /// "a key of this tenant exists" (API-07).</summary>
    IReadOnlyList<string> Scopes { get; }

    bool IsResolved { get; }
}
