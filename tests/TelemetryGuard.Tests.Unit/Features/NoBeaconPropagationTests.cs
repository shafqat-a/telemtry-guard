using TelemetryGuard.RiskEngine.Contracts;
using TelemetryGuard.RiskEngine.Enrichment;
using TelemetryGuard.RiskEngine.Features;
using TelemetryGuard.RiskEngine.Velocity;

namespace TelemetryGuard.Tests.Unit.Features;

/// <summary>Asserts the NaN-propagation table (RSK-04 step 11) row by row.
/// Missing ≠ zero (spec §7): degraded inputs become NaN/null, never 0/false.</summary>
public class NoBeaconPropagationTests
{
    [Fact]
    public void NoBeacon_MatchesPropagationTableExactly()
    {
        // Enrichment timezone present so TimezoneIpMismatch nullness comes from the
        // BEACON side; velocity non-zero so counters prove they are never NaN-ed.
        var raw = new RawSessionDataBuilder()
            .WithUserAgent(RawSessionDataBuilder.ChromeWindowsUa)
            .WithEnrichment(new IpEnrichment { TimeZone = "Europe/Berlin" })
            .WithVelocity(new VelocitySnapshot(3, 2, 1, 4, 5))
            .Build(); // Beacon = null

        var vector = RawSessionDataBuilder.Extractor().Extract(raw);

        Assert.False(vector.HasJsBeacon);

        // NaN row — every SDK-derived float.
        Assert.True(float.IsNaN(vector.MousePathLinearity));
        Assert.True(float.IsNaN(vector.MeanInterEventMs));
        Assert.True(float.IsNaN(vector.StdInterEventMs));
        Assert.True(float.IsNaN(vector.FirstInteractionDelayMs));
        Assert.True(float.IsNaN(vector.TimeOnPageSec));
        Assert.True(float.IsNaN(vector.FormFillTimeSec));
        Assert.True(float.IsNaN(vector.StorageAgeZeroRepeat));
        Assert.True(float.IsNaN(vector.InputEventCount));
        Assert.True(float.IsNaN(vector.ScrollEvents));
        Assert.True(float.IsNaN(vector.PagesViewed));

        // null row — every SDK-derived bool?.
        Assert.Null(vector.HoneypotTouched);
        Assert.Null(vector.ClickBeforeRender);
        Assert.Null(vector.PointerUntrusted);
        Assert.Null(vector.WebdriverFlag);
        Assert.Null(vector.HeadlessBrowser);
        Assert.Null(vector.BeaconIntegrityOk);
        Assert.Null(vector.ScreenResAnomalous);
        Assert.Null(vector.CookiesDisabled);
        Assert.Null(vector.CanvasFpBlocked);
        Assert.Null(vector.PasteInIdentityFields);
        Assert.Null(vector.TimezoneIpMismatch); // beacon side missing
        Assert.Null(vector.InputModalityMismatch);
        Assert.Null(vector.FormSubmitted);
        Assert.Null(vector.AutofillDetected);

        // Velocity members are NEVER NaN — legitimately non-zero here.
        Assert.Equal(3, vector.IpClicksLastMin);
        Assert.Equal(2, vector.IpDistinctUasLastHour);
        Assert.Equal(1, vector.DeviceSessionsLastHour);
        Assert.Equal(4, vector.DeviceIdsThisIpHour);
    }

    [Fact]
    public void ColdVelocity_CountersAreZero_NeverNaN()
    {
        var raw = new RawSessionDataBuilder().Build();

        var vector = RawSessionDataBuilder.Extractor().Extract(raw);

        Assert.Equal(0, vector.IpClicksLastMin);
        Assert.Equal(0, vector.IpDistinctUasLastHour);
        Assert.Equal(0, vector.DeviceSessionsLastHour);
        Assert.Equal(0, vector.DeviceIdsThisIpHour);
    }

