using Kusto.Data.Common;
using Microsoft.Extensions.Options;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Core.Analytics;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Core.Time;

namespace TelemetryGuard.Analytics.Kusto;

/// <summary>
/// Intent-named reads over tg_events in native KQL (D7). Tenant comes exclusively
/// from ITenantContext (D11) — never a parameter. Registered SCOPED (step 9)
/// because ITenantContext is scoped. Every value reaches KQL through a declared
/// query parameter — never string interpolation (the one narrow exception is
/// <c>limit</c>/<c>limitPerDay</c> when step 0's Q4 answer requires it; here Q4
/// came back "yes", so even those are bound). Aggregate results honor §7 null
/// semantics (missing != zero: empty-scope averages are NaN) and reproduce
/// ClickHouseAnalyticsQueries' arithmetic exactly, because ANA-06 asserts both.
/// </summary>
public sealed class KustoAnalyticsQueries(
    ITenantContext tenant,
    IOptions<KustoAnalyticsOptions> options,
    IClock clock,
    IKustoQueryExecutor executor) : IAnalyticsQueries
{
    /// <summary>P2-01: a session's tracker/pixel hit precedes its verdict by the
    /// API-02 grace period (~10 s) and can land on the previous UTC day. One hour
    /// of lookbehind on the capture side is generous and keeps the scan bounded —
    /// mirrors ClickHouseAnalyticsQueries.SessionJoinLookbehind exactly.</summary>
    private static readonly TimeSpan SessionJoinLookbehind = TimeSpan.FromHours(1);

    // Exposed as internal consts (not just inline strings) so
    // KustoAnalyticsQueriesKqlTests can assert the D7/D11 textual invariants
    // without a cluster.

    internal const string IpVelocityKql =
        """
        declare query_parameters(tenantId: guid, ipAddr: string, fromTs: datetime, toTs: datetime);
        tg_events
        | where tenant_id == tenantId
            and ip == ipAddr
            and timestamp >= fromTs
            and timestamp <  toTs
        | summarize
            click_count           = count(),
            distinct_sessions     = count_distinctif(session_id, isnotempty(session_id)),
            distinct_user_agents  = count_distinctif(user_agent, isnotempty(user_agent)),
            distinct_fingerprints = count_distinctif(fingerprint_visitor_id, isnotempty(fingerprint_visitor_id)),
            flagged_count         = countif(isnotnull(score) and score >= 71)
        """;

    internal const string CampaignReportKql =
        """
        declare query_parameters(tenantId: guid, campaign: string, fromTs: datetime, toTs: datetime);
        tg_events
        | where tenant_id == tenantId
            and campaign_id == campaign
            and timestamp >= fromTs
            and timestamp <  toTs
        | summarize
            total_events      = count(),
            scored_events     = countif(['kind'] == 'verdict'),
            allowed           = countif(['kind'] == 'verdict' and band == 'allow'),
            challenged        = countif(['kind'] == 'verdict' and band == 'challenge'),
            blocked           = countif(['kind'] == 'verdict' and band == 'block'),
            score_sum         = sumif(tolong(score), ['kind'] == 'verdict' and isnotnull(score)),
            scored_with_score = countif(['kind'] == 'verdict' and isnotnull(score)),
            no_js_beacon      = countif(['kind'] == 'verdict' and has_js_beacon == false),
            score_bucket_00   = countif(['kind'] == 'verdict' and score >= 0  and score < 10),
            score_bucket_10   = countif(['kind'] == 'verdict' and score >= 10 and score < 20),
            score_bucket_20   = countif(['kind'] == 'verdict' and score >= 20 and score < 30),
            score_bucket_30   = countif(['kind'] == 'verdict' and score >= 30 and score < 40),
            score_bucket_40   = countif(['kind'] == 'verdict' and score >= 40 and score < 50),
            score_bucket_50   = countif(['kind'] == 'verdict' and score >= 50 and score < 60),
            score_bucket_60   = countif(['kind'] == 'verdict' and score >= 60 and score < 70),
            score_bucket_70   = countif(['kind'] == 'verdict' and score >= 70 and score < 80),
            score_bucket_80   = countif(['kind'] == 'verdict' and score >= 80 and score < 90),
            score_bucket_90   = countif(['kind'] == 'verdict' and score >= 90 and score < 100),
            score_bucket_100  = countif(['kind'] == 'verdict' and score == 100),
            score_sum_sq      = sumif(tolong(score) * tolong(score), ['kind'] == 'verdict' and isnotnull(score))
            by day = startofday(timestamp)
        | order by day asc
        """;

    internal const string TopFlaggedSourcesKql =
        """
        declare query_parameters(tenantId: guid, fromTs: datetime, toTs: datetime, lim: long);
        tg_events
        | where tenant_id == tenantId
            and ['kind'] == 'verdict'
            and timestamp >= fromTs
            and timestamp <  toTs
        | summarize
            flagged_events = countif(band in ('challenge', 'block')),
            blocked_events = countif(band == 'block'),
            total_events   = count(),
            score_sum      = sumif(tolong(score), isnotnull(score)),
            scored         = countif(isnotnull(score)),
            first_seen     = min(timestamp),
            last_seen      = max(timestamp),
            score_bucket_00  = countif(score >= 0  and score < 10),
            score_bucket_10  = countif(score >= 10 and score < 20),
            score_bucket_20  = countif(score >= 20 and score < 30),
            score_bucket_30  = countif(score >= 30 and score < 40),
            score_bucket_40  = countif(score >= 40 and score < 50),
            score_bucket_50  = countif(score >= 50 and score < 60),
            score_bucket_60  = countif(score >= 60 and score < 70),
            score_bucket_70  = countif(score >= 70 and score < 80),
            score_bucket_80  = countif(score >= 80 and score < 90),
            score_bucket_90  = countif(score >= 90 and score < 100),
            score_bucket_100 = countif(score == 100),
            score_sum_sq     = sumif(tolong(score) * tolong(score), isnotnull(score))
            by source_value = ip
        | where flagged_events > 0
        | order by blocked_events desc, flagged_events desc
        | take lim
        """;

    // P2-01 — the session join. Resolves one placement per session via arg_min()
    // in a let-bound subquery, then inner-joins verdicts to it. BOTH sides filter
    // tenant_id, so the join can never cross tenants (D11). The capture side looks
    // back SessionJoinLookbehind before fromTs (P2-01 contract point 3).
    internal const string TopPlacementsDailyKql =
        """
        declare query_parameters(tenantId: guid, captureFromTs: datetime, fromTs: datetime, toTs: datetime, lim: long);
        let placements =
            tg_events
            | where tenant_id == tenantId
                and ['kind'] in ('tracker', 'pixel')
                and isnotempty(referrer)
                and timestamp >= captureFromTs
                and timestamp <  toTs
            | summarize arg_min(timestamp, referrer) by session_id
            | extend placement = tolower(tostring(parse_url(referrer).Host))
            | extend placement = iff(placement startswith 'www.', substring(placement, 4), placement)
            | where isnotempty(placement) and strlen(placement) <= 253
            | project session_id, placement;
        tg_events
        | where tenant_id == tenantId
            and ['kind'] == 'verdict'
            and timestamp >= fromTs
            and timestamp <  toTs
        | join kind=inner placements on session_id
        | summarize
            scored_events     = count(),
            allowed           = countif(band == 'allow'),
            challenged        = countif(band == 'challenge'),
            blocked           = countif(band == 'block'),
            score_sum         = sumif(tolong(score), isnotnull(score)),
            scored_with_score = countif(isnotnull(score)),
            no_js_beacon      = countif(has_js_beacon == false),
            score_bucket_00   = countif(score >= 0  and score < 10),
            score_bucket_10   = countif(score >= 10 and score < 20),
            score_bucket_20   = countif(score >= 20 and score < 30),
            score_bucket_30   = countif(score >= 30 and score < 40),
            score_bucket_40   = countif(score >= 40 and score < 50),
            score_bucket_50   = countif(score >= 50 and score < 60),
            score_bucket_60   = countif(score >= 60 and score < 70),
            score_bucket_70   = countif(score >= 70 and score < 80),
            score_bucket_80   = countif(score >= 80 and score < 90),
            score_bucket_90   = countif(score >= 90 and score < 100),
            score_bucket_100  = countif(score == 100),
            score_sum_sq      = sumif(tolong(score) * tolong(score), isnotnull(score))
            by day = startofday(timestamp), placement
        | order by day asc, scored_events desc, placement asc
        | partition by day (top lim by scored_events desc)
        """;

    internal const string SiteDailyCountsKql =
        """
        declare query_parameters(tenantId: guid, fromTs: datetime, toTs: datetime);
        tg_events
        | where tenant_id == tenantId
            and isnotempty(site_key)
            and timestamp >= fromTs
            and timestamp <  toTs
        | summarize
            total_events      = count(),
            scored_events     = countif(['kind'] == 'verdict'),
            allowed           = countif(['kind'] == 'verdict' and band == 'allow'),
            challenged        = countif(['kind'] == 'verdict' and band == 'challenge'),
            blocked           = countif(['kind'] == 'verdict' and band == 'block'),
            score_sum         = sumif(tolong(score), ['kind'] == 'verdict' and isnotnull(score)),
            scored_with_score = countif(['kind'] == 'verdict' and isnotnull(score)),
            no_js_beacon      = countif(['kind'] == 'verdict' and has_js_beacon == false),
            score_bucket_00   = countif(['kind'] == 'verdict' and score >= 0  and score < 10),
            score_bucket_10   = countif(['kind'] == 'verdict' and score >= 10 and score < 20),
            score_bucket_20   = countif(['kind'] == 'verdict' and score >= 20 and score < 30),
            score_bucket_30   = countif(['kind'] == 'verdict' and score >= 30 and score < 40),
            score_bucket_40   = countif(['kind'] == 'verdict' and score >= 40 and score < 50),
            score_bucket_50   = countif(['kind'] == 'verdict' and score >= 50 and score < 60),
            score_bucket_60   = countif(['kind'] == 'verdict' and score >= 60 and score < 70),
            score_bucket_70   = countif(['kind'] == 'verdict' and score >= 70 and score < 80),
            score_bucket_80   = countif(['kind'] == 'verdict' and score >= 80 and score < 90),
            score_bucket_90   = countif(['kind'] == 'verdict' and score >= 90 and score < 100),
            score_bucket_100  = countif(['kind'] == 'verdict' and score == 100),
            score_sum_sq      = sumif(tolong(score) * tolong(score), ['kind'] == 'verdict' and isnotnull(score))
            by day = startofday(timestamp), site_key
        | order by day asc, site_key asc
        """;

    public async Task<IpVelocityStats> GetIpVelocityAsync(string ip, TimeSpan window, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ip);

        var windowEnd = clock.UtcNow.UtcDateTime; // IClock.UtcNow is DateTimeOffset (FND-04)
        var windowStart = windowEnd - window;

        var props = NewProps();
        props.SetParameter("tenantId", tenant.TenantId.Value);
        props.SetParameter("ipAddr", KustoValueMapping.NormalizeIp(ip));
        props.SetParameter("fromTs", windowStart);
        props.SetParameter("toTs", windowEnd);

        using var r = await executor.ExecuteQueryAsync(IpVelocityKql, props, ct).ConfigureAwait(false);
        r.Read(); // summarize with no `by` always returns exactly one row
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

        var props = NewProps();
        props.SetParameter("tenantId", tenant.TenantId.Value);
        props.SetParameter("campaign", campaignId);
        props.SetParameter("fromTs", range.FromUtc);
        props.SetParameter("toTs", range.ToUtc);

        var days = new List<CampaignDailyCounts>();
        using (var r = await executor.ExecuteQueryAsync(CampaignReportKql, props, ct).ConfigureAwait(false))
        {
            while (r.Read())
            {
                var scoreSum = KustoValueMapping.ToInt64OrZero(r["score_sum"]);
                var scoredWithScore = Convert.ToInt64(r["scored_with_score"]);
                days.Add(new CampaignDailyCounts(
                    Day: DateOnly.FromDateTime(Convert.ToDateTime(r["day"])),
                    TotalEvents: Convert.ToInt64(r["total_events"]),
                    ScoredEvents: Convert.ToInt64(r["scored_events"]),
                    Allowed: Convert.ToInt64(r["allowed"]),
                    Challenged: Convert.ToInt64(r["challenged"]),
                    Blocked: Convert.ToInt64(r["blocked"]),
                    ScoreSum: scoreSum,
                    // ANA-04 computes the daily average as avgIf(score, kind='verdict' AND
                    // isNotNull(score)); deriving it from the two counters gives the
                    // identical value without depending on Kusto's empty-scope avg
                    // semantics. Missing != zero: 0 scored rows => NaN, never 0.0.
                    AvgScore: scoredWithScore == 0 ? double.NaN : (double)scoreSum / scoredWithScore,
                    NoJsBeaconCount: Convert.ToInt64(r["no_js_beacon"]),
                    ScoreHistogram: KustoValueMapping.ReadScoreHistogram(col => r[col])));
            }
        }

        // Byte-for-byte ANA-04's roll-up arithmetic (the deliberate asymmetry: the
        // report divides by ScoredEvents, the day divides by scored-with-score).
        var totalScored = days.Sum(d => d.ScoredEvents);
        var totalScoreSum = days.Sum(d => d.ScoreSum);
        var avg = totalScored == 0 ? double.NaN : (double)totalScoreSum / totalScored;
        var totalHistogram = days.Aggregate(ScoreHistogramCounts.Empty, (acc, d) => acc + d.ScoreHistogram);
        return new CampaignFraudReport(campaignId, range,
            days.Sum(d => d.TotalEvents), totalScored,
            days.Sum(d => d.Allowed), days.Sum(d => d.Challenged), days.Sum(d => d.Blocked),
            avg, days.Sum(d => d.NoJsBeaconCount), days, totalHistogram);
    }

    public async Task<IReadOnlyList<FlaggedSource>> GetTopFlaggedSourcesAsync(DateRange range, int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var props = NewProps();
        props.SetParameter("tenantId", tenant.TenantId.Value);
        props.SetParameter("fromTs", range.FromUtc);
        props.SetParameter("toTs", range.ToUtc);
        props.SetParameter("lim", (long)limit);

        var sources = new List<FlaggedSource>();
        using var r = await executor.ExecuteQueryAsync(TopFlaggedSourcesKql, props, ct).ConfigureAwait(false);
        while (r.Read())
        {
            var scoreSum = KustoValueMapping.ToInt64OrZero(r["score_sum"]);
            var scored = Convert.ToInt64(r["scored"]);
            sources.Add(new FlaggedSource(
                SourceType: "ip",
                SourceValue: Convert.ToString(r["source_value"]) ?? "",
                FlaggedEvents: Convert.ToInt64(r["flagged_events"]),
                BlockedEvents: Convert.ToInt64(r["blocked_events"]),
                TotalEvents: Convert.ToInt64(r["total_events"]),
                ScoreSum: scoreSum,
                AvgScore: scored == 0 ? double.NaN : (double)scoreSum / scored,
                FirstSeenUtc: KustoValueMapping.AsUtc(r["first_seen"]),
                LastSeenUtc: KustoValueMapping.AsUtc(r["last_seen"]),
                ScoreHistogram: KustoValueMapping.ReadScoreHistogram(col => r[col])));
        }
        return sources;
    }

    public async Task<IReadOnlyList<PlacementDailyCounts>> GetTopPlacementsDailyAsync(
        DateRange range, int limitPerDay, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limitPerDay);

        var props = NewProps();
        props.SetParameter("tenantId", tenant.TenantId.Value);
        props.SetParameter("captureFromTs", range.FromUtc - SessionJoinLookbehind);
        props.SetParameter("fromTs", range.FromUtc);
        props.SetParameter("toTs", range.ToUtc);
        props.SetParameter("lim", (long)limitPerDay);

        var rows = new List<PlacementDailyCounts>();
        using var r = await executor.ExecuteQueryAsync(TopPlacementsDailyKql, props, ct).ConfigureAwait(false);
        while (r.Read())
        {
            var scoreSum = KustoValueMapping.ToInt64OrZero(r["score_sum"]);
            var scoredWithScore = Convert.ToInt64(r["scored_with_score"]);
            rows.Add(new PlacementDailyCounts(
                Day: DateOnly.FromDateTime(Convert.ToDateTime(r["day"])),
                Placement: Convert.ToString(r["placement"]) ?? "",
                ScoredEvents: Convert.ToInt64(r["scored_events"]),
                Allowed: Convert.ToInt64(r["allowed"]),
                Challenged: Convert.ToInt64(r["challenged"]),
                Blocked: Convert.ToInt64(r["blocked"]),
                ScoreSum: scoreSum,
                AvgScore: scoredWithScore == 0 ? double.NaN : (double)scoreSum / scoredWithScore,
                NoJsBeaconCount: Convert.ToInt64(r["no_js_beacon"]),
                ScoreHistogram: KustoValueMapping.ReadScoreHistogram(col => r[col])));
        }
        return rows;
    }

    public async Task<IReadOnlyList<SiteDailyCounts>> GetSiteDailyCountsAsync(DateRange range, CancellationToken ct)
    {
        var props = NewProps();
        props.SetParameter("tenantId", tenant.TenantId.Value);
        props.SetParameter("fromTs", range.FromUtc);
        props.SetParameter("toTs", range.ToUtc);

        var rows = new List<SiteDailyCounts>();
        using var r = await executor.ExecuteQueryAsync(SiteDailyCountsKql, props, ct).ConfigureAwait(false);
        while (r.Read())
        {
            var scoreSum = KustoValueMapping.ToInt64OrZero(r["score_sum"]);
            var scoredWithScore = Convert.ToInt64(r["scored_with_score"]);
            rows.Add(new SiteDailyCounts(
                Day: DateOnly.FromDateTime(Convert.ToDateTime(r["day"])),
                SiteKey: Convert.ToString(r["site_key"]) ?? "",
                TotalEvents: Convert.ToInt64(r["total_events"]),
                ScoredEvents: Convert.ToInt64(r["scored_events"]),
                Allowed: Convert.ToInt64(r["allowed"]),
                Challenged: Convert.ToInt64(r["challenged"]),
                Blocked: Convert.ToInt64(r["blocked"]),
                ScoreSum: scoreSum,
                AvgScore: scoredWithScore == 0 ? double.NaN : (double)scoreSum / scoredWithScore,
                NoJsBeaconCount: Convert.ToInt64(r["no_js_beacon"]),
                ScoreHistogram: KustoValueMapping.ReadScoreHistogram(col => r[col])));
        }
        return rows;
    }

    private ClientRequestProperties NewProps()
    {
        var props = new ClientRequestProperties { ClientRequestId = $"tg;{Guid.NewGuid()}" };
        props.SetOption(ClientRequestProperties.OptionServerTimeout, TimeSpan.FromSeconds(options.Value.QueryTimeoutSeconds));
        return props;
    }
}
