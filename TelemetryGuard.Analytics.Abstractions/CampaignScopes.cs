namespace TelemetryGuard.Analytics.Abstractions;

/// <summary>Reserved analytics-query scopes that are not persisted campaign ids.</summary>
public static class CampaignScopes
{
    /// <summary>Select rows whose persisted campaign_id is empty.</summary>
    public const string Campaignless = "__tg_campaignless__";
}
