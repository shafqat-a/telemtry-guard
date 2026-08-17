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

    /// <summary>P2-01: a session's tracker/pixel hit precedes its verdict by the
    /// API-02 grace period (~10 s) and can land on the previous UTC day. One hour
    /// of lookbehind on the capture side is generous and keeps the scan bounded.</summary>
    private static readonly TimeSpan SessionJoinLookbehind = TimeSpan.FromHours(1);

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
        var storedCampaignId = campaignId == CampaignScopes.Campaignless ? "" : campaignId;

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
                sumIf(toInt64(score) * toInt64(score), kind = 'verdict' AND isNotNull(score)) AS score_sum_sq,
                countIf(kind = 'verdict' AND score BETWEEN 0 AND 9)    AS score_bucket_00,
                countIf(kind = 'verdict' AND score BETWEEN 10 AND 19) AS score_bucket_10,
                countIf(kind = 'verdict' AND score BETWEEN 20 AND 29) AS score_bucket_20,
                countIf(kind = 'verdict' AND score BETWEEN 30 AND 39) AS score_bucket_30,
                countIf(kind = 'verdict' AND score BETWEEN 40 AND 49) AS score_bucket_40,
                countIf(kind = 'verdict' AND score BETWEEN 50 AND 59) AS score_bucket_50,
                countIf(kind = 'verdict' AND score BETWEEN 60 AND 69) AS score_bucket_60,
                countIf(kind = 'verdict' AND score BETWEEN 70 AND 79) AS score_bucket_70,
                countIf(kind = 'verdict' AND score BETWEEN 80 AND 89) AS score_bucket_80,
                countIf(kind = 'verdict' AND score BETWEEN 90 AND 99) AS score_bucket_90,
                countIf(kind = 'verdict' AND score = 100)             AS score_bucket_100,
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
        cmd.AddParameter("campaignId", storedCampaignId);
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
                    NoJsBeaconCount: Convert.ToInt64(r["no_js_beacon"]))
                {
                    ScoreDistribution = ReadDistribution(r),
                });
            }
        }

        var totalScored = days.Sum(d => d.ScoredEvents);
        var totalScoreSum = days.Sum(d => d.ScoreSum);
        var avg = totalScored == 0 ? double.NaN : (double)totalScoreSum / totalScored;
        return new CampaignFraudReport(campaignId, range,
            days.Sum(d => d.TotalEvents), totalScored,
            days.Sum(d => d.Allowed), days.Sum(d => d.Challenged), days.Sum(d => d.Blocked),
            avg, days.Sum(d => d.NoJsBeaconCount), days)
        {
            ScoreDistribution = days.Aggregate(
                ScoreDistribution.Empty, (sum, day) => sum + day.ScoreDistribution),
        };
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
                sumIf(toInt64(score) * toInt64(score), band IN ('challenge', 'block') AND isNotNull(score)) AS score_sum_sq,
                countIf(band IN ('challenge', 'block') AND score BETWEEN 0 AND 9)    AS score_bucket_00,
                countIf(band IN ('challenge', 'block') AND score BETWEEN 10 AND 19) AS score_bucket_10,
                countIf(band IN ('challenge', 'block') AND score BETWEEN 20 AND 29) AS score_bucket_20,
                countIf(band IN ('challenge', 'block') AND score BETWEEN 30 AND 39) AS score_bucket_30,
                countIf(band IN ('challenge', 'block') AND score BETWEEN 40 AND 49) AS score_bucket_40,
                countIf(band IN ('challenge', 'block') AND score BETWEEN 50 AND 59) AS score_bucket_50,
                countIf(band IN ('challenge', 'block') AND score BETWEEN 60 AND 69) AS score_bucket_60,
                countIf(band IN ('challenge', 'block') AND score BETWEEN 70 AND 79) AS score_bucket_70,
                countIf(band IN ('challenge', 'block') AND score BETWEEN 80 AND 89) AS score_bucket_80,
                countIf(band IN ('challenge', 'block') AND score BETWEEN 90 AND 99) AS score_bucket_90,
                countIf(band IN ('challenge', 'block') AND score = 100)             AS score_bucket_100,
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
                LastSeenUtc: AsUtc(r["last_seen"]))
            {
                ScoreDistribution = ReadDistribution(r),
            });
        }
        return sources;
    }

    public async Task<IReadOnlyList<PlacementDailyCounts>> GetTopPlacementsDailyAsync(
        DateRange range, int limitPerDay, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limitPerDay);

        await using var conn = new ClickHouseConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // The inner subquery resolves ONE placement per session (argMin by timestamp
        // = the earliest capture row's referrer). ifNull keeps the expression a plain
        // String so domainWithoutWWW/lower never propagate Nullable into HAVING.
        // BOTH sides filter tenant_id, so the join can never cross tenants (D11).
        cmd.CommandText =
            """
            SELECT
                toDate(v.timestamp)                    AS day,
                p.placement                            AS placement,
                count()                                AS scored_events,
                countIf(v.band = 'allow')              AS allowed,
                countIf(v.band = 'challenge')          AS challenged,
                countIf(v.band = 'block')              AS blocked,
                sumIf(v.score, isNotNull(v.score))     AS score_sum,
                sumIf(toInt64(v.score) * toInt64(v.score), isNotNull(v.score)) AS score_sum_sq,
                countIf(v.score BETWEEN 0 AND 9)    AS score_bucket_00,
                countIf(v.score BETWEEN 10 AND 19) AS score_bucket_10,
                countIf(v.score BETWEEN 20 AND 29) AS score_bucket_20,
                countIf(v.score BETWEEN 30 AND 39) AS score_bucket_30,
                countIf(v.score BETWEEN 40 AND 49) AS score_bucket_40,
                countIf(v.score BETWEEN 50 AND 59) AS score_bucket_50,
                countIf(v.score BETWEEN 60 AND 69) AS score_bucket_60,
                countIf(v.score BETWEEN 70 AND 79) AS score_bucket_70,
                countIf(v.score BETWEEN 80 AND 89) AS score_bucket_80,
                countIf(v.score BETWEEN 90 AND 99) AS score_bucket_90,
                countIf(v.score = 100)             AS score_bucket_100,
                avgIf(v.score, isNotNull(v.score))     AS avg_score,
                countIf(v.has_js_beacon = 0)           AS no_js_beacon
            FROM tg_events AS v
            INNER JOIN
            (
                SELECT
                    session_id,
                    lower(domainWithoutWWW(argMin(ifNull(referrer, ''), timestamp))) AS placement
                FROM tg_events
                WHERE tenant_id = {tenantId:UUID}
                  AND kind IN ('tracker', 'pixel')
                  AND referrer IS NOT NULL
                  AND referrer != ''
                  AND timestamp >= {captureFromTs:DateTime64(3)}
                  AND timestamp <  {toTs:DateTime64(3)}
                GROUP BY session_id
                HAVING placement != '' AND length(placement) <= 253
            ) AS p ON v.session_id = p.session_id
            WHERE v.tenant_id = {tenantId:UUID}
              AND v.kind = 'verdict'
              AND v.timestamp >= {fromTs:DateTime64(3)}
              AND v.timestamp <  {toTs:DateTime64(3)}
            GROUP BY day, placement
            ORDER BY day ASC, scored_events DESC, placement ASC
            LIMIT {limitPerDay:Int32} BY day
            """;
        cmd.AddParameter("tenantId", tenant.TenantId.Value);
        cmd.AddParameter("captureFromTs", range.FromUtc - SessionJoinLookbehind);
        cmd.AddParameter("fromTs", range.FromUtc);
        cmd.AddParameter("toTs", range.ToUtc);
        cmd.AddParameter("limitPerDay", limitPerDay);

        var rows = new List<PlacementDailyCounts>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            rows.Add(new PlacementDailyCounts(
                Day: DateOnly.FromDateTime(Convert.ToDateTime(r["day"])),
                Placement: Convert.ToString(r["placement"]) ?? "",
                ScoredEvents: Convert.ToInt64(r["scored_events"]),
                Allowed: Convert.ToInt64(r["allowed"]),
                Challenged: Convert.ToInt64(r["challenged"]),
                Blocked: Convert.ToInt64(r["blocked"]),
                ScoreSum: ToInt64OrZero(r["score_sum"]),
                AvgScore: ToDoubleOrNaN(r["avg_score"]),
                NoJsBeaconCount: Convert.ToInt64(r["no_js_beacon"]))
            {
                ScoreDistribution = ReadDistribution(r),
            });
        }
        return rows;
    }

    public async Task<IReadOnlyList<SiteDailyCounts>> GetSiteDailyCountsAsync(
        DateRange range, CancellationToken ct)
    {
        await using var conn = new ClickHouseConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // Same conditional-aggregate shape as GetCampaignReportAsync, grouped on
        // site_key instead of campaign_id: TotalEvents spans every kind.
        cmd.CommandText =
            """
            SELECT
                toDate(timestamp)                                   AS day,
                site_key                                            AS site_key,
                count()                                             AS total_events,
                countIf(kind = 'verdict')                           AS scored_events,
                countIf(kind = 'verdict' AND band = 'allow')        AS allowed,
                countIf(kind = 'verdict' AND band = 'challenge')    AS challenged,
                countIf(kind = 'verdict' AND band = 'block')        AS blocked,
                sumIf(score, kind = 'verdict' AND isNotNull(score)) AS score_sum,
                sumIf(toInt64(score) * toInt64(score), kind = 'verdict' AND isNotNull(score)) AS score_sum_sq,
                countIf(kind = 'verdict' AND score BETWEEN 0 AND 9)    AS score_bucket_00,
                countIf(kind = 'verdict' AND score BETWEEN 10 AND 19) AS score_bucket_10,
                countIf(kind = 'verdict' AND score BETWEEN 20 AND 29) AS score_bucket_20,
                countIf(kind = 'verdict' AND score BETWEEN 30 AND 39) AS score_bucket_30,
                countIf(kind = 'verdict' AND score BETWEEN 40 AND 49) AS score_bucket_40,
                countIf(kind = 'verdict' AND score BETWEEN 50 AND 59) AS score_bucket_50,
                countIf(kind = 'verdict' AND score BETWEEN 60 AND 69) AS score_bucket_60,
                countIf(kind = 'verdict' AND score BETWEEN 70 AND 79) AS score_bucket_70,
                countIf(kind = 'verdict' AND score BETWEEN 80 AND 89) AS score_bucket_80,
                countIf(kind = 'verdict' AND score BETWEEN 90 AND 99) AS score_bucket_90,
                countIf(kind = 'verdict' AND score = 100)             AS score_bucket_100,
                avgIf(score, kind = 'verdict' AND isNotNull(score)) AS avg_score,
                countIf(kind = 'verdict' AND has_js_beacon = 0)     AS no_js_beacon
            FROM tg_events
            WHERE tenant_id = {tenantId:UUID}
              AND site_key != ''
              AND timestamp >= {fromTs:DateTime64(3)}
              AND timestamp <  {toTs:DateTime64(3)}
            GROUP BY day, site_key
            ORDER BY day ASC, site_key ASC
            """;
        cmd.AddParameter("tenantId", tenant.TenantId.Value);
        cmd.AddParameter("fromTs", range.FromUtc);
        cmd.AddParameter("toTs", range.ToUtc);

        var rows = new List<SiteDailyCounts>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            rows.Add(new SiteDailyCounts(
                Day: DateOnly.FromDateTime(Convert.ToDateTime(r["day"])),
                SiteKey: Convert.ToString(r["site_key"]) ?? "",
                TotalEvents: Convert.ToInt64(r["total_events"]),
                ScoredEvents: Convert.ToInt64(r["scored_events"]),
                Allowed: Convert.ToInt64(r["allowed"]),
                Challenged: Convert.ToInt64(r["challenged"]),
                Blocked: Convert.ToInt64(r["blocked"]),
                ScoreSum: ToInt64OrZero(r["score_sum"]),
                AvgScore: ToDoubleOrNaN(r["avg_score"]),
                NoJsBeaconCount: Convert.ToInt64(r["no_js_beacon"]))
            {
                ScoreDistribution = ReadDistribution(r),
            });
        }
        return rows;
    }

    /// <summary>IPv4 inputs must compare equal to stored IPv4-mapped IPv6 values —
    /// same normalization as the ANA-03 writer.</summary>
    private static string NormalizeIp(string ip) => ClickHouseEventSink.ToIpV6(ip).ToString();

    /// <summary>Nullable ClickHouse sums come back as DBNull over an empty scope; 0 is
    /// the correct mergeable value for a sum (unlike averages, which stay NaN).</summary>
    private static long ToInt64OrZero(object value) => value is DBNull ? 0L : Convert.ToInt64(value);

    /// <summary>Missing != zero (§7): empty-scope averages surface as NaN, never 0.</summary>
    private static double ToDoubleOrNaN(object value) => value is DBNull ? double.NaN : Convert.ToDouble(value);

    private static ScoreDistribution ReadDistribution(System.Data.Common.DbDataReader reader) =>
        ScoreDistribution.FromCounts(
            [
                Convert.ToInt64(reader["score_bucket_00"]),
                Convert.ToInt64(reader["score_bucket_10"]),
                Convert.ToInt64(reader["score_bucket_20"]),
                Convert.ToInt64(reader["score_bucket_30"]),
                Convert.ToInt64(reader["score_bucket_40"]),
                Convert.ToInt64(reader["score_bucket_50"]),
                Convert.ToInt64(reader["score_bucket_60"]),
                Convert.ToInt64(reader["score_bucket_70"]),
                Convert.ToInt64(reader["score_bucket_80"]),
                Convert.ToInt64(reader["score_bucket_90"]),
                Convert.ToInt64(reader["score_bucket_100"]),
            ],
            ToInt64OrZero(reader["score_sum_sq"]));

    private static DateTime AsUtc(object value) =>
        DateTime.SpecifyKind(Convert.ToDateTime(value), DateTimeKind.Utc);
}
