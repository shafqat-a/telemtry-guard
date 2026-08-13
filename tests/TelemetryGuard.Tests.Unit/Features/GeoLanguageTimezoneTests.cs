using TelemetryGuard.RiskEngine.Enrichment;
using TelemetryGuard.RiskEngine.Features;

namespace TelemetryGuard.Tests.Unit.Features;

/// <summary>Step-9 geo/timezone/language mismatches: null-side, match, mismatch,
/// and the English FP-guard.</summary>
public class GeoLanguageTimezoneTests
{
    private static readonly FeatureExtractor Extractor = RawSessionDataBuilder.Extractor();

    // ---- ip_geo_target_mismatch ----

    [Fact]
    public void NoCampaign_Null()
    {
        var raw = new RawSessionDataBuilder()
            .WithEnrichment(new IpEnrichment { CountryCode = "DE" })
            .Build();

        Assert.Null(Extractor.Extract(raw).IpGeoTargetMismatch);
    }

    [Fact]
    public void CampaignWithoutGeoTargets_Null()
    {
        var raw = new RawSessionDataBuilder()
            .WithCampaign() // empty targets
            .WithEnrichment(new IpEnrichment { CountryCode = "DE" })
            .Build();

        Assert.Null(Extractor.Extract(raw).IpGeoTargetMismatch);
    }

    [Fact]
    public void CountryUnknown_Null()
    {
        var raw = new RawSessionDataBuilder().WithCampaign("DE", "AT").Build();

        Assert.Null(Extractor.Extract(raw).IpGeoTargetMismatch);
    }

    [Fact]
    public void CountryInTargets_False_CaseInsensitive()
    {
        var raw = new RawSessionDataBuilder()
            .WithCampaign("DE", "AT")
            .WithEnrichment(new IpEnrichment { CountryCode = "de" })
            .Build();

        Assert.False(Extractor.Extract(raw).IpGeoTargetMismatch);
    }

    [Fact]
    public void CountryOutsideTargets_True()
    {
        var raw = new RawSessionDataBuilder()
            .WithCampaign("DE", "AT")
            .WithEnrichment(new IpEnrichment { CountryCode = "RU" })
            .Build();

        Assert.True(Extractor.Extract(raw).IpGeoTargetMismatch);
    }

    // ---- timezone_ip_mismatch ----

    [Fact]
    public void NeighboringZones_SameBaseOffset_False()
    {
        var beacon = RawSessionDataBuilder.HumanBeacon() with { Timezone = "Europe/Berlin" };
        var raw = new RawSessionDataBuilder()
            .WithBeacon(beacon)
            .WithEnrichment(new IpEnrichment { TimeZone = "Europe/Paris" })
            .Build();

        Assert.False(Extractor.Extract(raw).TimezoneIpMismatch);
    }

    [Fact]
    public void FarApartZones_True()
    {
        var beacon = RawSessionDataBuilder.HumanBeacon() with { Timezone = "Europe/Berlin" };
        var raw = new RawSessionDataBuilder()
            .WithBeacon(beacon)
            .WithEnrichment(new IpEnrichment { TimeZone = "America/New_York" })
            .Build();

        Assert.True(Extractor.Extract(raw).TimezoneIpMismatch);
    }

    [Fact]
    public void BeaconTimezoneMissing_Null()
    {
        var beacon = RawSessionDataBuilder.HumanBeacon() with { Timezone = null };
        var raw = new RawSessionDataBuilder()
            .WithBeacon(beacon)
            .WithEnrichment(new IpEnrichment { TimeZone = "Europe/Berlin" })
            .Build();

        Assert.Null(Extractor.Extract(raw).TimezoneIpMismatch);
    }

    [Fact]
    public void IpTimezoneMissing_Null()
    {
        var raw = new RawSessionDataBuilder()
            .WithBeacon(RawSessionDataBuilder.HumanBeacon())
            .Build(); // IpEnrichment.Empty — TimeZone null

        Assert.Null(Extractor.Extract(raw).TimezoneIpMismatch);
    }

    [Fact]
    public void UnresolvableZoneId_Null()
    {
        var beacon = RawSessionDataBuilder.HumanBeacon() with { Timezone = "Not/AZone" };
        var raw = new RawSessionDataBuilder()
            .WithBeacon(beacon)
            .WithEnrichment(new IpEnrichment { TimeZone = "Europe/Berlin" })
            .Build();

        Assert.Null(Extractor.Extract(raw).TimezoneIpMismatch);
    }

    // ---- language_geo_mismatch ----

    [Fact]
    public void AcceptLanguageMatchesCountry_False()
    {
        var raw = new RawSessionDataBuilder()
            .WithHeader("Accept-Language", "de-DE,de;q=0.9")
            .WithEnrichment(new IpEnrichment { CountryCode = "DE" })
            .Build();

        Assert.False(Extractor.Extract(raw).LanguageGeoMismatch);
    }

    [Fact]
    public void AcceptLanguageImplausibleForCountry_True()
    {
        var raw = new RawSessionDataBuilder()
            .WithHeader("Accept-Language", "ru-RU,ru;q=0.9")
            .WithEnrichment(new IpEnrichment { CountryCode = "DE" })
            .Build();

        Assert.True(Extractor.Extract(raw).LanguageGeoMismatch);
    }

    [Fact]
    public void English_GloballyPlausible_False_EvenWhereNotListed()
    {
        // RU's plausible list is ["ru"] — the explicit FP guard still yields false.
        var raw = new RawSessionDataBuilder()
            .WithHeader("Accept-Language", "en-US,en;q=0.9")
            .WithEnrichment(new IpEnrichment { CountryCode = "RU" })
            .Build();

        Assert.False(Extractor.Extract(raw).LanguageGeoMismatch);
    }

    [Fact]
    public void NoHeader_FallsBackToBeaconLanguage()
    {
        var beacon = RawSessionDataBuilder.HumanBeacon() with { Language = "de-DE" };
        var raw = new RawSessionDataBuilder()
            .WithBeacon(beacon)
            .WithEnrichment(new IpEnrichment { CountryCode = "DE" })
            .Build();

        Assert.False(Extractor.Extract(raw).LanguageGeoMismatch);
    }

    [Fact]
    public void CountryNotInMap_Null()
    {
        var raw = new RawSessionDataBuilder()
            .WithHeader("Accept-Language", "de-DE")
            .WithEnrichment(new IpEnrichment { CountryCode = "XK" }) // not in the seed map
            .Build();

        Assert.Null(Extractor.Extract(raw).LanguageGeoMismatch);
    }

    [Fact]
    public void NoLanguageAnywhere_Null()
    {
        var beacon = RawSessionDataBuilder.HumanBeacon() with { Language = null };
        var raw = new RawSessionDataBuilder()
            .WithBeacon(beacon)
            .WithEnrichment(new IpEnrichment { CountryCode = "DE" })
            .Build();

        Assert.Null(Extractor.Extract(raw).LanguageGeoMismatch);
    }

    [Fact]
    public void WildcardAcceptLanguage_FallsBackThenNull()
    {
        var beacon = RawSessionDataBuilder.HumanBeacon() with { Language = null };
        var raw = new RawSessionDataBuilder()
            .WithHeader("Accept-Language", "*")
            .WithBeacon(beacon)
            .WithEnrichment(new IpEnrichment { CountryCode = "DE" })
            .Build();

        Assert.Null(Extractor.Extract(raw).LanguageGeoMismatch);
    }
}
