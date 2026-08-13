using TelemetryGuard.RiskEngine.Features;

namespace TelemetryGuard.Tests.Unit.Features;

/// <summary>Step-6 UA-derived heuristics against real UA strings (Chrome/Windows,
/// Safari/iOS, Android emulator, HeadlessChrome) through the public extractor.</summary>
public class UaHeuristicsTests
{
    private static readonly FeatureExtractor Extractor = RawSessionDataBuilder.Extractor();

    // ---- ua_os_mismatch ----

    [Fact]
    public void ChromeWindows_PlatformHeaderWindows_NoMismatch()
    {
        var raw = new RawSessionDataBuilder()
            .WithUserAgent(RawSessionDataBuilder.ChromeWindowsUa)
            .WithHeader("Sec-CH-UA-Platform", "\"Windows\"")
            .Build();

        Assert.False(Extractor.Extract(raw).UaOsMismatch);
    }

    [Fact]
    public void ChromeWindows_PlatformHeaderMacos_Mismatch()
    {
        var raw = new RawSessionDataBuilder()
            .WithUserAgent(RawSessionDataBuilder.ChromeWindowsUa)
            .WithHeader("Sec-CH-UA-Platform", "\"macOS\"")
            .Build();

        Assert.True(Extractor.Extract(raw).UaOsMismatch);
    }

    [Fact]
    public void PlatformHeaderAbsent_UaOsMismatchNull()
    {
        var raw = new RawSessionDataBuilder()
            .WithUserAgent(RawSessionDataBuilder.ChromeWindowsUa)
            .Build();

        Assert.Null(Extractor.Extract(raw).UaOsMismatch);
    }

    [Fact]
    public void PlatformHeaderUnknownValue_UaOsMismatchNull()
    {
        var raw = new RawSessionDataBuilder()
            .WithUserAgent(RawSessionDataBuilder.ChromeWindowsUa)
            .WithHeader("Sec-CH-UA-Platform", "\"Unknown\"")
            .Build();

        Assert.Null(Extractor.Extract(raw).UaOsMismatch);
    }

    [Fact]
    public void IpadUa_PlatformHeaderIos_NoMismatch_IpadOsNormalizedToIos()
    {
        var raw = new RawSessionDataBuilder()
            .WithUserAgent(RawSessionDataBuilder.IpadSafariUa) // DeviceDetector reports iPadOS
            .WithHeader("Sec-CH-UA-Platform", "\"iOS\"")
            .Build();

        Assert.False(Extractor.Extract(raw).UaOsMismatch);
    }

    // ---- is_mobile ----

    [Fact]
    public void SafariIphone_IsMobileTrue_ChromeDesktop_False()
    {
        Assert.True(Extractor.Extract(
            new RawSessionDataBuilder().WithUserAgent(RawSessionDataBuilder.SafariIosUa).Build()).IsMobile);
        Assert.False(Extractor.Extract(
            new RawSessionDataBuilder().WithUserAgent(RawSessionDataBuilder.ChromeWindowsUa).Build()).IsMobile);
    }

    [Fact]
    public void SecChUaMobileHeader_WinsOverUa()
    {
        var raw = new RawSessionDataBuilder()
            .WithUserAgent(RawSessionDataBuilder.ChromeWindowsUa)
            .WithHeader("Sec-CH-UA-Mobile", "?1")
            .Build();

        Assert.True(Extractor.Extract(raw).IsMobile);
    }

    // ---- emulator_or_vm ----

    [Fact]
    public void AndroidSdkBuiltForX86_EmulatorTrue()
    {
        var raw = new RawSessionDataBuilder()
            .WithUserAgent(RawSessionDataBuilder.AndroidEmulatorUa)
            .Build();

        Assert.True(Extractor.Extract(raw).EmulatorOrVm);
    }

    [Fact]
    public void SdkGphone_EmulatorTrue()
    {
        var raw = new RawSessionDataBuilder()
            .WithUserAgent(RawSessionDataBuilder.SdkGphoneUa)
            .Build();

        Assert.True(Extractor.Extract(raw).EmulatorOrVm);
    }

    [Fact]
    public void RealDesktopAndPhoneUas_EmulatorFalse()
    {
        Assert.False(Extractor.Extract(
            new RawSessionDataBuilder().WithUserAgent(RawSessionDataBuilder.ChromeWindowsUa).Build()).EmulatorOrVm);
        Assert.False(Extractor.Extract(
            new RawSessionDataBuilder().WithUserAgent(RawSessionDataBuilder.SafariIosUa).Build()).EmulatorOrVm);
    }

