namespace TelemetryGuard.Core.Tenancy;

/// <summary>
/// Thrown when tenant-scoped state is read before tenant resolution ran.
/// Indicates a pipeline-ordering bug (e.g. an endpoint executing before
/// TenantResolutionMiddleware) — never a user error.
/// </summary>
public sealed class TenantNotResolvedException : InvalidOperationException
{
    public TenantNotResolvedException()
        : base("Tenant has not been resolved for this scope. " +
               "TenantResolutionMiddleware (DAT-04) must run before any tenant-scoped work.")
    {
    }
}
