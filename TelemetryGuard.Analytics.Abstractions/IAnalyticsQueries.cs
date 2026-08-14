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

    /// <summary>
    /// Top publisher placements per UTC day inside <paramref name="range"/>,
    /// ranked by ScoredEvents descending, capped at <paramref name="limitPerDay"/>
    /// buckets PER DAY (not per range).
    ///
    /// PROVIDER CONTRACT (binding on every implementation, incl. P2-05's Kusto):
    ///  1. Only kind = 'verdict' rows are counted.
    ///  2. A verdict's placement is the normalized publisher host of the EARLIEST
    ///     capture row (kind 'tracker' or 'pixel') sharing its tenant + session_id.
    ///     Normalized = host only, lowercased, leading "www." stripped.
    ///  3. The capture-row lookup must look back at least ONE HOUR before
    ///     range.FromUtc, because a session's tracker hit precedes its verdict by
    ///     the API-02 grace period and may fall on the previous day.
    ///  4. Verdicts with no such capture row, an empty/unparseable referrer, or a
    ///     host longer than 253 chars are OMITTED entirely (§7: missing != zero).
    /// Throws ArgumentOutOfRangeException when limitPerDay &lt;= 0.
    /// </summary>
    Task<IReadOnlyList<PlacementDailyCounts>> GetTopPlacementsDailyAsync(
        DateRange range, int limitPerDay, CancellationToken ct);

    /// <summary>
    /// Per-site (site_key), per-UTC-day counts over every event kind inside
    /// <paramref name="range"/>, ordered by day then site key. Rows with an empty
    /// site_key are omitted. This is the non-campaign traffic view (D23).
    /// </summary>
    Task<IReadOnlyList<SiteDailyCounts>> GetSiteDailyCountsAsync(
        DateRange range, CancellationToken ct);
}
