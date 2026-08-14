using System.Diagnostics.Metrics;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Analytics.ClickHouse;
using TelemetryGuard.Api.Workers;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Core.Time;
using TelemetryGuard.Data;
using TelemetryGuard.MigrationRunner;
using Testcontainers.ClickHouse;
using Testcontainers.MsSql;

namespace TelemetryGuard.Tests.Integration;

/// <summary>
/// ANA-07 fixture: SQL Server (migrations 0001–000x via DAT-01's runner) +
/// ClickHouse (ANA-02 SchemaMigrator), seeded with two tenants that share a
/// campaign id AND an IP so cross-tenant isolation is actually exercised, plus
/// a tenant with no campaigns (fails in the isolation case) and an inactive
/// tenant that must be skipped entirely.
/// </summary>
public sealed class RollupServiceFixture : IAsyncLifetime
{
    public static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    public static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    public static readonly Guid TenantD = Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd"); // no campaigns; forced failure case
    public static readonly Guid TenantInactive = Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee"); // Status = 1, must be skipped

    /// <summary>Deliberately the SAME campaign guid for tenants A and B (D11 proof).</summary>
    public static readonly Guid CampaignShared = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
    /// <summary>Tenant A campaign with zero events: must produce NO summary rows.</summary>
    public static readonly Guid CampaignQuiet = Guid.Parse("11111111-2222-4333-8444-555555555555");

    /// <summary>P2-01: the ClickHouse site_key stamped on every publisher/site
    /// aggregate seed row below (kept distinct from the campaign-report seed's
    /// "sk-test" default so its SiteDailySummaries totals are easy to reason about).</summary>
    public const string PublisherSiteKey = "site-p2-01";
    /// <summary>P2-01: tenant A's own domain (dbo.Sites), seeded so the
    /// self-referral session below proves the rollup's self-referral filter.</summary>
    public const string SelfReferralSiteKey = "site-p2-01-self";
    public const string SelfReferralDomain = "a-selfsite.example";

    public static readonly DateTime Now = new(2026, 8, 12, 12, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime Day1 = Now.Date.AddDays(-2); // 2026-08-10
    public static readonly DateTime Day2 = Now.Date.AddDays(-1); // 2026-08-11

    private readonly MsSqlContainer _sql = new MsSqlBuilder().Build();
    private readonly ClickHouseContainer _clickHouse = new ClickHouseBuilder()
        .WithImage("clickhouse/clickhouse-server:24.8")
        .Build();

    public string SqlConnectionString { get; private set; } = "";
    public string ClickHouseConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_sql.StartAsync(), _clickHouse.StartAsync());

