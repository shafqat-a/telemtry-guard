using ClickHouse.Client.ADO;
using ClickHouse.Client.Utility;
using Microsoft.Extensions.Options;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Core.Time;

namespace TelemetryGuard.Analytics.ClickHouse;

/// <summary>
/// Intent-named reads over tg_events in native ClickHouse SQL (D7).
/// Tenant comes exclusively from ITenantContext (D11) — never a parameter.
/// Registered SCOPED (ANA-05) because ITenantContext is scoped.
/// All values reach the SQL through bound parameters; aggregate results honor
/// spec §7 null semantics (missing != zero: empty-scope averages are NaN).
/// </summary>
public sealed class ClickHouseAnalyticsQueries(
    ITenantContext tenant,
    IOptions<ClickHouseAnalyticsOptions> options,
    IClock clock) : IAnalyticsQueries
{
    private readonly string _cs = options.Value.ConnectionString;

    public async Task<IpVelocityStats> GetIpVelocityAsync(string ip, TimeSpan window, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ip);

        var windowEnd = clock.UtcNow.UtcDateTime; // IClock.UtcNow is DateTimeOffset (FND-04)
        var windowStart = windowEnd - window;

        await using var conn = new ClickHouseConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT
                count()                           AS click_count,
                uniqExact(session_id)             AS distinct_sessions,
                uniqExact(user_agent)             AS distinct_user_agents,
                uniqExact(fingerprint_visitor_id) AS distinct_fingerprints,
                countIf(score >= 71)              AS flagged_count
            FROM tg_events
            WHERE tenant_id = {tenantId:UUID}
              AND ip = toIPv6({ip:String})
              AND timestamp >= {fromTs:DateTime64(3)}
              AND timestamp <  {toTs:DateTime64(3)}
            """;
        cmd.AddParameter("tenantId", tenant.TenantId.Value);
        cmd.AddParameter("ip", NormalizeIp(ip));
        cmd.AddParameter("fromTs", windowStart);
        cmd.AddParameter("toTs", windowEnd);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new IpVelocityStats(
            Ip: ip,
            Window: window,
            WindowEndUtc: windowEnd,
            ClickCount: Convert.ToInt64(r["click_count"]),
            DistinctSessions: Convert.ToInt64(r["distinct_sessions"]),
            DistinctUserAgents: Convert.ToInt64(r["distinct_user_agents"]),
            DistinctFingerprints: Convert.ToInt64(r["distinct_fingerprints"]),
            FlaggedCount: Convert.ToInt64(r["flagged_count"]));
    }

    public async Task<CampaignFraudReport> GetCampaignReportAsync(string campaignId, DateRange range, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(campaignId);

        await using var conn = new ClickHouseConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT
                toDate(timestamp)                                   AS day,
                count()                                             AS total_events,
                countIf(kind = 'verdict')                           AS scored_events,
                countIf(kind = 'verdict' AND band = 'allow')        AS allowed,
                countIf(kind = 'verdict' AND band = 'challenge')    AS challenged,
                countIf(kind = 'verdict' AND band = 'block')        AS blocked,
                sumIf(score, kind = 'verdict' AND isNotNull(score)) AS score_sum,
                avgIf(score, kind = 'verdict' AND isNotNull(score)) AS avg_score,
                countIf(kind = 'verdict' AND has_js_beacon = 0)     AS no_js_beacon
            FROM tg_events
            WHERE tenant_id = {tenantId:UUID}
              AND campaign_id = {campaignId:String}
              AND timestamp >= {fromTs:DateTime64(3)}
              AND timestamp <  {toTs:DateTime64(3)}
            GROUP BY day
            ORDER BY day
            """;
        cmd.AddParameter("tenantId", tenant.TenantId.Value);
        cmd.AddParameter("campaignId", campaignId);
        cmd.AddParameter("fromTs", range.FromUtc);
        cmd.AddParameter("toTs", range.ToUtc);

        var days = new List<CampaignDailyCounts>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                days.Add(new CampaignDailyCounts(
                    Day: DateOnly.FromDateTime(Convert.ToDateTime(r["day"])),
                    TotalEvents: Convert.ToInt64(r["total_events"]),
                    ScoredEvents: Convert.ToInt64(r["scored_events"]),
                    Allowed: Convert.ToInt64(r["allowed"]),
                    Challenged: Convert.ToInt64(r["challenged"]),
                    Blocked: Convert.ToInt64(r["blocked"]),
                    ScoreSum: ToInt64OrZero(r["score_sum"]),          // 0 when no scored rows (mergeable sum)
                    AvgScore: ToDoubleOrNaN(r["avg_score"]),          // NaN when no scored rows — never 0
                    NoJsBeaconCount: Convert.ToInt64(r["no_js_beacon"])));
            }
        }

        var totalScored = days.Sum(d => d.ScoredEvents);
        var totalScoreSum = days.Sum(d => d.ScoreSum);
        var avg = totalScored == 0 ? double.NaN : (double)totalScoreSum / totalScored;
        return new CampaignFraudReport(campaignId, range,
            days.Sum(d => d.TotalEvents), totalScored,
            days.Sum(d => d.Allowed), days.Sum(d => d.Challenged), days.Sum(d => d.Blocked),
            avg, days.Sum(d => d.NoJsBeaconCount), days);
    }

    public async Task<IReadOnlyList<FlaggedSource>> GetTopFlaggedSourcesAsync(DateRange range, int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var conn = new ClickHouseConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT
                IPv6NumToString(ip)                        AS source_value,
                countIf(band IN ('challenge', 'block'))    AS flagged_events,
                countIf(band = 'block')                    AS blocked_events,
                count()                                    AS total_events,
                sumIf(score, isNotNull(score))             AS score_sum,
                avgIf(score, isNotNull(score))             AS avg_score,
                min(timestamp)                             AS first_seen,
                max(timestamp)                             AS last_seen
            FROM tg_events
            WHERE tenant_id = {tenantId:UUID}
              AND kind = 'verdict'
              AND timestamp >= {fromTs:DateTime64(3)}
              AND timestamp <  {toTs:DateTime64(3)}
            GROUP BY ip
            HAVING flagged_events > 0
            ORDER BY blocked_events DESC, flagged_events DESC
            LIMIT {limit:Int32}
            """;
        cmd.AddParameter("tenantId", tenant.TenantId.Value);
        cmd.AddParameter("fromTs", range.FromUtc);
        cmd.AddParameter("toTs", range.ToUtc);
        cmd.AddParameter("limit", limit);

        var sources = new List<FlaggedSource>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            sources.Add(new FlaggedSource(
                SourceType: "ip",
                SourceValue: Convert.ToString(r["source_value"]) ?? "",
                FlaggedEvents: Convert.ToInt64(r["flagged_events"]),
                BlockedEvents: Convert.ToInt64(r["blocked_events"]),
                TotalEvents: Convert.ToInt64(r["total_events"]),
                ScoreSum: ToInt64OrZero(r["score_sum"]),
                AvgScore: ToDoubleOrNaN(r["avg_score"]),
                FirstSeenUtc: AsUtc(r["first_seen"]),
                LastSeenUtc: AsUtc(r["last_seen"])));
        }
        return sources;
    }

    /// <summary>IPv4 inputs must compare equal to stored IPv4-mapped IPv6 values —
    /// same normalization as the ANA-03 writer.</summary>
    private static string NormalizeIp(string ip) => ClickHouseEventSink.ToIpV6(ip).ToString();

    /// <summary>Nullable ClickHouse sums come back as DBNull over an empty scope; 0 is
    /// the correct mergeable value for a sum (unlike averages, which stay NaN).</summary>
    private static long ToInt64OrZero(object value) => value is DBNull ? 0L : Convert.ToInt64(value);

    /// <summary>Missing != zero (§7): empty-scope averages surface as NaN, never 0.</summary>
    private static double ToDoubleOrNaN(object value) => value is DBNull ? double.NaN : Convert.ToDouble(value);

    private static DateTime AsUtc(object value) =>
        DateTime.SpecifyKind(Convert.ToDateTime(value), DateTimeKind.Utc);
}
