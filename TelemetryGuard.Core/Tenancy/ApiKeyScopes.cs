namespace TelemetryGuard.Core.Tenancy;

/// <summary>
/// The scope vocabulary stored space-separated in <c>dbo.ApiKeys.Scopes</c> and carried
/// on <see cref="ITenantContext.Scopes"/> for API-key-resolved requests (API-07).
/// <list type="bullet">
///   <item><see cref="Admin"/> — every <c>/admin/*</c> route, including mutations
///   (policy, enforcement approvals, whitelist, labels).</item>
///   <item><see cref="Report"/> — read-only <c>/admin/*</c> routes (GET/HEAD): reports,
///   listings, verdict evidence. Never a mutation.</item>
///   <item><see cref="Ingest"/> — reserved for server-to-server ingestion; grants nothing
///   under <c>/admin</c>.</item>
/// </list>
/// Site-key resolutions carry no scopes at all.
/// </summary>
public static class ApiKeyScopes
{
    public const string Admin = "admin";
    public const string Report = "report";
    public const string Ingest = "ingest";

    public static readonly IReadOnlySet<string> Known =
        new HashSet<string>(StringComparer.Ordinal) { Admin, Report, Ingest };

    /// <summary>Scope required to reach an <c>/admin</c> route with the given HTTP method:
    /// reads accept <see cref="Admin"/> or <see cref="Report"/>, everything else needs
    /// <see cref="Admin"/>.</summary>
    public static bool Allows(IReadOnlyList<string> granted, bool isReadOnlyMethod)
    {
        for (var i = 0; i < granted.Count; i++)
        {
            if (string.Equals(granted[i], Admin, StringComparison.Ordinal)) return true;
            if (isReadOnlyMethod && string.Equals(granted[i], Report, StringComparison.Ordinal)) return true;
        }

        return false;
    }
}