        SqlConnectionString = new SqlConnectionStringBuilder(_sql.GetConnectionString())
        {
            InitialCatalog = "TelemetryGuard",
        }.ConnectionString;
        var exitCode = Migrations.RunSqlServer(SqlConnectionString);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Migration runner failed with exit code {exitCode}.");
        }

        ClickHouseConnectionString = _clickHouse.GetConnectionString();
        await new SchemaMigrator(ClickHouseConnectionString).ApplyAsync();

        await SeedSqlAsync();
        await TestEvents.SeedAsync(ClickHouseConnectionString, BuildClickHouseSeed());
    }

    public async Task DisposeAsync()
    {
        await _sql.DisposeAsync();
        await _clickHouse.DisposeAsync();
    }

    /// <summary>Opens a raw connection stamped with SESSION_CONTEXT(N'TenantId').</summary>
    public async Task<SqlConnection> OpenStampedAsync(Guid tenantId)
    {
        var conn = new SqlConnection(SqlConnectionString);
        try
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "EXEC sp_set_session_context @key = N'TenantId', @value = @tid",
                new { tid = tenantId });
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    private async Task SeedSqlAsync()
    {
        await using var conn = await OpenStampedAsync(WellKnownTenants.System);
        await conn.ExecuteAsync(
            """
            INSERT INTO dbo.Tenants (TenantId, Name, Status) VALUES
                (@TenantA, N'A', 0),
                (@TenantB, N'B', 0),
                (@TenantD, N'D', 0),
                (@TenantInactive, N'Inactive', 1);

            INSERT INTO dbo.Campaigns (TenantId, CampaignId, Platform, LandingUrl) VALUES
                (@TenantA, @CampaignShared, 'google', N'https://a.example.com/'),
                (@TenantA, @CampaignQuiet,  'google', N'https://a.example.com/quiet'),
                (@TenantB, @CampaignShared, 'google', N'https://b.example.com/');

            -- P2-01: tenant A's own site/domain — the rollup's self-referral filter
            -- must exclude any placement whose normalized host matches this Domain.
            INSERT INTO dbo.Sites (TenantId, SiteKey, Domain) VALUES
                (@TenantA, @SelfReferralSiteKey, @SelfReferralDomain);
            """,
            new
            {
                TenantA, TenantB, TenantD, TenantInactive, CampaignShared, CampaignQuiet,
                SelfReferralSiteKey, SelfReferralDomain,
            });
    }

    private static List<ClickEvent> BuildClickHouseSeed()
    {
        var tenantA = new TenantId(TenantA);
        var tenantB = new TenantId(TenantB);
        var camp = CampaignShared.ToString("D"); // how API-02 stamps campaign_id

        return
        [
            // ---- tenant A, shared campaign ----
            // Day1: allow(10) + challenge(50) + block(90) + 1 tracker (tracker must
            // not count into Events; Events = scored verdicts only).
            TestEvents.Verdict(tenantA, "10.0.0.1", Day1.AddHours(10), "a1", 10, VerdictBands.Allow, campaignId: camp),
            TestEvents.Verdict(tenantA, "9.9.9.1", Day1.AddHours(11), "a2", 50, VerdictBands.Challenge, campaignId: camp),
            TestEvents.Verdict(tenantA, "9.9.9.1", Day1.AddHours(12), "a3", 90, VerdictBands.Block, campaignId: camp),
            TestEvents.Tracker(tenantA, "10.0.0.1", Day1.AddHours(13), "a4", campaignId: camp),
            // Day2: allow(20) + block(80)
            TestEvents.Verdict(tenantA, "10.0.0.2", Day2.AddHours(10), "a5", 20, VerdictBands.Allow, campaignId: camp),
            TestEvents.Verdict(tenantA, "9.9.9.2", Day2.AddHours(11), "a6", 80, VerdictBands.Block, campaignId: camp),

            // ---- tenant B: SAME campaign id, SAME ip as tenant A — must never
            // leak into tenant A's summary rows (D11 / RLS proof).
            TestEvents.Verdict(tenantB, "9.9.9.1", Day1.AddHours(10), "b1", 99, VerdictBands.Block, campaignId: camp),
            TestEvents.Verdict(tenantB, "9.9.9.1", Day2.AddHours(10), "b2", 60, VerdictBands.Challenge, campaignId: camp),

            // ==== P2-01: publisher/placement + site aggregates ====
            // Tenant A, publisher "ad-net-x.example": 2 verdicts day1 (allow 15, block 85), 1 verdict day2 (challenge 45).
            TestEvents.Tracker(tenantA, "20.0.0.1", Day1.AddHours(1), "pxA1",
                referrer: "https://ad-net-x.example/slot1", siteKey: PublisherSiteKey),
            TestEvents.Verdict(tenantA, "20.0.0.1", Day1.AddHours(1).AddSeconds(30), "pxA1",
                15, VerdictBands.Allow, siteKey: PublisherSiteKey),
            TestEvents.Tracker(tenantA, "20.0.0.2", Day1.AddHours(2), "pxA2",
                referrer: "https://ad-net-x.example/slot2", siteKey: PublisherSiteKey),
            TestEvents.Verdict(tenantA, "20.0.0.2", Day1.AddHours(2).AddSeconds(30), "pxA2",
                85, VerdictBands.Block, siteKey: PublisherSiteKey),
            TestEvents.Tracker(tenantA, "20.0.0.3", Day2.AddHours(1), "pxA3",
                referrer: "https://ad-net-x.example/slot3", siteKey: PublisherSiteKey),
            TestEvents.Verdict(tenantA, "20.0.0.3", Day2.AddHours(1).AddSeconds(30), "pxA3",
                45, VerdictBands.Challenge, siteKey: PublisherSiteKey),

            // Tenant A, publisher "ad-net-y.example" (SAME host tenant B also uses below):
            // 1 verdict day1 (block 95).
            TestEvents.Tracker(tenantA, "20.0.0.4", Day1.AddHours(3), "pyA1",
                referrer: "https://ad-net-y.example/slot1", siteKey: PublisherSiteKey),
            TestEvents.Verdict(tenantA, "20.0.0.4", Day1.AddHours(3).AddSeconds(30), "pyA1",
                95, VerdictBands.Block, siteKey: PublisherSiteKey),

            // Tenant A, self-referral session: referrer host == dbo.Sites.Domain seeded
            // above. Must be EXCLUDED from PublisherDailySummaries entirely, but still
            // counted in SiteDailySummaries (site_key is on every row regardless).
            TestEvents.Tracker(tenantA, "20.0.0.5", Day1.AddHours(4), "selfA1",
                referrer: $"https://www.{SelfReferralDomain}/home", siteKey: PublisherSiteKey),
            TestEvents.Verdict(tenantA, "20.0.0.5", Day1.AddHours(4).AddSeconds(30), "selfA1",
                5, VerdictBands.Allow, siteKey: PublisherSiteKey),

            // Tenant B, SAME publisher host "ad-net-y.example" as tenant A above — must
            // produce an INDEPENDENT PublisherDailySummaries row, never merged with A's.
            TestEvents.Tracker(tenantB, "21.0.0.1", Day1.AddHours(3), "pyB1",
                referrer: "https://ad-net-y.example/slotB", siteKey: PublisherSiteKey),
            TestEvents.Verdict(tenantB, "21.0.0.1", Day1.AddHours(3).AddSeconds(30), "pyB1",
                77, VerdictBands.Block, siteKey: PublisherSiteKey)
        ];
    }
}

