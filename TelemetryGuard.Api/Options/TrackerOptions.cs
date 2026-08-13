namespace TelemetryGuard.Api.Options;

/// <summary>
/// Options for the click/redirect tracker endpoints (API-02 /c, API-03 /p.gif,
/// API-06 verdict finalizer). Bound from the "Tracker" section of appsettings
/// (section ownership documented in appsettings.json, API-01).
/// </summary>
public sealed class TrackerOptions
{
    /// <summary>Seconds after a tracker click within which a beacon may still
    /// arrive; after the deadline the session is scored on HTTP + velocity
    /// features alone (spec §6.1 step 4, consumed by API-06).</summary>
    public int GraceSeconds { get; init; } = 10;

    /// <summary>Session cookie name on the tracker domain (also the query param
    /// name is fixed as tg_sid on the redirect).</summary>
    public string SessionCookieName { get; init; } = "tg_sid";

    /// <summary>TTL of the in-memory campaign redirect cache.</summary>
    public int CampaignCacheSeconds { get; init; } = 60;

    /// <summary>TTL for the session cookie and the Redis click-context hash.</summary>
    public int SessionTtlSeconds { get; init; } = 1800;
}