    [Fact]
    public void HeadlessChrome_ParsesAsDesktopLinux_NotEmulator()
    {
        var vector = Extractor.Extract(
            new RawSessionDataBuilder().WithUserAgent(RawSessionDataBuilder.HeadlessChromeUa).Build());

        // Headless is the SDK/Botd's signal (headless_browser), not the emulator heuristic's.
        Assert.False(vector.EmulatorOrVm);
        Assert.False(vector.IsMobile);
    }

    // ---- screen_res_anomalous ----

    [Fact]
    public void PlausibleDesktopGeometry_False()
    {
        var raw = new RawSessionDataBuilder()
            .WithUserAgent(RawSessionDataBuilder.ChromeWindowsUa)
            .WithBeacon(RawSessionDataBuilder.HumanBeacon())
            .Build();

        Assert.False(Extractor.Extract(raw).ScreenResAnomalous);
    }

    [Fact]
    public void ViewportLargerThanScreen_True()
    {
        var beacon = RawSessionDataBuilder.HumanBeacon() with { ViewportWidth = 2500 };
        var raw = new RawSessionDataBuilder().WithBeacon(beacon).Build();

        Assert.True(Extractor.Extract(raw).ScreenResAnomalous);
    }

    [Fact]
    public void NonPositiveDevicePixelRatio_True()
    {
        var beacon = RawSessionDataBuilder.HumanBeacon() with { DevicePixelRatio = 0 };
        var raw = new RawSessionDataBuilder().WithBeacon(beacon).Build();

        Assert.True(Extractor.Extract(raw).ScreenResAnomalous);
    }

    [Fact]
    public void ExtremeAspectRatio_True()
    {
        var beacon = RawSessionDataBuilder.HumanBeacon() with
        {
            ScreenWidth = 4000,
            ScreenHeight = 1000,
            ViewportWidth = 4000,
            ViewportHeight = 1000,
        };
        var raw = new RawSessionDataBuilder().WithBeacon(beacon).Build();

        Assert.True(Extractor.Extract(raw).ScreenResAnomalous);
    }

    [Fact]
    public void DesktopWithTinyScreen_True()
    {
        var beacon = RawSessionDataBuilder.HumanBeacon() with
        {
            ScreenWidth = 640,
            ScreenHeight = 480,
            ViewportWidth = 640,
            ViewportHeight = 480,
        };
        var raw = new RawSessionDataBuilder()
            .WithUserAgent(RawSessionDataBuilder.ChromeWindowsUa) // is_mobile == false
            .WithBeacon(beacon)
            .Build();

        Assert.True(Extractor.Extract(raw).ScreenResAnomalous);
    }

    [Fact]
    public void ScreenDimensionsAbsent_Null()
    {
        var beacon = RawSessionDataBuilder.HumanBeacon() with { ScreenWidth = null, ScreenHeight = null };
        var raw = new RawSessionDataBuilder().WithBeacon(beacon).Build();

        Assert.Null(Extractor.Extract(raw).ScreenResAnomalous);
    }

    // ---- input_modality_mismatch ----

    [Fact]
    public void MobileUa_MouseOnlyInput_ModalityMismatchTrue()
    {
        // 11 mouse points, zero touches on a phone — scripted mouse input.
        var beacon = RawSessionDataBuilder.HumanBeacon() with { MmN = 10, TouchCount = 0 };
        var raw = new RawSessionDataBuilder()
            .WithUserAgent(RawSessionDataBuilder.SafariIosUa)
            .WithBeacon(beacon)
            .Build();

        Assert.True(Extractor.Extract(raw).InputModalityMismatch);
    }

    [Fact]
    public void DesktopUa_MouseInput_ModalityMismatchFalse()
    {
        var raw = new RawSessionDataBuilder()
            .WithUserAgent(RawSessionDataBuilder.ChromeWindowsUa)
            .WithBeacon(RawSessionDataBuilder.HumanBeacon())
            .Build();

        Assert.False(Extractor.Extract(raw).InputModalityMismatch);
    }

    [Fact]
    public void TooFewInputEvents_ModalityMismatchNull()
    {
        var beacon = RawSessionDataBuilder.HumanBeacon() with { MmN = 2, ClickCount = 1, KeyCount = 0 };
        var raw = new RawSessionDataBuilder()
            .WithUserAgent(RawSessionDataBuilder.ChromeWindowsUa)
            .WithBeacon(beacon)
            .Build();

        Assert.Null(Extractor.Extract(raw).InputModalityMismatch);
    }
}
