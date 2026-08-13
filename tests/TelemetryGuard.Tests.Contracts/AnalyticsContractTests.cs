// ---------------------------------------------------------------------------
// D7 GUARDRAIL — READ BEFORE TOUCHING THIS FILE
//
// Every analytics provider MUST have a runner class in this project inheriting
// AnalyticsContractTests<TFixture>, with a fixture backed by that provider's
// REAL engine running in a container (see README.md for the per-provider
// list). Adding a provider without its runner — or skipping/weakening a
// contract test to make a provider pass — violates spec D7.
//
// The suite asserts only the WEAK shared guarantees (eventual batched
// visibility <= 5 s); do not tighten it with engine-specific timing that only
// one provider can meet. Engine-specific assertions belong in the provider's
// own integration tests, never here.
// ---------------------------------------------------------------------------
using System.Net;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Core.Tenancy;

namespace TelemetryGuard.Tests.Contracts;

/// <summary>
/// The shared, provider-agnostic behavioral contract of IEventSink +
/// IAnalyticsQueries + ILabelSink. Each provider runs it via a one-line
/// subclass supplying its <see cref="IAnalyticsProviderFixture"/>.
/// Tests use unique IPs/sessions/campaigns and disjoint time windows per run,
/// so the shared container never cross-contaminates tests.
/// </summary>
public abstract class AnalyticsContractTests<TFixture>(TFixture fx) : IClassFixture<TFixture>
    where TFixture : class, IAnalyticsProviderFixture
{
    protected static readonly TenantId TenantA = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"));
    protected static readonly TenantId TenantB = new(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"));

    // Per-test anchor days, all disjoint and inside the 90-day retention
    // window, so range-scoped queries (top flagged sources in particular)
    // never see another test's rows.
    private static readonly DateTime Today = DateTime.UtcNow.Date;

    private static readonly CancellationToken Ct = CancellationToken.None;

    // ---------------------------------------------------------------- helpers

    protected static async Task<T> EventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> done,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            var v = await read();
            if (done(v)) return v;
            if (DateTime.UtcNow > deadline) return v; // final value; caller's assert produces the failure
            await Task.Delay(250);
        }
    }

    private Task WriteAsync(params ClickEvent[] events) =>
        fx.Sink.WriteBatchAsync(events, Ct).AsTask();

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    /// <summary>Fresh IPv4 in 10/8 so every test hits a never-seen source.</summary>
    private static string UniqueIpv4()
    {
        var b = Guid.NewGuid().ToByteArray();
        return $"10.{b[0]}.{b[1]}.{b[2]}";
    }

    /// <summary>Engine-neutral IP equality: providers may surface an IPv4
    /// source in mapped-IPv6 form, so compare parsed addresses, not strings.</summary>
    private static bool SameIp(string rendered, string seeded) =>
        IPAddress.Parse(rendered).MapToIPv6().Equals(IPAddress.Parse(seeded).MapToIPv6());

    // ------------------------------------------------------------------ tests

    [Fact]
    public async Task BatchWrite_IsEventuallyReadable_Within5s()
    {
        var ip = UniqueIpv4();
        var now = DateTime.UtcNow;
        var events = Enumerable.Range(0, 25)
            .Select(i => TestEvents.Create(TenantA, ip, Unique("s"),
                timestampUtc: now.AddSeconds(-60 - i)))
            .ToArray();
        await WriteAsync(events);

        var queries = fx.CreateQueries(TenantA, now);
        var stats = await EventuallyAsync(
            () => queries.GetIpVelocityAsync(ip, TimeSpan.FromHours(1), Ct),
            s => s.ClickCount == 25);

        Assert.Equal(25, stats.ClickCount);
    }

    [Fact]
    public async Task IpVelocity_ComputesCountsOverSeededEvents()
    {
        var ip = UniqueIpv4();
        var now = Today.AddDays(-10).AddHours(12);
        var (s1, s2, s3) = (Unique("s"), Unique("s"), Unique("s"));
        var (ua1, ua2) = (Unique("ua"), Unique("ua"));
        var (fp1, fp2) = (Unique("fp"), Unique("fp"));

        await WriteAsync(
            // 6 in-window events: 3 sessions, 2 distinct UAs, 2 distinct fingerprints
            TestEvents.Create(TenantA, ip, s1, timestampUtc: now.AddMinutes(-10), userAgent: ua1, fingerprint: fp1),
            TestEvents.Create(TenantA, ip, s1, timestampUtc: now.AddMinutes(-15), userAgent: ua1, fingerprint: fp2),
            TestEvents.Create(TenantA, ip, s2, timestampUtc: now.AddMinutes(-20), userAgent: ua2),
            TestEvents.Create(TenantA, ip, s3, timestampUtc: now.AddMinutes(-25), fingerprint: fp1),
            // 2 verdicts with score 90 (block band) => FlaggedCount
            TestEvents.Verdict(TenantA, ip, s2, 90, VerdictBands.Block, timestampUtc: now.AddMinutes(-5), userAgent: ua2),
            TestEvents.Verdict(TenantA, ip, s3, 90, VerdictBands.Block, timestampUtc: now.AddMinutes(-8), userAgent: ua1),
            // outside the 1h window (now-2h): would shift every count if leaked
            TestEvents.Create(TenantA, ip, Unique("s"), timestampUtc: now.AddHours(-2),
                userAgent: Unique("ua"), fingerprint: Unique("fp")));

        var queries = fx.CreateQueries(TenantA, now);
        var stats = await EventuallyAsync(
            () => queries.GetIpVelocityAsync(ip, TimeSpan.FromHours(1), Ct),
            s => s.ClickCount == 6);

        Assert.Equal(6, stats.ClickCount);          // all seeded in-window rows; out-of-window excluded
        Assert.Equal(3, stats.DistinctSessions);
        Assert.Equal(2, stats.DistinctUserAgents);
        Assert.Equal(2, stats.DistinctFingerprints);
        Assert.Equal(2, stats.FlaggedCount);
    }

    [Fact]
    public async Task CampaignReport_AggregatesBandsAndDays()
    {
        var campaign = Unique("camp");
        var ip = UniqueIpv4();
        var day1 = Today.AddDays(-40);
        var day2 = day1.AddDays(1);
        var range = new DateRange(day1, day1.AddDays(3)); // day3 stays empty

        await WriteAsync(
            // day 1: 1 tracker + 2 allow + 1 block
            TestEvents.Create(TenantA, ip, Unique("s"), campaign, timestampUtc: day1.AddHours(8)),
            TestEvents.Verdict(TenantA, ip, Unique("s"), 10, VerdictBands.Allow, campaign, day1.AddHours(9)),
            TestEvents.Verdict(TenantA, ip, Unique("s"), 20, VerdictBands.Allow, campaign, day1.AddHours(10)),
            TestEvents.Verdict(TenantA, ip, Unique("s"), 90, VerdictBands.Block, campaign, day1.AddHours(11)),
            // day 2: 1 challenge
            TestEvents.Verdict(TenantA, ip, Unique("s"), 50, VerdictBands.Challenge, campaign, day2.AddHours(9)));

        var queries = fx.CreateQueries(TenantA);
        var report = await EventuallyAsync(
            () => queries.GetCampaignReportAsync(campaign, range, Ct),
            r => r.TotalEvents == 5);

        Assert.Equal(5, report.TotalEvents);        // tracker row included
        Assert.Equal(4, report.ScoredEvents);
        Assert.Equal(2, report.Allowed);
        Assert.Equal(1, report.Challenged);
        Assert.Equal(1, report.Blocked);
        Assert.Equal((10 + 20 + 90 + 50) / 4.0, report.AvgScore, 0.01); // seeded weighted mean

        Assert.Equal(2, report.Days.Count);         // empty day 3 absent
        Assert.True(report.Days[0].Day < report.Days[1].Day, "Days must be ordered ascending");

        var d1 = report.Days[0];
        Assert.Equal(DateOnly.FromDateTime(day1), d1.Day);
        Assert.Equal(4, d1.TotalEvents);
        Assert.Equal(3, d1.ScoredEvents);
        Assert.Equal(2, d1.Allowed);
        Assert.Equal(0, d1.Challenged);
        Assert.Equal(1, d1.Blocked);
        Assert.Equal(10 + 20 + 90, d1.ScoreSum);    // sum of seeded scores
        Assert.Equal((10 + 20 + 90) / 3.0, d1.AvgScore, 0.01);

        var d2 = report.Days[1];
        Assert.Equal(DateOnly.FromDateTime(day2), d2.Day);
        Assert.Equal(1, d2.TotalEvents);
        Assert.Equal(1, d2.ScoredEvents);
        Assert.Equal(1, d2.Challenged);
        Assert.Equal(50, d2.ScoreSum);
        Assert.Equal(50.0, d2.AvgScore, 0.01);
    }

    [Fact]
    public async Task CampaignReport_NoScoredEvents_AvgScoreIsNaN()
    {
        var campaign = Unique("quiet");
        var ip = UniqueIpv4();
        var day = Today.AddDays(-35);
        var range = new DateRange(day, day.AddDays(1));

        await WriteAsync(
            TestEvents.Create(TenantA, ip, Unique("s"), campaign, timestampUtc: day.AddHours(8)),
            TestEvents.Create(TenantA, ip, Unique("s"), campaign, timestampUtc: day.AddHours(9)),
            TestEvents.Create(TenantA, ip, Unique("s"), campaign, EventKind.Pixel, timestampUtc: day.AddHours(10)));

        var queries = fx.CreateQueries(TenantA);
        var report = await EventuallyAsync(
            () => queries.GetCampaignReportAsync(campaign, range, Ct),
            r => r.TotalEvents == 3);

        Assert.Equal(3, report.TotalEvents);
        Assert.Equal(0, report.ScoredEvents);
        Assert.Equal(0, report.Allowed);
        Assert.Equal(0, report.Challenged);
        Assert.Equal(0, report.Blocked);
        // Missing != zero: an unscored scope surfaces NaN, NEVER 0.0.
        Assert.True(double.IsNaN(report.AvgScore),
            $"AvgScore must be NaN with no scored events, was {report.AvgScore}");
        var d = Assert.Single(report.Days);
        Assert.Equal(0, d.ScoreSum);
        Assert.True(double.IsNaN(d.AvgScore),
            $"Daily AvgScore must be NaN with no scored events, was {d.AvgScore}");
    }

    [Fact]
    public async Task TopFlaggedSources_RanksAndLimits()
    {
        var day = Today.AddDays(-30);
        var range = new DateRange(day, day.AddDays(1));
        var (ipHeavy, ipMid, ipLow, ipAllow, ipChal) =
            (UniqueIpv4(), UniqueIpv4(), UniqueIpv4(), UniqueIpv4(), UniqueIpv4());

        var seed = new List<ClickEvent>();
        for (var i = 0; i < 5; i++) // 5 block verdicts
            seed.Add(TestEvents.Verdict(TenantA, ipHeavy, Unique("s"), 80 + i, VerdictBands.Block, timestampUtc: day.AddHours(1 + i)));
        for (var i = 0; i < 3; i++) // 3 block verdicts
            seed.Add(TestEvents.Verdict(TenantA, ipMid, Unique("s"), 75 + i, VerdictBands.Block, timestampUtc: day.AddHours(7 + i)));
        seed.Add(TestEvents.Verdict(TenantA, ipLow, Unique("s"), 72, VerdictBands.Block, timestampUtc: day.AddHours(11)));
        // allow-only source: must never appear in the result at all
        seed.Add(TestEvents.Verdict(TenantA, ipAllow, Unique("s"), 10, VerdictBands.Allow, timestampUtc: day.AddHours(13)));
        seed.Add(TestEvents.Verdict(TenantA, ipAllow, Unique("s"), 20, VerdictBands.Allow, timestampUtc: day.AddHours(14)));
        // challenge-only source: flagged but zero blocked
        seed.Add(TestEvents.Verdict(TenantA, ipChal, Unique("s"), 50, VerdictBands.Challenge, timestampUtc: day.AddHours(16)));
        seed.Add(TestEvents.Verdict(TenantA, ipChal, Unique("s"), 60, VerdictBands.Challenge, timestampUtc: day.AddHours(17)));
        await WriteAsync(seed.ToArray());

        var queries = fx.CreateQueries(TenantA);
        var all = await EventuallyAsync(
            () => queries.GetTopFlaggedSourcesAsync(range, 10, Ct),
            r => r.Count == 4);

        Assert.Equal(4, all.Count); // heavy, mid, low, challenge-only; allow-only absent
        Assert.All(all, s => Assert.Equal("ip", s.SourceType));
        Assert.DoesNotContain(all, s => SameIp(s.SourceValue, ipAllow));
        Assert.True(SameIp(all[0].SourceValue, ipHeavy), "rank 1 must be the 5-block source");
        Assert.True(SameIp(all[1].SourceValue, ipMid), "rank 2 must be the 3-block source");
        Assert.True(SameIp(all[2].SourceValue, ipLow), "rank 3 must be the 1-block source");
        Assert.Equal(5, all[0].BlockedEvents);
        Assert.Equal(3, all[1].BlockedEvents);
        Assert.Equal(1, all[2].BlockedEvents);

        var chal = Assert.Single(all, s => SameIp(s.SourceValue, ipChal));
        Assert.Equal(2, chal.FlaggedEvents);   // challenge verdicts count as flagged...
        Assert.Equal(0, chal.BlockedEvents);   // ...but never as blocked

        var limited = await queries.GetTopFlaggedSourcesAsync(range, 2, Ct);
        Assert.Equal(2, limited.Count);        // limit respected exactly
        Assert.True(SameIp(limited[0].SourceValue, ipHeavy));
        Assert.True(SameIp(limited[1].SourceValue, ipMid));
    }

    [Fact]
    public async Task TenantIsolation_TenantAQueries_NeverSeeTenantB()
    {
        // Identical-shaped data for both tenants: SAME ip, SAME campaign id.
        // Only the tenant differs — the D11 proof at the analytics layer.
        var ip = UniqueIpv4();
        var campaign = Unique("camp");
        var day = Today.AddDays(-25);
        var range = new DateRange(day, day.AddDays(1));
        var queryNow = day.AddHours(12); // trailing 6h window covers the seeds below

        await WriteAsync(
            // tenant A: 1 tracker + 2 block verdicts (scores 80, 90)
            TestEvents.Create(TenantA, ip, Unique("s"), campaign, timestampUtc: day.AddHours(8)),
            TestEvents.Verdict(TenantA, ip, Unique("s"), 80, VerdictBands.Block, campaign, day.AddHours(9)),
            TestEvents.Verdict(TenantA, ip, Unique("s"), 90, VerdictBands.Block, campaign, day.AddHours(10)),
            // tenant B: 3 trackers + 1 block verdict (score 99)
            TestEvents.Create(TenantB, ip, Unique("s"), campaign, timestampUtc: day.AddHours(8)),
            TestEvents.Create(TenantB, ip, Unique("s"), campaign, timestampUtc: day.AddHours(9)),
            TestEvents.Create(TenantB, ip, Unique("s"), campaign, timestampUtc: day.AddHours(10)),
            TestEvents.Verdict(TenantB, ip, Unique("s"), 99, VerdictBands.Block, campaign, day.AddHours(11)));

        var qa = fx.CreateQueries(TenantA, queryNow);
        var qb = fx.CreateQueries(TenantB, queryNow);
        var window = TimeSpan.FromHours(6);

        // wait until BOTH tenants' rows are visible, then assert isolation
        var va = await EventuallyAsync(() => qa.GetIpVelocityAsync(ip, window, Ct), s => s.ClickCount == 3);
        var vb = await EventuallyAsync(() => qb.GetIpVelocityAsync(ip, window, Ct), s => s.ClickCount == 4);

        // tenant A sees ONLY tenant A numbers, on all three methods
        Assert.Equal(3, va.ClickCount);
        Assert.Equal(2, va.FlaggedCount);

        var ra = await qa.GetCampaignReportAsync(campaign, range, Ct);
        Assert.Equal(3, ra.TotalEvents);
        Assert.Equal(2, ra.ScoredEvents);
        Assert.Equal(2, ra.Blocked);
        Assert.Equal(85.0, ra.AvgScore, 0.01);

        var fa = await qa.GetTopFlaggedSourcesAsync(range, 10, Ct);
        var fasrc = Assert.Single(fa);
        Assert.True(SameIp(fasrc.SourceValue, ip));
        Assert.Equal(2, fasrc.BlockedEvents);
        Assert.Equal(2, fasrc.TotalEvents);

        // and tenant B sees ONLY tenant B numbers
        Assert.Equal(4, vb.ClickCount);
        Assert.Equal(1, vb.FlaggedCount);

        var rb = await qb.GetCampaignReportAsync(campaign, range, Ct);
        Assert.Equal(4, rb.TotalEvents);
        Assert.Equal(1, rb.ScoredEvents);
        Assert.Equal(1, rb.Blocked);
        Assert.Equal(99.0, rb.AvgScore, 0.01);

        var fb = await qb.GetTopFlaggedSourcesAsync(range, 10, Ct);
        var fbsrc = Assert.Single(fb);
        Assert.True(SameIp(fbsrc.SourceValue, ip));
        Assert.Equal(1, fbsrc.BlockedEvents);
        Assert.Equal(1, fbsrc.TotalEvents);
    }

    [Fact]
    public async Task SdkNumerics_AbsentValues_RoundTripAsNaN()
    {
        // Pixel-mode event: every SDK float stays at its NaN default (§7).
        var ip = UniqueIpv4();
        var session = Unique("s");
        var now = DateTime.UtcNow;
        await WriteAsync(TestEvents.Create(TenantA, ip, session, kind: EventKind.Pixel,
            timestampUtc: now.AddMinutes(-1)));

        // wait for row visibility via the public query surface first
        var queries = fx.CreateQueries(TenantA, now);
        var stats = await EventuallyAsync(
            () => queries.GetIpVelocityAsync(ip, TimeSpan.FromHours(1), Ct),
            s => s.ClickCount >= 1);
        Assert.Equal(1, stats.ClickCount);

        var stored = await fx.ReadStorageAgeSecAsync(TenantA, session);
        // Missing != zero: NaN must survive write -> store -> readback unchanged.
        Assert.True(float.IsNaN(stored),
            $"storage_age_sec must round-trip as NaN for absent SDK values, was {stored}");
    }

    [Fact]
    public async Task LabelSink_WritesAreEventuallyStored()
    {
        var session = Unique("lbl");
        await fx.LabelSink.WriteAsync(
            new LabelEvent(TenantA, session, LabelValues.Fraud, LabelSources.T1Rule, DateTime.UtcNow), Ct);

        var count = await EventuallyAsync(() => fx.CountLabelsAsync(TenantA), c => c >= 1);
        Assert.True(count >= 1, "label row must become visible within the 5 s eventual-delivery window");
    }
}
