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

    bool IsResolved { get; }
}
