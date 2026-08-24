using System.Diagnostics;
using System.Diagnostics.Metrics;
using Dapper;
using Microsoft.Extensions.Options;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Core.Time;
using TelemetryGuard.Data;
using TelemetryGuard.Data.Models;
using TelemetryGuard.Data.Repositories;
using TelemetryGuard.Api.Services;

namespace TelemetryGuard.Api.Workers;

/// <summary>
/// D23 rollup: ClickHouse aggregates -> SQL summary tables, every 15 min.
/// The ONLY bridge between the event store and the relational tier — portals and
/// APIs read the small RLS-protected SQL aggregates and never query ClickHouse.
/// Windows re-cover recent days each run, and DAT-06's absolute-value MERGE
/// upserts make replays convergent (never double-counting).
/// </summary>
public sealed class RollupService(
    IServiceScopeFactory scopeFactory,
    ISystemConnectionFactory systemConnections, // DAT-03: tenant enumeration ONLY
    IOptions<RollupOptions> options,
    IClock clock,
    ILogger<RollupService> log) : BackgroundService
{
    private static readonly Meter Meter = new("TelemetryGuard.Rollup");
    private static readonly Counter<long> Runs           = Meter.CreateCounter<long>("tg.rollup.runs");
    private static readonly Counter<long> TenantsOk      = Meter.CreateCounter<long>("tg.rollup.tenants_processed");
    private static readonly Counter<long> TenantFailures = Meter.CreateCounter<long>("tg.rollup.tenant_failures");
    private static readonly Counter<long> RowsUpserted   = Meter.CreateCounter<long>("tg.rollup.rows_upserted");
    private static readonly Histogram<double> RunSeconds = Meter.CreateHistogram<double>("tg.rollup.run_seconds");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Value.IntervalMinutes));
        do { await RunOnceAsync(stoppingToken); }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// One full rollup pass over every active tenant. Public for tests. Never
    /// throws except on shutdown cancellation — a failed run must not kill the
    /// host loop; the next tick retries the same window idempotently.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        Runs.Add(1);
        var sw = Stopwatch.StartNew();
        try
        {
            // Enumerate active tenants under the SYSTEM sentinel — the only
            // sanctioned cross-tenant read path (D11). The DAT-03 factories are
            // the only way this class ever obtains a connection.
            IReadOnlyList<Guid> tenantIds;
            await using (var sys = await systemConnections.OpenSystemAsync(ct))
            {
                tenantIds = (await sys.QueryAsync<Guid>(
                    "SELECT TenantId FROM dbo.Tenants WHERE Status = 0")).AsList(); // 0 = Active (DAT-02)
            }

            // Per-tenant loop with failure isolation: a failed tenant keeps its old
            // watermark (its window re-covers next run) and never stops the rest.
            foreach (var tid in tenantIds)
            {
                ct.ThrowIfCancellationRequested();
                try { await ProcessTenantAsync(tid, ct); TenantsOk.Add(1); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    TenantFailures.Add(1);
                    log.LogError(ex, "Rollup failed for tenant {TenantId}; continuing with remaining tenants", tid);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // shutdown — propagate so the host loop stops cleanly
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Rollup run failed; the next tick retries the same window");
        }
        finally
        {
            RunSeconds.Record(sw.Elapsed.TotalSeconds);
        }
    }

    private async Task ProcessTenantAsync(Guid tid, CancellationToken ct)
    {
        // A DI scope resolved to this tenant: DAT-06's repositories and ANA-05's
        // scoped IAnalyticsQueries work unchanged (DAT-06's preferred job pattern).
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        // FND-04's set-once scoped TenantContext — same call DAT-04's middleware makes per request.
        sp.GetRequiredService<TenantContext>().Resolve(new TenantId(tid));

        var queries     = sp.GetRequiredService<IAnalyticsQueries>();          // ANA-05, scoped
        var summaries   = sp.GetRequiredService<IVerdictSummaryRepository>();  // DAT-06, scoped
        var watermarks  = sp.GetRequiredService<IRollupWatermarkRepository>(); // DAT-06, scoped
        var connFactory = sp.GetRequiredService<ITenantConnectionFactory>();   // DAT-03, campaigns query
        var publishers  = sp.GetRequiredService<IPublisherSummaryRepository>(); // P2-01, scoped
        var webhooks     = sp.GetService<IWebhookPublisher>();

        var now = clock.UtcNow.UtcDateTime;
        var rollupName = options.Value.RollupName;
        var watermark = await watermarks.GetAsync(rollupName, ct);   // null on first run
        var (fromDay, range) = ComputeWindow(watermark, now, options.Value.LookbackDays);

        // Enumerate this tenant's campaigns on an RLS-scoped stamped connection.
        // Explicit @TenantId stays in the statement for index seeks; correctness
        // comes from RLS (D11).
        IReadOnlyList<Guid> campaignIds;
        await using (var conn = await connFactory.OpenAsync(ct))
        {
            campaignIds = (await conn.QueryAsync<Guid>(
                "SELECT CampaignId FROM dbo.Campaigns WHERE TenantId = @TenantId",
                new { TenantId = tid })).Append(Guid.Empty).Distinct().ToList();
        }

        long rows = 0;
        foreach (var campaignId in campaignIds)
        {
            // ClickHouse stores campaign_id as the Guid in "D" format (lowercase,
            // hyphenated) — the same string API-02 stamps onto ClickEvent.CampaignId
            // from the /c?cid= parameter.
            var report = await queries.GetCampaignReportAsync(
                campaignId == Guid.Empty ? CampaignScopes.Campaignless : campaignId.ToString("D"), range, ct);
            foreach (var d in report.Days)
            {
                await summaries.UpsertDailySummaryAsync(MapDay(tid, campaignId, d), ct);
                rows++;
            }
        }

        for (var day = fromDay; day <= now.Date; day = day.AddDays(1))
        {
            var dayEnd = day.AddDays(1) < now ? day.AddDays(1) : now;
            var dayRange = new DateRange(
                DateTime.SpecifyKind(day, DateTimeKind.Utc),
                DateTime.SpecifyKind(dayEnd, DateTimeKind.Utc));
            var sources = await queries.GetTopFlaggedSourcesAsync(dayRange, options.Value.TopFlaggedLimit, ct);
            foreach (var s in sources)
            {
                await summaries.UpsertFlaggedSourceAsync(new FlaggedSourceDailyRow(
                    TenantId: tid, Date: DateOnly.FromDateTime(day),
                    SourceType: s.SourceType,                  // "ip" — allowed by the DAT-06 CHECK constraint
                    Value: s.SourceValue,
                    FlaggedCount: checked((int)s.FlaggedEvents),
                    BlockedCount: checked((int)s.BlockedEvents),
                ScoreSum: s.ScoreSum)
                {
                    ScoreDistribution = s.ScoreDistribution,
                }, ct);
                rows++;
            }
        }

        // ---- P2-01: publisher/placement + site (non-campaign) aggregates ----
        // Its OWN watermark: on first deploy this rollup must backfill LookbackDays
        // independently of the already-advanced 'verdict_daily' watermark, otherwise
        // the new tables would silently start at "now" with no history.
        var pubName = options.Value.PublisherRollupName;
        var pubWatermark = await watermarks.GetAsync(pubName, ct);
        var (_, pubRange) = ComputeWindow(pubWatermark, now, options.Value.LookbackDays);

        // Self-referral filter: a session's first capture row on a tenant's OWN
        // landing page yields that tenant's own domain as a "placement", which would
        // top every report. dbo.Sites is RLS-EXEMPT (a resolution table), so the
        // explicit TenantId predicate here is load-bearing, not just an index hint.
        HashSet<string> ownDomains;
        await using (var conn = await connFactory.OpenAsync(ct))
        {
            var domains = await conn.QueryAsync<string>(new CommandDefinition(
                "SELECT Domain FROM dbo.Sites WHERE TenantId = @TenantId",
                new { TenantId = tid }, cancellationToken: ct));
            ownDomains = domains.Select(NormalizeHost)
                                .Where(d => d.Length > 0)
                                .ToHashSet(StringComparer.Ordinal);
        }

        var placements = await queries.GetTopPlacementsDailyAsync(
            pubRange, options.Value.TopPlacementsLimit, ct);
        foreach (var p in placements)
        {
            if (ownDomains.Contains(p.Placement)) continue;   // tenant's own site, not a publisher
            await publishers.UpsertPlacementDailyAsync(new PublisherDailySummaryRow(
                TenantId: tid, Date: p.Day, Placement: p.Placement,
                Events: checked((int)p.ScoredEvents),
                Allowed: checked((int)p.Allowed),
                Challenged: checked((int)p.Challenged),
                Blocked: checked((int)p.Blocked),
                ScoreSum: p.ScoreSum,                          // AvgScore (NaN included) is never stored
                NoJsBeaconCount: checked((int)p.NoJsBeaconCount))
                {
                    ScoreDistribution = p.ScoreDistribution,
                }, ct);
            rows++;
        }

        var sites = await queries.GetSiteDailyCountsAsync(pubRange, ct);
        foreach (var s in sites)
        {
            await publishers.UpsertSiteDailyAsync(new SiteDailySummaryRow(
                TenantId: tid, Date: s.Day, SiteKey: s.SiteKey,
                TotalEvents: checked((int)s.TotalEvents),
                Events: checked((int)s.ScoredEvents),
                Allowed: checked((int)s.Allowed),
                Challenged: checked((int)s.Challenged),
                Blocked: checked((int)s.Blocked),
                ScoreSum: s.ScoreSum,
                NoJsBeaconCount: checked((int)s.NoJsBeaconCount))
                {
                    ScoreDistribution = s.ScoreDistribution,
                }, ct);
            rows++;
        }

        await watermarks.SetAsync(pubName, now, ct);  // advance ONLY after these upserts succeeded

        await watermarks.SetAsync(rollupName, now, ct);  // advance ONLY after all upserts succeeded
        RowsUpserted.Add(rows);
        if (webhooks is not null)
            await webhooks.PublishAsync("rollup.completed", $"{rollupName}:{now:O}", new
            {
                tenantId = tid,
                rollup = rollupName,
                completedUtc = now,
                rowsUpserted = rows,
            }, ct);
    }

    /// <summary>
    /// Absolute-value mapping into DAT-06's summary row: stores ScoreSum + Events
    /// (mergeable), never an average. A zero-verdict day carries Events = 0 and
    /// ScoreSum = 0 so readers get "no data", not a fabricated 0 average (§7 —
    /// missing ≠ zero; AvgScore, NaN included, is deliberately never consumed here).
    /// </summary>
    internal static VerdictDailySummaryRow MapDay(Guid tenantId, Guid campaignId, CampaignDailyCounts d) =>
        new VerdictDailySummaryRow(TenantId: tenantId, CampaignId: campaignId, Date: d.Day,
            Allowed: checked((int)d.Allowed), Challenged: checked((int)d.Challenged),
            Blocked: checked((int)d.Blocked), ScoreSum: d.ScoreSum,
            Events: checked((int)d.ScoredEvents))
        {
            ScoreDistribution = d.ScoreDistribution,
        }; // Events = scored (verdict) events; avg = ScoreSum/Events

    /// <summary>
    /// Pure window math, all UTC. First run (no watermark) backfills LookbackDays;
    /// otherwise re-cover from the day BEFORE the watermark so late-arriving events
    /// and grace-period verdicts fold in idempotently.
    /// </summary>
    internal static (DateTime fromDay, DateRange range) ComputeWindow(
        DateTime? watermarkUtc, DateTime nowUtc, int lookbackDays)
    {
        // A SQL-read watermark arrives with Kind = Unspecified — normalize every
        // bound to DateTimeKind.Utc (DateRange requires it).
        var fromDay = DateTime.SpecifyKind(
            watermarkUtc is null
                ? nowUtc.Date.AddDays(-lookbackDays)
                : watermarkUtc.Value.Date.AddDays(-1), // re-cover the last closed day for late arrivals
            DateTimeKind.Utc);
        return (fromDay, new DateRange(
            fromDay,
            DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)));
    }

    /// <summary>
    /// P2-01 host normalization, identical on both sides of the self-referral
    /// filter: trim, lowercase (invariant), drop any scheme/path/port a configured
    /// dbo.Sites.Domain may carry, then strip a leading "www.". Must match what
    /// lower(domainWithoutWWW(...)) produces in the ClickHouse query.
    /// </summary>
    internal static string NormalizeHost(string? value)
    {
        var s = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (s.Length == 0) return string.Empty;
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        var slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];
        var colon = s.IndexOf(':');
        if (colon >= 0) s = s[..colon];
        if (s.StartsWith("www.", StringComparison.Ordinal)) s = s[4..];
        return s;
    }
}
