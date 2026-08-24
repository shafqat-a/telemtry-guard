using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Core.Time;

namespace TelemetryGuard.Tests.Contracts;

/// <summary>Trivial resolved-tenant stub (FND-04 contract) for building
/// tenant-scoped query instances in the contract suite.</summary>
public sealed class FixedTenantContext(TenantId tenantId, string? siteKey = null) : ITenantContext
{
    public TenantId TenantId { get; } = tenantId;
    public string? SiteKey { get; } = siteKey;
    public bool IsResolved => true;
    public IReadOnlyList<string> Scopes => Array.Empty<string>();
}

/// <summary>Trivial fixed-time stub (FND-04 contract) for deterministic
/// trailing windows in the contract suite.</summary>
public sealed class FixedClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;
}