/// <summary>
/// ANA-07 acceptance: one RunOnceAsync materializes exact per-day verdict
/// aggregates and flagged sources into SQL, watermarks advance per tenant,
/// re-running converges (absolute-value MERGE idempotence), and one failing
/// tenant neither stops the others nor advances its own watermark.
/// </summary>
[Trait("requires", "docker")]
public sealed class RollupServiceTests(RollupServiceFixture fixture) : IClassFixture<RollupServiceFixture>
{
    private const string Ip1 = "::ffff:9.9.9.1"; // ANA-04 returns IPv4-mapped IPv6 strings
    private const string Ip2 = "::ffff:9.9.9.2";

    private static readonly DateTime Now = RollupServiceFixture.Now;
    private static readonly DateTime Day1 = RollupServiceFixture.Day1;
    private static readonly DateTime Day2 = RollupServiceFixture.Day2;

    [Fact]
    public async Task RunOnce_MaterializesExactSummaries_Idempotently_WithFailureIsolation()
    {
        // ================= run 1: fresh state =================
        long run1Failures;
        await using (var provider = BuildServiceGraph(Now))
        using (var failures = ListenTenantFailures())
        {
            await provider.GetRequiredService<RollupService>().RunOnceAsync(CancellationToken.None);
            run1Failures = failures.Total;
        }
        Assert.Equal(0, run1Failures);

        // ---- tenant A daily summaries: exact numbers per UTC day, keyed
        // (TenantId, CampaignId, Date); tenant B's rows for the SAME campaign id
        // and days must not bleed in (RLS + per-tenant scope).
        await using (var conn = await fixture.OpenStampedAsync(RollupServiceFixture.TenantA))
        {
            var rows = (await conn.QueryAsync<(Guid CampaignId, DateTime Date, int Allowed, int Challenged, int Blocked, long ScoreSum, int Events)>(
                "SELECT CampaignId, [Date], Allowed, Challenged, Blocked, ScoreSum, Events FROM dbo.VerdictDailySummaries ORDER BY [Date]")).ToList();

            Assert.Equal(2, rows.Count); // zero-verdict CampaignQuiet produced NO row (never Events=0 fabrication)
            Assert.All(rows, r => Assert.Equal(RollupServiceFixture.CampaignShared, r.CampaignId));
            Assert.Equal((RollupServiceFixture.CampaignShared, Day1, 1, 1, 1, 150L, 3), rows[0]);
            Assert.Equal((RollupServiceFixture.CampaignShared, Day2, 1, 0, 1, 100L, 2), rows[1]);

            // Flagged sources: only IPs with challenge/block verdicts; allow-only
            // 10.0.0.x and the P2-01 allow-band IPs (20.0.0.1, 20.0.0.5) never appear;
            // SourceType is 'ip'. The P2-01 seed's block/challenge IPs (20.0.0.2,
            // 20.0.0.3, 20.0.0.4) land here too — this table is populated from EVERY
            // verdict in the day, independent of campaign/placement.
            var flagged = (await conn.QueryAsync<(DateTime Date, string SourceType, string Value, int FlaggedCount, int BlockedCount, long ScoreSum)>(
                "SELECT [Date], SourceType, Value, FlaggedCount, BlockedCount, ScoreSum FROM dbo.FlaggedSourcesDaily ORDER BY [Date], Value")).ToList();

            Assert.Equal(5, flagged.Count);
            Assert.Equal((Day1, "ip", "::ffff:20.0.0.2", 1, 1, 85L), flagged[0]);
            Assert.Equal((Day1, "ip", "::ffff:20.0.0.4", 1, 1, 95L), flagged[1]);
            Assert.Equal((Day1, "ip", Ip1, 2, 1, 140L), flagged[2]);
            Assert.Equal((Day2, "ip", "::ffff:20.0.0.3", 1, 0, 45L), flagged[3]);
            Assert.Equal((Day2, "ip", Ip2, 1, 1, 80L), flagged[4]);

            // Watermarks: TWO rows for this tenant now — 'verdict_daily' AND P2-01's
            // OWN 'publisher_daily' — both at Now (proves the second watermark is real).
            var watermarks = (await conn.QueryAsync<(string RollupName, DateTime WatermarkUtc)>(
                "SELECT RollupName, WatermarkUtc FROM dbo.RollupWatermarks")).ToDictionary(w => w.RollupName, w => w.WatermarkUtc);
            Assert.Equal(2, watermarks.Count);
            Assert.Equal(Now, watermarks["verdict_daily"]);
            Assert.Equal(Now, watermarks["publisher_daily"]);

            // P2-01: publisher placements — "ad-net-x.example" (2 verdicts day1, 1 day2),
            // "ad-net-y.example" (1 verdict day1, SHARED host with tenant B below but a
            // SEPARATE row per tenant), and the self-referral placement absent entirely.
            var placements = (await conn.QueryAsync<(string Placement, DateTime Date, int Events, int Allowed, int Challenged, int Blocked, long ScoreSum)>(
                "SELECT Placement, [Date], Events, Allowed, Challenged, Blocked, ScoreSum FROM dbo.PublisherDailySummaries ORDER BY [Date], Placement")).ToList();

            Assert.Equal(3, placements.Count); // self-referral produced NO row (§7)
            Assert.Equal(("ad-net-x.example", Day1, 2, 1, 0, 1, 100L), placements[0]);
            Assert.Equal(("ad-net-y.example", Day1, 1, 0, 0, 1, 95L), placements[1]);
            Assert.Equal(("ad-net-x.example", Day2, 1, 0, 1, 0, 45L), placements[2]);
            Assert.DoesNotContain(placements, p => p.Placement == RollupServiceFixture.SelfReferralDomain);

            // P2-01: site aggregate for the shared publisher-test site key — TotalEvents
            // spans every kind (tracker + verdict), Events counts verdicts only, and the
            // self-referral session's verdict IS counted here (only the placement view
            // excludes it). Filtered to OUR site key: the pre-existing campaign-report
            // seed above also produces dbo.SiteDailySummaries rows for its own default
            // site_key ("sk-test") which this test does not otherwise care about.
            var sites = (await conn.QueryAsync<(string SiteKey, DateTime Date, int TotalEvents, int Events, int Allowed, int Challenged, int Blocked, long ScoreSum)>(
                "SELECT SiteKey, [Date], TotalEvents, Events, Allowed, Challenged, Blocked, ScoreSum FROM dbo.SiteDailySummaries WHERE SiteKey = @SiteKey ORDER BY [Date]",
                new { SiteKey = RollupServiceFixture.PublisherSiteKey })).ToList();

            Assert.Equal(2, sites.Count);
            // Day1: pxA1, pxA2, pyA1, selfA1 = 4 sessions x (tracker+verdict) = 8 total, 4 scored.
            Assert.Equal((RollupServiceFixture.PublisherSiteKey, Day1, 8, 4, 2, 0, 2, 200L), sites[0]);
            // Day2: pxA3 = 1 session x (tracker+verdict) = 2 total, 1 scored.
            Assert.Equal((RollupServiceFixture.PublisherSiteKey, Day2, 2, 1, 0, 1, 0, 45L), sites[1]);
        }

        // ---- tenant B sees ONLY its own numbers for the shared campaign id / shared ip.
        await using (var conn = await fixture.OpenStampedAsync(RollupServiceFixture.TenantB))
        {
            var rows = (await conn.QueryAsync<(Guid CampaignId, DateTime Date, int Allowed, int Challenged, int Blocked, long ScoreSum, int Events)>(
                "SELECT CampaignId, [Date], Allowed, Challenged, Blocked, ScoreSum, Events FROM dbo.VerdictDailySummaries ORDER BY [Date]")).ToList();

            Assert.Equal(2, rows.Count);
            Assert.Equal((RollupServiceFixture.CampaignShared, Day1, 0, 0, 1, 99L, 1), rows[0]);
            Assert.Equal((RollupServiceFixture.CampaignShared, Day2, 0, 1, 0, 60L, 1), rows[1]);

            var flagged = (await conn.QueryAsync<(DateTime Date, string Value, int FlaggedCount, int BlockedCount, long ScoreSum)>(
                "SELECT [Date], Value, FlaggedCount, BlockedCount, ScoreSum FROM dbo.FlaggedSourcesDaily ORDER BY [Date], Value")).ToList();
            Assert.Equal(3, flagged.Count); // includes the P2-01 seed's 21.0.0.1 block verdict
            Assert.Equal((Day1, "::ffff:21.0.0.1", 1, 1, 77L), flagged[0]);
            Assert.Equal((Day1, Ip1, 1, 1, 99L), flagged[1]);
            Assert.Equal((Day2, Ip1, 1, 0, 60L), flagged[2]);

            // P2-01: tenant B's OWN "ad-net-y.example" row — same host as tenant A's,
            // but independent numbers (proves the SQL rows are never merged across tenants).
            var placements = (await conn.QueryAsync<(string Placement, DateTime Date, int Events, int Allowed, int Challenged, int Blocked, long ScoreSum)>(
                "SELECT Placement, [Date], Events, Allowed, Challenged, Blocked, ScoreSum FROM dbo.PublisherDailySummaries ORDER BY [Date], Placement")).ToList();
            Assert.Equal(("ad-net-y.example", Day1, 1, 0, 0, 1, 77L), Assert.Single(placements));

            var sites = (await conn.QueryAsync<(string SiteKey, DateTime Date, int TotalEvents, int Events, int Allowed, int Challenged, int Blocked, long ScoreSum)>(
                "SELECT SiteKey, [Date], TotalEvents, Events, Allowed, Challenged, Blocked, ScoreSum FROM dbo.SiteDailySummaries WHERE SiteKey = @SiteKey ORDER BY [Date]",
                new { SiteKey = RollupServiceFixture.PublisherSiteKey })).ToList();
            Assert.Equal((RollupServiceFixture.PublisherSiteKey, Day1, 2, 1, 0, 0, 1, 77L), Assert.Single(sites));
        }

        // ---- every ACTIVE tenant got TWO watermark rows now (A, B and campaign-less
        // D each own 'verdict_daily' AND 'publisher_daily'); the inactive tenant was
        // skipped entirely.
        await using (var sys = await fixture.OpenStampedAsync(WellKnownTenants.System))
        {
            var watermarks = (await sys.QueryAsync<(Guid TenantId, string RollupName, DateTime WatermarkUtc)>(
                "SELECT TenantId, RollupName, WatermarkUtc FROM dbo.RollupWatermarks ORDER BY TenantId, RollupName")).ToList();

            Assert.Equal(6, watermarks.Count); // 3 active tenants x 2 rollup names
            Assert.All(watermarks, w => Assert.Equal(Now, w.WatermarkUtc));
            var activeTenants = new[] { RollupServiceFixture.TenantA, RollupServiceFixture.TenantB, RollupServiceFixture.TenantD };
            foreach (var tid in activeTenants)
            {
                var names = watermarks.Where(w => w.TenantId == tid).Select(w => w.RollupName).OrderBy(n => n).ToArray();
                Assert.Equal(new[] { "publisher_daily", "verdict_daily" }, names);
            }
            Assert.DoesNotContain(watermarks, w => w.TenantId == RollupServiceFixture.TenantInactive);
        }

        // ================= run 2: idempotence =================
        // Same clock, back-to-back: identical row counts and values (absolute-value
        // MERGE convergence) — only UpdatedUtc/watermark may move, which the
        // snapshot deliberately excludes.
        var before = await SnapshotAsync();
        await using (var provider = BuildServiceGraph(Now))
        {
            await provider.GetRequiredService<RollupService>().RunOnceAsync(CancellationToken.None);
        }
        var after = await SnapshotAsync();
        Assert.Equal(before.Summaries, after.Summaries);
        Assert.Equal(before.Flagged, after.Flagged);
        Assert.Equal(before.Watermarks, after.Watermarks);
        Assert.Equal(before.Placements, after.Placements); // P2-01 absolute-value MERGE convergence
        Assert.Equal(before.Sites, after.Sites);

        // ================= run 3: per-tenant failure isolation =================
        // Tenant D's analytics scope throws; A and B are healthy. A/B rows land and
        // their watermarks advance to now2; D's watermark stays at Now; the failure
        // counter increments by exactly 1; summary values stay converged.
        var now2 = Now.AddHours(2);
        long run3Failures;
        await using (var provider = BuildServiceGraph(now2, failingTenant: RollupServiceFixture.TenantD))
        using (var failures = ListenTenantFailures())
        {
            await provider.GetRequiredService<RollupService>().RunOnceAsync(CancellationToken.None);
            run3Failures = failures.Total;
        }
        Assert.Equal(1, run3Failures);

        var afterFailure = await SnapshotAsync();
        Assert.Equal(before.Summaries, afterFailure.Summaries); // healthy tenants' rows re-converged
        Assert.Equal(before.Flagged, afterFailure.Flagged);
        Assert.Equal(before.Placements, afterFailure.Placements); // P2-01 rows re-converged too
        Assert.Equal(before.Sites, afterFailure.Sites);
        await using (var sys = await fixture.OpenStampedAsync(WellKnownTenants.System))
        {
            var watermarks = (await sys.QueryAsync<(Guid TenantId, string RollupName, DateTime WatermarkUtc)>(
                "SELECT TenantId, RollupName, WatermarkUtc FROM dbo.RollupWatermarks"))
                .ToDictionary(w => (w.TenantId, w.RollupName), w => w.WatermarkUtc);

            Assert.Equal(now2, watermarks[(RollupServiceFixture.TenantA, "verdict_daily")]);
            Assert.Equal(now2, watermarks[(RollupServiceFixture.TenantA, "publisher_daily")]);
            Assert.Equal(now2, watermarks[(RollupServiceFixture.TenantB, "verdict_daily")]);
            Assert.Equal(now2, watermarks[(RollupServiceFixture.TenantB, "publisher_daily")]);
            // Tenant D's analytics scope threw during the flagged-sources step (before
            // P2-01's own section runs) — NEITHER of its watermarks advanced.
            Assert.Equal(Now, watermarks[(RollupServiceFixture.TenantD, "verdict_daily")]);
            Assert.Equal(Now, watermarks[(RollupServiceFixture.TenantD, "publisher_daily")]);
        }
    }

