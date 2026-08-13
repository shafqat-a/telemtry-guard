using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Core.Tenancy;

namespace TelemetryGuard.Tests.Unit.Analytics;

public class ClickEventDefaultsTests
{
    private static ClickEvent MinimalEvent() => new()
    {
        TenantId = new TenantId(Guid.NewGuid()),
        SiteKey = "site-key",
        SessionId = "session-1",
        Kind = EventKind.Tracker,
        Ip = "203.0.113.7",
        HasJsBeacon = false,
        RetentionDays = 90,
        TimestampUtc = DateTime.UtcNow
    };

    [Fact]
    public void Defaults_AreAbsent_NotZero()
    {
        var e = MinimalEvent();

        // SDK-derived floats default to NaN (missing != zero, spec §7)
        Assert.True(float.IsNaN(e.StorageAgeSec));
        Assert.True(float.IsNaN(e.MousePathLinearity));
        Assert.True(float.IsNaN(e.ScreenWidth));
        Assert.True(float.IsNaN(e.ScreenHeight));
        Assert.True(float.IsNaN(e.MouseEventCount));
        Assert.True(float.IsNaN(e.KeyEventCount));
        Assert.True(float.IsNaN(e.TouchEventCount));
        Assert.True(float.IsNaN(e.ScrollEventCount));
        Assert.True(float.IsNaN(e.MeanInterEventMs));
        Assert.True(float.IsNaN(e.StdInterEventMs));
        Assert.True(float.IsNaN(e.FirstInteractionDelayMs));
        Assert.True(float.IsNaN(e.FormFillTimeSec));
        Assert.True(float.IsNaN(e.TimeOnPageSec));
        Assert.True(float.IsNaN(e.PagesViewed));

        // Absent SDK bool flags are null, never false
        Assert.Null(e.WebdriverFlag);
        Assert.Null(e.HeadlessBrowser);
        Assert.Null(e.BeaconIntegrityOk);
        Assert.Null(e.AutofillDetected);
        Assert.Null(e.PasteInIdentityFields);
        Assert.Null(e.HoneypotTouched);
        Assert.Null(e.PointerUntrusted);
        Assert.Null(e.InputModalityMismatch);
        Assert.Null(e.ClickIdInvalid);

        // Velocity counters are plain ints; 0 is a legitimate cold value
        Assert.Equal(0, e.IpClicksLastMin);
        Assert.Equal(0, e.IpDistinctUasLastHour);
        Assert.Equal(0, e.DeviceSessionsLastHour);
        Assert.Equal(0, e.DeviceIdsThisIpHour);

        // Collections default to empty, never null (RuleHits.Count == 0)
        Assert.Empty(e.RuleHits);
        Assert.Empty(e.HeaderNames);

        // Click ids default to empty strings
        Assert.Equal("", e.CampaignId);
        Assert.Equal("", e.Gclid);
        Assert.Equal("", e.Fbclid);
        Assert.Equal("", e.Msclkid);
        Assert.Equal("", e.Ttclid);

        // Verdict block empty until Kind == Verdict
        Assert.Null(e.Score);
        Assert.Null(e.Band);
        Assert.Null(e.Action);
        Assert.Null(e.ScorerVersion);
        Assert.Null(e.FeatureSetVersion);
    }

    [Fact]
    public void EventKindWire_MapsAllValues()
    {
        Assert.Equal("tracker", EventKind.Tracker.ToWire());
        Assert.Equal("pixel", EventKind.Pixel.ToWire());
        Assert.Equal("beacon", EventKind.Beacon.ToWire());
        Assert.Equal("verdict", EventKind.Verdict.ToWire());
    }

    [Fact]
    public void EventKindWire_ThrowsOnUndefinedValue()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((EventKind)99).ToWire());
    }

    [Fact]
    public void DateRange_Validates()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc);

        // Valid: UTC bounds, from <= to
        var range = new DateRange(from, to);
        Assert.Equal(from, range.FromUtc);
        Assert.Equal(to, range.ToUtc);

        // Valid: empty interval (from == to) is allowed
        _ = new DateRange(from, from);

        // Non-UTC bounds rejected
        Assert.Throws<ArgumentException>(() =>
            new DateRange(DateTime.SpecifyKind(from, DateTimeKind.Local), to));
        Assert.Throws<ArgumentException>(() =>
            new DateRange(from, DateTime.SpecifyKind(to, DateTimeKind.Unspecified)));

        // Inverted bounds rejected
        Assert.Throws<ArgumentException>(() => new DateRange(to, from));

        // LastDays produces [nowUtc - days, nowUtc)
        var now = new DateTime(2026, 8, 12, 12, 0, 0, DateTimeKind.Utc);
        var last7 = DateRange.LastDays(7, now);
        Assert.Equal(now.AddDays(-7), last7.FromUtc);
        Assert.Equal(now, last7.ToUtc);
    }
}
