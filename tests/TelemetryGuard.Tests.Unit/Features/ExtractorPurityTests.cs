using TelemetryGuard.RiskEngine.Enrichment;
using TelemetryGuard.RiskEngine.Features;
using TelemetryGuard.RiskEngine.Velocity;

namespace TelemetryGuard.Tests.Unit.Features;

/// <summary>Extract is a pure function: no I/O, no clock, no randomness — the same
/// RawSessionData must produce record-equal vectors on every call, on every instance
/// (the UA parse cache must not change outputs between cold and warm hits).</summary>
public class ExtractorPurityTests
{
    private static RawSessionData FullyLoadedSession() => new RawSessionDataBuilder()
        .WithUserAgent(RawSessionDataBuilder.ChromeWindowsUa)
        .WithHeader("Sec-CH-UA-Platform", "\"Windows\"")
        .WithHeader("Sec-CH-UA-Mobile", "?0")
        .WithHeader("Accept-Language", "de-DE,de;q=0.9")
        .WithHeader("Referer", "https://ads.example/lp")
        .WithTlsFingerprint("473cd7cb9faa642487833865d516e578")
        .AsPaidClick("gclid-abc", clickIdFresh: true)
        .WithBeacon(RawSessionDataBuilder.HumanBeacon())
        .WithEnrichment(new IpEnrichment
        {
            CountryCode = "DE",
            TimeZone = "Europe/Berlin",
            IsProxyOrVpn = false,
            IsTor = false,
            IsDatacenter = false,
        })
        .WithVelocity(new VelocitySnapshot(2, 1, 1, 1, 1))
        .WithCampaign("DE", "AT")
        .Build();

    [Fact]
    public void SameInput_SameInstance_EqualVectors()
    {
        var extractor = RawSessionDataBuilder.Extractor(new FeatureExtractionOptions { TlsUaMismatchEnabled = true });
        var raw = FullyLoadedSession();

        var first = extractor.Extract(raw);
        var second = extractor.Extract(raw); // warm UA cache

        Assert.Equal(first, second); // record equality; NaN == NaN under EqualityComparer
    }

    [Fact]
    public void SameInput_DifferentInstances_EqualVectors()
    {
        var raw = FullyLoadedSession();
        var options = new FeatureExtractionOptions { TlsUaMismatchEnabled = true };

        var first = RawSessionDataBuilder.Extractor(options).Extract(raw);
        var second = RawSessionDataBuilder.Extractor(options).Extract(raw); // cold UA cache

        Assert.Equal(first, second);
    }

    [Fact]
    public void NoBeaconInput_Deterministic()
    {
        var extractor = RawSessionDataBuilder.Extractor();
        var raw = new RawSessionDataBuilder().Build();

        Assert.Equal(extractor.Extract(raw), extractor.Extract(raw));
    }
}
