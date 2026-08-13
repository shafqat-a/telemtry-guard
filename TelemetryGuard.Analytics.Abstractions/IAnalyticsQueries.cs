namespace TelemetryGuard.Analytics.Abstractions;

/// <summary>
/// Intent-named read path over the analytics event store (spec D7). There is
/// deliberately NO tenant parameter on any method: implementations take the
/// tenant from the scoped <c>ITenantContext</c> (FND-04) and inject it into
/// every query (D11).
/// </summary>
public interface IAnalyticsQueries
{
    Task<IpVelocityStats> GetIpVelocityAsync(string ip, TimeSpan window, CancellationToken ct);
    Task<CampaignFraudReport> GetCampaignReportAsync(string campaignId, DateRange range, CancellationToken ct);
    Task<IReadOnlyList<FlaggedSource>> GetTopFlaggedSourcesAsync(DateRange range, int limit, CancellationToken ct);
}