    /// <summary>
    /// P2-01 watermark independence: on a DB where 'verdict_daily' already sits at
    /// Now but 'publisher_daily' has never run, the first run for that rollup name
    /// backfills LookbackDays — proving the second watermark is a real, separate
    /// key and not silently aliased onto the first.
    /// </summary>
    [Fact]
    public async Task PublisherRollup_FirstRun_BackfillsIndependentlyOfAnAlreadyAdvancedVerdictWatermark()
    {
        await using (var provider = BuildServiceGraph(Now))
        {
            await provider.GetRequiredService<RollupService>().RunOnceAsync(CancellationToken.None);
        }

        // Wipe ONLY the publisher_daily watermark for tenant A, simulating a DB where
        // verdict_daily has already run to completion but the publisher rollup has not.
        await using (var conn = await fixture.OpenStampedAsync(RollupServiceFixture.TenantA))
        {
            await conn.ExecuteAsync(
                "DELETE FROM dbo.RollupWatermarks WHERE RollupName = 'publisher_daily'");
        }

        await using (var provider = BuildServiceGraph(Now))
        {
            await provider.GetRequiredService<RollupService>().RunOnceAsync(CancellationToken.None);
        }

        await using (var conn = await fixture.OpenStampedAsync(RollupServiceFixture.TenantA))
        {
            var watermarks = (await conn.QueryAsync<(string RollupName, DateTime WatermarkUtc)>(
                "SELECT RollupName, WatermarkUtc FROM dbo.RollupWatermarks")).ToDictionary(w => w.RollupName, w => w.WatermarkUtc);
            Assert.Equal(Now, watermarks["verdict_daily"]);
            Assert.Equal(Now, watermarks["publisher_daily"]); // backfilled independently, not skipped

            // Day1's publisher rows are still there — the backfill re-covered LookbackDays,
            // not just "from now onward".
            var count = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.PublisherDailySummaries WHERE [Date] = @Day1",
                new { Day1 });
            Assert.True(count > 0, "publisher_daily's first run must backfill LookbackDays of history");
        }
    }

    /// <summary>
    /// Mirrors Program.cs: concrete scoped TenantContext + ITenantContext forward,
    /// AddTelemetryGuardData (real DAT-03 factories + DAT-06 repositories), a scoped
    /// real ClickHouseAnalyticsQueries (ANA-05 shape), FixedClock, RollupOptions
    /// defaults. <paramref name="failingTenant"/> swaps that one tenant's analytics
    /// for a throwing stub — per-tenant failure without touching shared state.
    /// </summary>
    private ServiceProvider BuildServiceGraph(DateTime nowUtc, Guid? failingTenant = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Main"] = fixture.SqlConnectionString,
            })
            .Build();

        var chOptions = Options.Create(new ClickHouseAnalyticsOptions
        {
            ConnectionString = fixture.ClickHouseConnectionString,
        });

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration)
            .AddScoped<TenantContext>()
            .AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>())
            .AddSingleton<IClock>(new FixedClock(nowUtc))
            .AddTelemetryGuardData();

        services.AddScoped<IAnalyticsQueries>(sp =>
        {
            var tenant = sp.GetRequiredService<ITenantContext>();
            return failingTenant is { } f && tenant.TenantId.Value == f
                ? new ThrowingAnalyticsQueries()
                : new ClickHouseAnalyticsQueries(tenant, chOptions, sp.GetRequiredService<IClock>());
        });

        services.AddSingleton(Options.Create(new RollupOptions())); // 15 min / 3 days / 100 / verdict_daily
        services.AddSingleton<RollupService>();
        return services.BuildServiceProvider();
    }

    private async Task<(List<string> Summaries, List<string> Flagged, List<string> Watermarks, List<string> Placements, List<string> Sites)> SnapshotAsync()
    {
        await using var sys = await fixture.OpenStampedAsync(WellKnownTenants.System);
        var summaries = (await sys.QueryAsync<string>(
            """
            SELECT CONCAT(TenantId, '|', CampaignId, '|', [Date], '|', Allowed, '|', Challenged, '|', Blocked, '|', ScoreSum, '|', Events)
            FROM dbo.VerdictDailySummaries ORDER BY TenantId, CampaignId, [Date]
            """)).ToList();
        var flagged = (await sys.QueryAsync<string>(
            """
            SELECT CONCAT(TenantId, '|', [Date], '|', SourceType, '|', Value, '|', FlaggedCount, '|', BlockedCount, '|', ScoreSum)
            FROM dbo.FlaggedSourcesDaily ORDER BY TenantId, [Date], SourceType, Value
            """)).ToList();
        var watermarks = (await sys.QueryAsync<string>(
            """
            SELECT CONCAT(TenantId, '|', RollupName, '|', CONVERT(varchar(30), WatermarkUtc, 126))
            FROM dbo.RollupWatermarks ORDER BY TenantId, RollupName
            """)).ToList();
        var placements = (await sys.QueryAsync<string>(
            """
            SELECT CONCAT(TenantId, '|', [Date], '|', Placement, '|', Events, '|', Allowed, '|', Challenged, '|', Blocked, '|', ScoreSum, '|', NoJsBeaconCount)
            FROM dbo.PublisherDailySummaries ORDER BY TenantId, [Date], Placement
            """)).ToList();
        var sites = (await sys.QueryAsync<string>(
            """
            SELECT CONCAT(TenantId, '|', [Date], '|', SiteKey, '|', TotalEvents, '|', Events, '|', Allowed, '|', Challenged, '|', Blocked, '|', ScoreSum, '|', NoJsBeaconCount)
            FROM dbo.SiteDailySummaries ORDER BY TenantId, [Date], SiteKey
            """)).ToList();
        return (summaries, flagged, watermarks, placements, sites);
    }

    private static FailureCounterListener ListenTenantFailures() => new();

    /// <summary>Sums tg.rollup.tenant_failures measurements published while alive.</summary>
    private sealed class FailureCounterListener : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _total;

        public FailureCounterListener()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "TelemetryGuard.Rollup"
                    && instrument.Name == "tg.rollup.tenant_failures")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>(
                (_, value, _, _) => Interlocked.Add(ref _total, value));
            _listener.Start();
        }

        public long Total => Interlocked.Read(ref _total);

        public void Dispose() => _listener.Dispose();
    }

    private sealed class ThrowingAnalyticsQueries : IAnalyticsQueries
    {
        public Task<IpVelocityStats> GetIpVelocityAsync(string ip, TimeSpan window, CancellationToken ct)
            => throw new InvalidOperationException("Deliberate per-tenant analytics failure (test).");

        public Task<CampaignFraudReport> GetCampaignReportAsync(string campaignId, DateRange range, CancellationToken ct)
            => throw new InvalidOperationException("Deliberate per-tenant analytics failure (test).");

        public Task<IReadOnlyList<FlaggedSource>> GetTopFlaggedSourcesAsync(DateRange range, int limit, CancellationToken ct)
            => throw new InvalidOperationException("Deliberate per-tenant analytics failure (test).");

        public Task<IReadOnlyList<PlacementDailyCounts>> GetTopPlacementsDailyAsync(DateRange range, int limitPerDay, CancellationToken ct)
            => throw new InvalidOperationException("Deliberate per-tenant analytics failure (test).");

        public Task<IReadOnlyList<SiteDailyCounts>> GetSiteDailyCountsAsync(DateRange range, CancellationToken ct)
            => throw new InvalidOperationException("Deliberate per-tenant analytics failure (test).");
    }
}
