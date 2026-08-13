namespace TelemetryGuard.Data;

public static class WellKnownTenants
{
    /// <summary>
    /// SYSTEM sentinel tenant id ("all-zeros-1"). Allowed by rls.fn_tenantPredicate to
    /// see ALL tenants' rows. Only ISystemConnectionFactory may stamp it. It is never a
    /// real tenant (DAT-02 CHECK constraint) and must never be written into a tenant row.
    /// </summary>
    public static readonly Guid System = new("00000000-0000-0000-0000-000000000001");
}
