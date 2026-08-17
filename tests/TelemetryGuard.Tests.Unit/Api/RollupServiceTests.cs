using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Api.Workers;
using TelemetryGuard.Core.Analytics;

namespace TelemetryGuard.Tests.Unit.Api;

/// <summary>
/// ANA-07 pure-function tests: window math (UTC, watermark replay re-covering
/// the last closed day) and the absolute-value day mapping that never fabricates
/// an average (§7 — missing ≠ zero).
/// </summary>
public sealed class RollupServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 12, 13, 45, 30, DateTimeKind.Utc);

    [Fact]
    public void ComputeWindow_NoWatermark_BackfillsLookbackDays()
    {
        var (fromDay, range) = RollupService.ComputeWindow(null, Now, 3);

        Assert.Equal(new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc), fromDay);
        Assert.Equal(fromDay, range.FromUtc);
        Assert.Equal(Now, range.ToUtc);
    }

    [Fact]
    public void ComputeWindow_WatermarkYesterday_StartsTwoDaysAgo()
    {
        var watermark = new DateTime(2026, 8, 11, 23, 50, 0, DateTimeKind.Utc);

        var (fromDay, range) = RollupService.ComputeWindow(watermark, Now, 3);

        // watermark.Date.AddDays(-1): re-cover the last closed day for late arrivals.
        Assert.Equal(new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc), fromDay);
        Assert.Equal(fromDay, range.FromUtc);
        Assert.Equal(Now, range.ToUtc);
    }

    [Fact]
    public void ComputeWindow_WatermarkToday_StartsYesterday()
    {
        var watermark = new DateTime(2026, 8, 12, 13, 30, 0, DateTimeKind.Utc);

        var (fromDay, range) = RollupService.ComputeWindow(watermark, Now, 3);

        Assert.Equal(new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc), fromDay);
        Assert.Equal(fromDay, range.FromUtc);
        Assert.Equal(Now, range.ToUtc);
    }

    [Fact]
    public void ComputeWindow_AllBounds_AreUtcKind()
    {
        // The watermark comes back from SQL datetime2 with Kind = Unspecified;
        // the window must still be strictly DateTimeKind.Utc (DateRange asserts it).
        var unspecifiedWatermark = new DateTime(2026, 8, 11, 8, 0, 0, DateTimeKind.Unspecified);

        var (fromDayNoWm, rangeNoWm) = RollupService.ComputeWindow(null, Now, 3);
        var (fromDayWm, rangeWm) = RollupService.ComputeWindow(unspecifiedWatermark, Now, 3);

        Assert.Equal(DateTimeKind.Utc, fromDayNoWm.Kind);
        Assert.Equal(DateTimeKind.Utc, rangeNoWm.FromUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, rangeNoWm.ToUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, fromDayWm.Kind);
        Assert.Equal(DateTimeKind.Utc, rangeWm.FromUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, rangeWm.ToUtc.Kind);
    }

    [Fact]
    public void MapDay_ZeroVerdictDay_StoresZeroEventsAndZeroScoreSum_NeverAFabricatedAverage()
    {
        var tenantId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        // CampaignDailyCounts(Day, TotalEvents, ScoredEvents, Allowed, Challenged,
        //                     Blocked, ScoreSum, AvgScore, NoJsBeaconCount)
        var day = new CampaignDailyCounts(
            new DateOnly(2026, 8, 10), 5, 0, 0, 0, 0, 0, double.NaN, 2);

        var row = RollupService.MapDay(tenantId, campaignId, day);

        Assert.Equal(tenantId, row.TenantId);
        Assert.Equal(campaignId, row.CampaignId);
        Assert.Equal(new DateOnly(2026, 8, 10), row.Date);
        Assert.Equal(0, row.Events);     // "no data" comes from Events = 0 ...
        Assert.Equal(0, row.ScoreSum);   // ... never from a defaulted 0.0 average
        Assert.Equal(0, row.Allowed);
        Assert.Equal(0, row.Challenged);
        Assert.Equal(0, row.Blocked);
        Assert.Equal(ScoreHistogramCounts.Empty, row.ScoreHistogram); // REQ-01: no fabricated buckets either
    }

    [Fact]
    public void MapDay_CopiesAbsoluteCounts_AndUsesScoredEventsAsEvents()
    {
        var tenantId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var day = new CampaignDailyCounts(
            new DateOnly(2026, 8, 9),
            TotalEvents: 10, ScoredEvents: 5, Allowed: 2, Challenged: 2, Blocked: 1,
            ScoreSum: 210, AvgScore: 42.0, NoJsBeaconCount: 1);

        var row = RollupService.MapDay(tenantId, campaignId, day);

        Assert.Equal(2, row.Allowed);
        Assert.Equal(2, row.Challenged);
        Assert.Equal(1, row.Blocked);
        Assert.Equal(210, row.ScoreSum);
        Assert.Equal(5, row.Events);     // scored (verdict) events, NOT TotalEvents
    }

    [Fact]
    public void MapDay_CopiesScoreHistogram_AsAnAbsoluteValue_NeverRecomputed()
    {
        // REQ-01: MapDay must pass the ClickHouse-computed histogram straight
        // through — the same "absolute copy, no re-derivation" contract as
        // Allowed/Challenged/Blocked/ScoreSum above.
        var tenantId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var histogram = new ScoreHistogramCounts(
            Bucket00: 1, Bucket10: 0, Bucket20: 0, Bucket30: 0, Bucket40: 0,
            Bucket50: 0, Bucket60: 0, Bucket70: 0, Bucket80: 0, Bucket90: 1,
            Bucket100: 0, SumSq: 8100);
        var day = new CampaignDailyCounts(
            new DateOnly(2026, 8, 9),
            TotalEvents: 2, ScoredEvents: 2, Allowed: 1, Challenged: 0, Blocked: 1,
            ScoreSum: 95, AvgScore: 47.5, NoJsBeaconCount: 0,
            ScoreHistogram: histogram);

        var row = RollupService.MapDay(tenantId, campaignId, day);

        Assert.Equal(histogram, row.ScoreHistogram);
    }

    // ------------------------------------------------------- P2-01: NormalizeHost =

    [Theory]
    [InlineData("https://WWW.Example.COM/path?q=1", "example.com")]
    [InlineData("example.com:8443", "example.com")]
    [InlineData("www.www.example.com", "www.example.com")] // only ONE leading label stripped
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void NormalizeHost_ProducesExpectedHost(string? input, string expected)
    {
        Assert.Equal(expected, RollupService.NormalizeHost(input));
    }
}
