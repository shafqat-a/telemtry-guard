namespace TelemetryGuard.Core.Tenancy;

/// <summary>
/// Set-once implementation of <see cref="ITenantContext"/>. Registered as a
/// scoped service (one instance per request/job scope) by the Api composition
/// root — this class itself is DI-framework-agnostic and NOT thread-safe:
/// a scope belongs to one logical operation.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    private TenantId _tenantId;
    private string? _siteKey;

    public bool IsResolved { get; private set; }

    public TenantId TenantId
        => IsResolved ? _tenantId : throw new TenantNotResolvedException();

    public string? SiteKey
        => IsResolved ? _siteKey : throw new TenantNotResolvedException();

    /// <summary>
    /// Resolves the tenant for this scope. May be called exactly once;
    /// a second call throws InvalidOperationException. An empty tenantId throws
    /// ArgumentException — resolution with a zero tenant is always a bug.
    /// </summary>
    public void Resolve(TenantId tenantId, string? siteKey = null)
    {
        if (IsResolved)
        {
            throw new InvalidOperationException(
                "Tenant context is already resolved for this scope; it cannot be reassigned.");
        }

        if (tenantId.IsEmpty)
        {
            throw new ArgumentException("TenantId must not be empty.", nameof(tenantId));
        }

        _tenantId = tenantId;
        _siteKey = siteKey;
        IsResolved = true;
    }
}
