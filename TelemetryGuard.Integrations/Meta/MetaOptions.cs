namespace TelemetryGuard.Integrations.Meta;

/// <summary>
/// Bound from configuration section "Meta" (see TelemetryGuard.Api/appsettings.json).
/// Consumed by the DI registration in <see cref="MetaServiceCollectionExtensions"/>
/// (the typed client) and by the hosted <c>TelemetryGuard.Api.Workers.MetaExclusionSyncService</c>
/// (the sync cadence / dry-run gate).
/// </summary>
public sealed class MetaOptions
{
    public const string SectionName = "Meta";

    /// <summary>Version-pinned Graph API base. Bump deliberately; never call an unpinned URL.</summary>
    public string BaseUrl { get; set; } = "https://graph.facebook.com/v21.0";

    /// <summary>System-user access token (Business Settings → System users). Platform-level
    /// for MVP; per-tenant tokens are a later enhancement.</summary>
    public string SystemUserToken { get; set; } = "";

    public bool DryRun { get; set; } = true;      // same safety posture as INT-03
    public int SyncIntervalMinutes { get; set; } = 15;
    public int TimeoutSeconds { get; set; } = 10;
    public string BlockListNamePrefix { get; set; } = "TelemetryGuard exclusions";

    /// <summary>SAFETY: dry-run unless explicitly disabled AND a token is configured. A
    /// misconfigured fresh deployment (empty token, whatever DryRun says) must stay inert
    /// — never accidentally mutate a live Meta Business asset.</summary>
    public bool EffectiveDryRun => DryRun || string.IsNullOrEmpty(SystemUserToken);
}
