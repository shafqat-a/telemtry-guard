namespace TelemetryGuard.Integrations.GoogleAds;

/// <summary>
/// Bound from configuration section "GoogleAds" (see TelemetryGuard.Api/appsettings.json).
/// Consumed by the DI registration in <see cref="GoogleAdsServiceCollectionExtensions"/>
/// (the gateway) and by the hosted <c>TelemetryGuard.Api.Workers.GoogleAdsExclusionSyncService</c>
/// (the sync cadence / dry-run gate / batch sizing).
/// </summary>
public sealed class GoogleAdsOptions
{
    public const string SectionName = "GoogleAds";

    public string DeveloperToken { get; set; } = "";
    public string OAuthClientId { get; set; } = "";
    public string OAuthClientSecret { get; set; } = "";
    public string OAuthRefreshToken { get; set; } = "";

    /// <summary>MCC/manager account id (10 digits, no dashes); empty when tenants are accessed directly.</summary>
    public string LoginCustomerId { get; set; } = "";

    /// <summary>SAFETY: true by default. The worker only logs its plan until this is
    /// explicitly false AND credentials are present (see <see cref="EffectiveDryRun"/>).</summary>
    public bool DryRun { get; set; } = true;

    public int SyncIntervalMinutes { get; set; } = 15;
    public int MaxIpExclusionsPerCampaign { get; set; } = 500; // Google product cap (~500)
    public int MutateBatchSize { get; set; } = 500;            // operations per mutate request
    public int QueueBatchSize { get; set; } = 1000;            // queue rows fetched per tenant per cycle

    public bool CredentialsConfigured =>
        !string.IsNullOrEmpty(DeveloperToken) && !string.IsNullOrEmpty(OAuthRefreshToken)
        && !string.IsNullOrEmpty(OAuthClientId) && !string.IsNullOrEmpty(OAuthClientSecret);

    /// <summary>Dry-run unless explicitly disabled AND fully configured. A misconfigured
    /// fresh deployment (empty credentials, whatever DryRun says) must stay inert — never
    /// accidentally mutate a live ad account.</summary>
    public bool EffectiveDryRun => DryRun || !CredentialsConfigured;
}
