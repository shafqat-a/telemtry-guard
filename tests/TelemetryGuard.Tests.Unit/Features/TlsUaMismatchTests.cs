using TelemetryGuard.RiskEngine.Features;

namespace TelemetryGuard.Tests.Unit.Features;

/// <summary>Step-8 config-gated JA3/JA4 map (D13). Gate is OFF by default until INT-05
/// (Cloudflare fronting) supplies the headers; unknown fingerprints prove nothing.</summary>
public class TlsUaMismatchTests
{
    private const string GoHttpJa3 = "473cd7cb9faa642487833865d516e578";  // seeded "go-http"
    private const string ChromeJa3 = "cd08e31494f9531f560d64c695473da9";  // seeded "chrome"
    private const string UnknownJa3 = "ffffffffffffffffffffffffffffffff"; // not in the map

    private static FeatureExtractionOptions GateOn => new() { TlsUaMismatchEnabled = true };

    [Fact]
    public void GateOffByDefault_AlwaysNull()
    {
        var raw = new RawSessionDataBuilder()
            .WithUserAgent(RawSessionDataBuilder.ChromeWindowsUa)
            .WithTlsFingerprint(GoHttpJa3)
            .Build();

        Assert.Null(RawSessionDataBuilder.Extractor().Extract(raw).TlsUaMismatch);
    }

    [Fact]
    public void GateOn_NoFingerprintHeader_Null()
    {
        var raw = new RawSessionDataBuilder()
            .WithUserAgent(RawSessionDataBuilder.ChromeWindowsUa)
            .WithTlsFingerprint(null)
            .Build();

        Assert.Null(RawSessionDataBuilder.Extractor(GateOn).Extract(raw).TlsUaMismatch);
    }

    [Fact]
    public void GateOn_UnknownFingerprint_Null_UnknownProvesNothing()
    {
        var raw = new RawSessionDataBuilder()
            .WithUserAgent(RawSessionDataBuilder.ChromeWindowsUa)
            .WithTlsFingerprint(UnknownJa3)
            .Build();

        Assert.Null(RawSessionDataBuilder.Extractor(GateOn).Extract(raw).TlsUaMismatch);
    }

    [Fact]
    public void GateOn_GoHttpHandshake_ChromeUa_True()
    {
        // "UA says Chrome, TLS handshake says Go binary" — D13's canonical example.
        var raw = new RawSessionDataBuilder()
            .WithUserAgent(RawSessionDataBuilder.ChromeWindowsUa)
            .WithTlsFingerprint(GoHttpJa3)
            .Build();

        Assert.True(RawSessionDataBuilder.Extractor(GateOn).Extract(raw).TlsUaMismatch);
    }

    [Fact]
    public void GateOn_MatchingBrowserFamily_False()
    {
        var raw = new RawSessionDataBuilder()
            .WithUserAgent(RawSessionDataBuilder.ChromeWindowsUa)
            .WithTlsFingerprint(ChromeJa3)
            .Build();

        Assert.False(RawSessionDataBuilder.Extractor(GateOn).Extract(raw).TlsUaMismatch);
    }

    [Fact]
    public void GateOn_BrowserHandshake_DifferentBrowserUa_True()
    {
        var raw = new RawSessionDataBuilder()
            .WithUserAgent(RawSessionDataBuilder.FirefoxMacUa)
            .WithTlsFingerprint(ChromeJa3)
            .Build();

        Assert.True(RawSessionDataBuilder.Extractor(GateOn).Extract(raw).TlsUaMismatch);
    }

    [Fact]
    public void GateOn_NonBrowserHandshake_NonBrowserUa_False()
    {
        // curl UA does not claim to be a browser — a go-http handshake contradicts nothing.
        var raw = new RawSessionDataBuilder()
            .WithUserAgent(RawSessionDataBuilder.CurlUa)
            .WithTlsFingerprint(GoHttpJa3)
            .Build();

        Assert.False(RawSessionDataBuilder.Extractor(GateOn).Extract(raw).TlsUaMismatch);
    }

    [Fact]
    public void GateOn_NoUserAgent_Null()
    {
        var raw = new RawSessionDataBuilder()
            .WithUserAgent(null)
            .WithTlsFingerprint(GoHttpJa3)
            .Build();

        Assert.Null(RawSessionDataBuilder.Extractor(GateOn).Extract(raw).TlsUaMismatch);
    }
}
