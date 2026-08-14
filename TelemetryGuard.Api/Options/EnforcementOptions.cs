namespace TelemetryGuard.Api.Options;

/// <summary>
/// Config surface for the "Enforcement" appsettings section (spec D21). Per-tenant
/// EnforcementMode (dbo.Tenants.EnforcementMode, DAT-02/DAT-05) is always the
/// authoritative source for a resolved tenant — API-06's finalizer reads it via
/// <c>TenantRecord.EnforcementMode</c> and never consults this option.
/// <see cref="DefaultMode"/> exists only as the documented fallback label for
/// contexts with no tenant row to read (e.g. provisioning tooling, docs); it is
/// not currently read on any request path.
/// </summary>
public sealed class EnforcementOptions
{
    public const string SectionName = "Enforcement";

    /// <summary>"AutoEnforce" or "ApprovalQueue" — mirrors dbo.Tenants.EnforcementMode's
    /// two values (0/1) as a string for readability in config.</summary>
    public string DefaultMode { get; set; } = "AutoEnforce";
}
