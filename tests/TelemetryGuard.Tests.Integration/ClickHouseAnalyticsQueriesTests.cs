using Microsoft.Extensions.Options;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Analytics.ClickHouse;
using TelemetryGuard.Core.Tenancy;
using Testcontainers.ClickHouse;

namespace TelemetryGuard.Tests.Integration;

/// <summary>
/// One ClickHouse container, ANA-02 schema, and one deterministic seed dataset
/// (three tenants) shared by every ANA-04 query test. Timestamps are anchored
/// to <see cref="Now"/> so a stub IClock makes trailing windows reproducible.
/// </summary>
public sealed class ClickHouseAnalyticsQueriesFixture : IAsyncLifetime
{
    public static readonly TenantId TenantA = new(Guid.Parse("aaaaaaaa-1111-4111-8111-111111111111"));
    public static readonly TenantId TenantB = new(Guid.Parse("bbbbbbbb-2222-4222-8222-222222222222"));
    public static readonly TenantId TenantC = new(Guid.Parse("cccccccc-3333-4333-8333-333333333333"));

    public static readonly DateTime Now = new(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime Day1 = new(2026, 8, 8, 0, 0, 0, DateTimeKind.Utc);
    public static DateRange ThreeDays => new(Day1, Day1.AddDays(3)); // [08-08, 08-11)

    private readonly ClickHouseContainer _container = new ClickHouseBuilder()
        .WithImage("clickhouse/clickhouse-server:24.8")
        .Build();

    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
        await new SchemaMigrator(ConnectionString).ApplyAsync();
        await TestEvents.SeedAsync(ConnectionString, BuildSeed());
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private static List<ClickEvent> BuildSeed()
    {
        var d1 = Day1;             // 2026-08-08
        var d2 = Day1.AddDays(1);  // 2026-08-09
        var d3 = Day1.AddDays(2);  // 2026-08-10 (Now's day)

        return
        [
            // ---- IP velocity (tenant A, ip 1.2.3.4, trailing 1h window ending Now) ----
            // 5 in-window events / 3 sessions / 2 non-null UAs / 1 fingerprint / 1 block verdict.
            TestEvents.Tracker(TenantA, "1.2.3.4", Now.AddMinutes(-10), "v-s1", userAgent: "ua-1"),
            TestEvents.Tracker(TenantA, "1.2.3.4", Now.AddMinutes(-20), "v-s1", userAgent: "ua-1", fingerprint: "fp-1"),
            TestEvents.Tracker(TenantA, "1.2.3.4", Now.AddMinutes(-30), "v-s2", userAgent: "ua-2"),
            TestEvents.Tracker(TenantA, "1.2.3.4", Now.AddMinutes(-40), "v-s3"),
            TestEvents.Verdict(TenantA, "1.2.3.4", Now.AddMinutes(-5), "v-s3", 85, VerdictBands.Block, userAgent: "ua-2"),
            // outside the 1h window — must not count
            TestEvents.Tracker(TenantA, "1.2.3.4", Now.AddHours(-2), "v-out", userAgent: "ua-9"),
            // tenant B on the SAME ip, in-window — must never leak into tenant A numbers
            TestEvents.Tracker(TenantB, "1.2.3.4", Now.AddMinutes(-15), "vb-s1", userAgent: "ub-1"),
            TestEvents.Tracker(TenantB, "1.2.3.4", Now.AddMinutes(-25), "vb-s2", userAgent: "ub-1"),
            TestEvents.Verdict(TenantB, "1.2.3.4", Now.AddMinutes(-8), "vb-s2", 99, VerdictBands.Block, userAgent: "ub-1"),

            // ---- campaign report (tenant A, camp-A, three days) ----
            // day 1: 2 trackers + allow(10, js) + block(90, no-js)
            TestEvents.Tracker(TenantA, "10.0.0.1", d1.AddHours(8), "c-s1", campaignId: "camp-A"),
            TestEvents.Tracker(TenantA, "10.0.0.1", d1.AddHours(9), "c-s2", campaignId: "camp-A"),
            TestEvents.Verdict(TenantA, "10.0.0.1", d1.AddHours(10), "c-s3", 10, VerdictBands.Allow, campaignId: "camp-A"),
            TestEvents.Verdict(TenantA, "10.0.0.1", d1.AddHours(11), "c-s4", 90, VerdictBands.Block, campaignId: "camp-A", hasJsBeacon: false),
            // day 2: 1 tracker + challenge(50, js) + challenge(40, no-js) + allow(20, js)
            TestEvents.Tracker(TenantA, "10.0.0.2", d2.AddHours(8), "c-s5", campaignId: "camp-A"),
            TestEvents.Verdict(TenantA, "10.0.0.2", d2.AddHours(9), "c-s6", 50, VerdictBands.Challenge, campaignId: "camp-A"),
            TestEvents.Verdict(TenantA, "10.0.0.2", d2.AddHours(10), "c-s7", 40, VerdictBands.Challenge, campaignId: "camp-A", hasJsBeacon: false),
            TestEvents.Verdict(TenantA, "10.0.0.2", d2.AddHours(11), "c-s8", 20, VerdictBands.Allow, campaignId: "camp-A"),
            // day 3: tracker only (scored = 0 for the day => daily AvgScore NaN)
            TestEvents.Tracker(TenantA, "10.0.0.3", d3.AddHours(8), "c-s9", campaignId: "camp-A"),
            // before the range — must not count
            TestEvents.Tracker(TenantA, "10.0.0.1", d1.AddDays(-1).AddHours(10), "c-out", campaignId: "camp-A"),
            // tenant B rows for the SAME campaign id — must never leak into tenant A
            TestEvents.Tracker(TenantB, "10.9.0.1", d2.AddHours(9), "cb-s1", campaignId: "camp-A"),
            TestEvents.Verdict(TenantB, "10.9.0.1", d2.AddHours(10), "cb-s2", 99, VerdictBands.Block, campaignId: "camp-A"),

            // ---- campaign with traffic but zero verdicts (tenant A) => AvgScore NaN ----
            TestEvents.Tracker(TenantA, "10.0.1.1", d2.AddHours(9), "q-s1", campaignId: "camp-quiet"),
            TestEvents.Tracker(TenantA, "10.0.1.2", d2.AddHours(10), "q-s2", campaignId: "camp-quiet"),

            // ---- top flagged sources (tenant C, verdicts only count) ----
            // 9.9.9.1: 3 block + 1 challenge + 1 allow => flagged 4, blocked 3, total 5
            TestEvents.Verdict(TenantC, "9.9.9.1", d1.AddHours(10), "f-s1", 80, VerdictBands.Block),
            TestEvents.Verdict(TenantC, "9.9.9.1", d2.AddHours(10), "f-s2", 90, VerdictBands.Block),
            TestEvents.Verdict(TenantC, "9.9.9.1", d3.AddHours(10), "f-s3", 95, VerdictBands.Block),
            TestEvents.Verdict(TenantC, "9.9.9.1", d2.AddHours(11), "f-s4", 50, VerdictBands.Challenge),
            TestEvents.Verdict(TenantC, "9.9.9.1", d2.AddHours(12), "f-s5", 10, VerdictBands.Allow),
            // non-verdict traffic from the same ip must not inflate total_events
            TestEvents.Tracker(TenantC, "9.9.9.1", d2.AddHours(13), "f-s6"),
            // before the range — must not shift first_seen
            TestEvents.Verdict(TenantC, "9.9.9.1", d1.AddDays(-1).AddHours(10), "f-out", 99, VerdictBands.Block),
            // 9.9.9.2: 2 challenge + 1 allow => flagged 2, blocked 0, total 3
            TestEvents.Verdict(TenantC, "9.9.9.2", d1.AddHours(9), "f-s7", 40, VerdictBands.Challenge),
            TestEvents.Verdict(TenantC, "9.9.9.2", d2.AddHours(9), "f-s8", 60, VerdictBands.Challenge),
            TestEvents.Verdict(TenantC, "9.9.9.2", d3.AddHours(9), "f-s9", 5, VerdictBands.Allow),
            // 9.9.9.3: allow-only => no challenge/block verdicts, must be excluded entirely
            TestEvents.Verdict(TenantC, "9.9.9.3", d1.AddHours(9), "f-s10", 10, VerdictBands.Allow),
            TestEvents.Verdict(TenantC, "9.9.9.3", d2.AddHours(9), "f-s11", 20, VerdictBands.Allow),
            // 9.9.9.4: single block => flagged 1, blocked 1, total 1
            TestEvents.Verdict(TenantC, "9.9.9.4", d2.AddHours(15), "f-s12", 75, VerdictBands.Block),
            // tenant B noise with heavy blocking — must never appear for tenant C
            TestEvents.Verdict(TenantB, "9.9.9.9", d1.AddHours(9), "fb-s1", 95, VerdictBands.Block),
            TestEvents.Verdict(TenantB, "9.9.9.9", d2.AddHours(9), "fb-s2", 96, VerdictBands.Block),
            TestEvents.Verdict(TenantB, "9.9.9.9", d3.AddHours(9), "fb-s3", 97, VerdictBands.Block)
        ];
    }
}

/// <summary>
/// ANA-04 acceptance: tenant-scoped velocity counts, campaign aggregates with
/// NaN-preserving averages, and flagged-source ranking — all against a real
/// ClickHouse seeded via the ANA-03 sink. Cross-provider suite is ANA-06.
/// </summary>
public sealed class ClickHouseAnalyticsQueriesTests(ClickHouseAnalyticsQueriesFixture fixture)
    : IClassFixture<ClickHouseAnalyticsQueriesFixture>
{
    private static readonly DateTime Now = ClickHouseAnalyticsQueriesFixture.Now;

    [Fact]
    public async Task IpVelocity_Ipv4Input_MatchesMappedRows_ScopedToWindowAndTenant()
    {
        var stats = await Queries(ClickHouseAnalyticsQueriesFixture.TenantA)
            .GetIpVelocityAsync("1.2.3.4", TimeSpan.FromHours(1), CancellationToken.None);

        Assert.Equal("1.2.3.4", stats.Ip);
        Assert.Equal(TimeSpan.FromHours(1), stats.Window);
        Assert.Equal(Now, stats.WindowEndUtc);
        Assert.Equal(5, stats.ClickCount);          // out-of-window row excluded
        Assert.Equal(3, stats.DistinctSessions);
        Assert.Equal(2, stats.DistinctUserAgents);  // null UA not counted
        Assert.Equal(1, stats.DistinctFingerprints);
        Assert.Equal(1, stats.FlaggedCount);        // score >= 71
    }

    [Fact]
    public async Task IpVelocity_OtherTenant_SeesOnlyItsOwnRows()
    {
        // Same ip, same window, tenant B context: tenant A's 5 rows must not appear.
        var stats = await Queries(ClickHouseAnalyticsQueriesFixture.TenantB)
            .GetIpVelocityAsync("1.2.3.4", TimeSpan.FromHours(1), CancellationToken.None);

        Assert.Equal(3, stats.ClickCount);
        Assert.Equal(2, stats.DistinctSessions);
        Assert.Equal(1, stats.DistinctUserAgents);
        Assert.Equal(0, stats.DistinctFingerprints);
        Assert.Equal(1, stats.FlaggedCount);
    }

    [Fact]
    public async Task CampaignReport_DailyRowsAndTotals_ExactNumbers()
    {
        var report = await Queries(ClickHouseAnalyticsQueriesFixture.TenantA)
            .GetCampaignReportAsync("camp-A", ClickHouseAnalyticsQueriesFixture.ThreeDays, CancellationToken.None);

        Assert.Equal("camp-A", report.CampaignId);
        Assert.Equal(9, report.TotalEvents);       // pre-range row and tenant B rows excluded
        Assert.Equal(5, report.ScoredEvents);
        Assert.Equal(2, report.Allowed);
        Assert.Equal(2, report.Challenged);
        Assert.Equal(1, report.Blocked);
        Assert.Equal(42.0, report.AvgScore);       // (10+90+50+40+20)/5
        Assert.Equal(2, report.NoJsBeaconCount);

        Assert.Equal(3, report.Days.Count);
        Assert.Equal(
            new CampaignDailyCounts(new DateOnly(2026, 8, 8), 4, 2, 1, 0, 1, 100, 50.0, 1)
            {
                ScoreDistribution = new ScoreDistribution(
                    0, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 8_200),
            },
            report.Days[0]);

        var day2 = report.Days[1];
        Assert.Equal(new DateOnly(2026, 8, 9), day2.Day);
        Assert.Equal(4, day2.TotalEvents);
        Assert.Equal(3, day2.ScoredEvents);
        Assert.Equal(1, day2.Allowed);
        Assert.Equal(2, day2.Challenged);
        Assert.Equal(0, day2.Blocked);
        Assert.Equal(110, day2.ScoreSum);
        Assert.Equal(110.0 / 3, day2.AvgScore, 9);
        Assert.Equal(1, day2.NoJsBeaconCount);

        // Day with traffic but zero verdicts: sum is 0 (mergeable), average is NaN (missing != zero).
        var day3 = report.Days[2];
        Assert.Equal(new DateOnly(2026, 8, 10), day3.Day);
        Assert.Equal(1, day3.TotalEvents);
        Assert.Equal(0, day3.ScoredEvents);
        Assert.Equal(0, day3.ScoreSum);
        Assert.True(double.IsNaN(day3.AvgScore));
    }

    [Fact]
    public async Task CampaignReport_NoScoredEvents_AvgIsNaN_NotZero()
    {
        var report = await Queries(ClickHouseAnalyticsQueriesFixture.TenantA)
            .GetCampaignReportAsync("camp-quiet", ClickHouseAnalyticsQueriesFixture.ThreeDays, CancellationToken.None);

        Assert.Equal(2, report.TotalEvents);
        Assert.Equal(0, report.ScoredEvents);
        Assert.True(double.IsNaN(report.AvgScore));
        var day = Assert.Single(report.Days);
        Assert.Equal(0, day.ScoreSum);
        Assert.True(double.IsNaN(day.AvgScore));
    }

    [Fact]
    public async Task CampaignReport_NoEventsAtAll_AllZero_NaNAvg_EmptyDays()
    {
        var report = await Queries(ClickHouseAnalyticsQueriesFixture.TenantA)
            .GetCampaignReportAsync("camp-never-seeded", ClickHouseAnalyticsQueriesFixture.ThreeDays, CancellationToken.None);

        Assert.Equal(0, report.TotalEvents);
        Assert.Equal(0, report.ScoredEvents);
        Assert.Equal(0, report.Allowed);
        Assert.Equal(0, report.Challenged);
        Assert.Equal(0, report.Blocked);
        Assert.Equal(0, report.NoJsBeaconCount);
        Assert.True(double.IsNaN(report.AvgScore));
        Assert.Empty(report.Days);
    }

    [Fact]
    public async Task CampaignReport_OtherTenant_SameCampaignId_IsIsolated()
    {
        var report = await Queries(ClickHouseAnalyticsQueriesFixture.TenantB)
            .GetCampaignReportAsync("camp-A", ClickHouseAnalyticsQueriesFixture.ThreeDays, CancellationToken.None);

        Assert.Equal(2, report.TotalEvents);
        Assert.Equal(1, report.ScoredEvents);
        Assert.Equal(1, report.Blocked);
        Assert.Equal(99.0, report.AvgScore);
    }

    [Fact]
    public async Task TopFlaggedSources_RanksByBlockedThenFlagged_ExcludesUnflaggedAndOtherTenants()
    {
        var sources = await Queries(ClickHouseAnalyticsQueriesFixture.TenantC)
            .GetTopFlaggedSourcesAsync(ClickHouseAnalyticsQueriesFixture.ThreeDays, 10, CancellationToken.None);

        Assert.Equal(3, sources.Count); // allow-only 9.9.9.3 and tenant B's 9.9.9.9 excluded
        Assert.All(sources, s => Assert.Equal("ip", s.SourceType));
        Assert.Equal(
            new[] { "::ffff:9.9.9.1", "::ffff:9.9.9.4", "::ffff:9.9.9.2" },
            sources.Select(s => s.SourceValue).ToArray());

        var top = sources[0];
        Assert.Equal(4, top.FlaggedEvents);   // challenge + block bands
        Assert.Equal(3, top.BlockedEvents);   // block band only
        Assert.Equal(5, top.TotalEvents);     // verdict rows only — tracker row excluded
        Assert.Equal(325, top.ScoreSum);
        Assert.Equal(65.0, top.AvgScore);
        Assert.Equal(DateTimeKind.Utc, top.FirstSeenUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, top.LastSeenUtc.Kind);
        Assert.Equal(new DateTime(2026, 8, 8, 10, 0, 0, DateTimeKind.Utc), top.FirstSeenUtc); // pre-range verdict excluded
        Assert.Equal(new DateTime(2026, 8, 10, 10, 0, 0, DateTimeKind.Utc), top.LastSeenUtc);

        Assert.Equal(1, sources[1].FlaggedEvents);
        Assert.Equal(1, sources[1].BlockedEvents);
        Assert.Equal(2, sources[2].FlaggedEvents);
        Assert.Equal(0, sources[2].BlockedEvents);
        Assert.Equal(105, sources[2].ScoreSum);
        Assert.Equal(35.0, sources[2].AvgScore);
    }

    [Fact]
    public async Task TopFlaggedSources_LimitCapsRowCount()
    {
        var sources = await Queries(ClickHouseAnalyticsQueriesFixture.TenantC)
            .GetTopFlaggedSourcesAsync(ClickHouseAnalyticsQueriesFixture.ThreeDays, 2, CancellationToken.None);

        Assert.Equal(2, sources.Count);
        Assert.Equal(
            new[] { "::ffff:9.9.9.1", "::ffff:9.9.9.4" },
            sources.Select(s => s.SourceValue).ToArray());
    }

    // ---------------------------------------------------------- P2-01 edges --
    // Provider-specific ClickHouse SQL edges that don't belong in the shared
    // AnalyticsContractTests suite: engine functions (domainWithoutWWW), the
    // length(placement) <= 253 HAVING guard, and argMin tie-break behavior.
    // Each test seeds its own isolated day/tenant so it can't collide with the
    // fixture's shared seed or with another test in this class.

    [Fact]
    public async Task GetTopPlacementsDailyAsync_NonUrlReferrer_ProducesNoBucket()
    {
        var day = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var range = new DateRange(day, day.AddDays(1));
        await TestEvents.SeedAsync(fixture.ConnectionString,
        [
            TestEvents.Tracker(ClickHouseAnalyticsQueriesFixture.TenantA, "10.1.1.1", day.AddHours(1), "p01-nonurl",
                referrer: "not a url"),
            TestEvents.Verdict(ClickHouseAnalyticsQueriesFixture.TenantA, "10.1.1.1", day.AddHours(2), "p01-nonurl",
                50, VerdictBands.Challenge),
        ]);

        var placements = await Queries(ClickHouseAnalyticsQueriesFixture.TenantA)
            .GetTopPlacementsDailyAsync(range, 10, CancellationToken.None);

        Assert.Empty(placements); // unparseable referrer -> no resolvable placement
    }

    [Fact]
    public async Task GetTopPlacementsDailyAsync_HostLongerThan253Chars_IsDropped()
    {
        var day = new DateTime(2027, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var range = new DateRange(day, day.AddDays(1));
        var longHost = new string('a', 300) + ".example";
        await TestEvents.SeedAsync(fixture.ConnectionString,
        [
            TestEvents.Tracker(ClickHouseAnalyticsQueriesFixture.TenantA, "10.1.1.2", day.AddHours(1), "p01-longhost",
                referrer: $"https://{longHost}/"),
            TestEvents.Verdict(ClickHouseAnalyticsQueriesFixture.TenantA, "10.1.1.2", day.AddHours(2), "p01-longhost",
                50, VerdictBands.Challenge),
        ]);

        var placements = await Queries(ClickHouseAnalyticsQueriesFixture.TenantA)
            .GetTopPlacementsDailyAsync(range, 10, CancellationToken.None);

        Assert.Empty(placements); // > 253 chars -> dropped by the HAVING guard
    }

    [Fact]
    public async Task GetTopPlacementsDailyAsync_TwoCaptureRowsOnOneSession_ResolvesToTheEarliestByTimestamp()
    {
        var day = new DateTime(2027, 1, 3, 0, 0, 0, DateTimeKind.Utc);
        var range = new DateRange(day, day.AddDays(1));
        await TestEvents.SeedAsync(fixture.ConnectionString,
        [
            TestEvents.Tracker(ClickHouseAnalyticsQueriesFixture.TenantA, "10.1.1.3", day.AddHours(1), "p01-argmin",
                referrer: "https://tracker-pub.example/"),
            // a pixel row 5s later on the SAME session, different referrer — argMin(timestamp) must pick the tracker.
            new ClickEvent
            {
                TenantId = ClickHouseAnalyticsQueriesFixture.TenantA,
                SiteKey = "sk-test",
                SessionId = "p01-argmin",
                Kind = EventKind.Pixel,
                Ip = "10.1.1.3",
                HasJsBeacon = false,
                Referrer = "https://pixel-pub.example/",
                RetentionDays = 90,
                TimestampUtc = day.AddHours(1).AddSeconds(5),
            },
            TestEvents.Verdict(ClickHouseAnalyticsQueriesFixture.TenantA, "10.1.1.3", day.AddHours(2), "p01-argmin",
                50, VerdictBands.Challenge),
        ]);

        var placements = await Queries(ClickHouseAnalyticsQueriesFixture.TenantA)
            .GetTopPlacementsDailyAsync(range, 10, CancellationToken.None);

        var bucket = Assert.Single(placements);
        Assert.Equal("tracker-pub.example", bucket.Placement);
    }

    private ClickHouseAnalyticsQueries Queries(TenantId tenant) =>
        new(new FixedTenantContext(tenant),
            Options.Create(new ClickHouseAnalyticsOptions { ConnectionString = fixture.ConnectionString }),
            new FixedClock(Now));
}

/// <summary>
/// ANA-04 input validation: rejected before any connection is opened, so no
/// container is needed (the connection string points at a closed port).
/// </summary>
public sealed class ClickHouseAnalyticsQueriesValidationTests
{
    private static readonly DateRange AnyRange = DateRange.LastDays(1, DateTime.UtcNow);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task IpVelocity_BlankIp_ThrowsArgumentException(string ip)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => Queries().GetIpVelocityAsync(ip, TimeSpan.FromMinutes(5), CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CampaignReport_BlankCampaignId_ThrowsArgumentException(string campaignId)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => Queries().GetCampaignReportAsync(campaignId, AnyRange, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task TopFlaggedSources_NonPositiveLimit_ThrowsArgumentOutOfRange(int limit)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Queries().GetTopFlaggedSourcesAsync(AnyRange, limit, CancellationToken.None));
    }

    private static ClickHouseAnalyticsQueries Queries() =>
        new(new FixedTenantContext(new TenantId(Guid.NewGuid())),
            Options.Create(new ClickHouseAnalyticsOptions { ConnectionString = "Host=127.0.0.1;Port=59998" }),
            new FixedClock(DateTimeOffset.UtcNow));
}
