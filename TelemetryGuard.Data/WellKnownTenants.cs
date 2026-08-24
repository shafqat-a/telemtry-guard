namespace TelemetryGuard.Data;

public static class WellKnownTenants
{
    /// <summary>
    /// SYSTEM sentinel tenant id ("all-zeros-1"). rls.fn_tenantPredicate treats it as
    /// "all tenants" ONLY when the session's principal is a member of the tg_system role
    /// or db_owner (0013); on the request-path login (tg_app) it yields zero rows. Only
    /// ISystemConnectionFactory stamps it (the C# guard is defence in depth, no longer the
    /// boundary). It is never a real tenant (DAT-02 CHECK constraint) and must never be
    /// written into a tenant row.
    /// </summary>
    public static readonly Guid System = new("00000000-0000-0000-0000-000000000001");
}