    [Fact]
    public void BeaconWithoutVisitorId_StorageAgeZeroRepeatIsNaN()
    {
        var beacon = RawSessionDataBuilder.HumanBeacon() with { VisitorId = null };
        var raw = new RawSessionDataBuilder()
            .WithBeacon(beacon)
            .WithVelocity(new VelocitySnapshot(0, 0, 0, 0, 6))
            .Build();

        Assert.True(float.IsNaN(RawSessionDataBuilder.Extractor().Extract(raw).StorageAgeZeroRepeat));
    }

    [Fact]
    public void BeaconWithVisitorId_StorageAgeZeroRepeatFromVelocity()
    {
        var raw = new RawSessionDataBuilder()
            .WithBeacon(RawSessionDataBuilder.HumanBeacon())
            .WithVelocity(new VelocitySnapshot(0, 0, 0, 0, 6))
            .Build();

        Assert.Equal(6f, RawSessionDataBuilder.Extractor().Extract(raw).StorageAgeZeroRepeat);
    }

    [Fact]
    public void EnrichmentEmpty_IpSignalsNull_AsnUnknown()
    {
        var raw = new RawSessionDataBuilder()
            .WithBeacon(RawSessionDataBuilder.HumanBeacon()) // beacon tz present — null comes from IP side
            .WithCampaign("DE", "AT")                        // targets present — null comes from country
            .WithHeader("Accept-Language", "de-DE,de;q=0.9") // language present — null comes from country
            .Build(); // IpEnrichment.Empty default

        var vector = RawSessionDataBuilder.Extractor().Extract(raw);

        Assert.Null(vector.IpTor);
        Assert.Null(vector.IpProxyOrVpn);
        Assert.Null(vector.IpDatacenterAsn);
        Assert.Null(vector.IpGeoTargetMismatch);
        Assert.Null(vector.TimezoneIpMismatch);
        Assert.Null(vector.LanguageGeoMismatch);
        Assert.Equal(AsnType.Unknown, vector.AsnType);
    }

    [Fact]
    public void UserAgentNull_UaDerivedSignalsNull_IncludingModality()
    {
        // HumanBeacon has 17 input events (>= 10) so modality nullness comes from IsMobile.
        var raw = new RawSessionDataBuilder()
            .WithUserAgent(null)
            .WithBeacon(RawSessionDataBuilder.HumanBeacon())
            .Build();

        var vector = RawSessionDataBuilder.Extractor().Extract(raw);

        Assert.Null(vector.UaOsMismatch);
        Assert.Null(vector.EmulatorOrVm);
        Assert.Null(vector.IsMobile);
        Assert.Null(vector.InputModalityMismatch);
    }

    [Fact]
    public void Organic_ClickIdInvalidNull_IsPaidClickFalse()
    {
        var vector = RawSessionDataBuilder.Extractor().Extract(new RawSessionDataBuilder().Build());

        Assert.Null(vector.ClickIdInvalid);
        Assert.False(vector.IsPaidClick);
    }

    [Fact]
    public void ReferrerMissing_TrueWhenAbsentOrEmpty_FalseWhenPresent()
    {
        var extractor = RawSessionDataBuilder.Extractor();

        Assert.True(extractor.Extract(new RawSessionDataBuilder().Build()).ReferrerMissing);
        Assert.True(extractor.Extract(new RawSessionDataBuilder().WithHeader("Referer", "").Build()).ReferrerMissing);
        Assert.False(extractor.Extract(
            new RawSessionDataBuilder().WithHeader("Referer", "https://ads.example/lp").Build()).ReferrerMissing);
    }

    [Fact]
    public void IpReputationBad_IsConstantZero_NoProducerExists()
    {
        // TODO(P2-06) upstream: constant 0 until a reputation store exists.
        var vector = RawSessionDataBuilder.Extractor().Extract(new RawSessionDataBuilder().Build());

        Assert.Equal(0f, vector.IpReputationBad);
    }

    [Fact]
    public void ChallengeOutcome_PassedThrough()
    {
        var raw = new RawSessionDataBuilder().WithChallengeOutcome(ChallengeOutcome.Passed).Build();

        Assert.Equal(ChallengeOutcome.Passed, RawSessionDataBuilder.Extractor().Extract(raw).ChallengeOutcome);
    }
}
