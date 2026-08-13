using System.Collections.Concurrent;
using DeviceDetectorNET;

namespace TelemetryGuard.RiskEngine.Features;

/// <summary>Parsed User-Agent facts. OsFamily/BrowserFamily are normalized (see
/// <see cref="UaHeuristics.NormalizeOsFamily"/> / <see cref="UaHeuristics.NormalizeBrowserFamily"/>);
/// null = not detected. Brand is carried for the Generic-brand emulator check (step 6).</summary>
internal sealed record UaInfo(
    string? OsFamily,
    string? BrowserFamily,
    bool IsMobileDevice,
    string? DeviceModel,
    bool IsBot,
    string? Brand);

/// <summary>DeviceDetector.NET wrapper with a bounded parse cache — parsing is not free and
/// UAs repeat heavily. The cache lives in the extractor instance and does not make
/// extraction impure: same input → same output regardless of hit or miss.</summary>
internal sealed class UaHeuristics
{
    private const int MaxCacheEntries = 10_000;

    private readonly ConcurrentDictionary<string, UaInfo> _cache = new(StringComparer.Ordinal);

    private static readonly HashSet<string> MobileDeviceTypes =
        new(StringComparer.OrdinalIgnoreCase) { "smartphone", "tablet", "phablet" };

    // Heuristic seed list for emulator_or_vm — extend during listen-only tuning.
    private static readonly string[] EmulatorMarkers =
    {
        "Android SDK built for x86",
        "sdk_gphone",
        "Emulator",
        "Genymotion",
        "Droid4X",
        "Nox",
        "BlueStacks",
        "MuMu",
        "Andy",
    };

    // DeviceDetector OS name → normalized family {Windows, macOS, Android, iOS, Linux, Chrome OS}.
    // Also covers Sec-CH-UA-Platform values after quote-stripping ("Chromium OS", "iPadOS" → …).
    private static readonly Dictionary<string, string> OsFamilyMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Windows"] = "Windows",
            ["Mac"] = "macOS",
            ["macOS"] = "macOS",
            ["Mac OS X"] = "macOS",
            ["OS X"] = "macOS",
            ["iOS"] = "iOS",
            ["iPadOS"] = "iOS",
            ["tvOS"] = "iOS",
            ["watchOS"] = "iOS",
            ["Android"] = "Android",
            ["Android TV"] = "Android",
            ["GNU/Linux"] = "Linux",
            ["Linux"] = "Linux",
            ["Ubuntu"] = "Linux",
            ["Kubuntu"] = "Linux",
            ["Xubuntu"] = "Linux",
            ["Lubuntu"] = "Linux",
            ["Linux Mint"] = "Linux",
            ["Debian"] = "Linux",
            ["Fedora"] = "Linux",
            ["Arch Linux"] = "Linux",
            ["CentOS"] = "Linux",
            ["Red Hat"] = "Linux",
            ["openSUSE"] = "Linux",
            ["SUSE"] = "Linux",
            ["Gentoo"] = "Linux",
            ["Slackware"] = "Linux",
            ["Mageia"] = "Linux",
            ["Manjaro"] = "Linux",
            ["elementary OS"] = "Linux",
            ["Deepin"] = "Linux",
            ["Chrome OS"] = "Chrome OS",
            ["ChromeOS"] = "Chrome OS",
            ["Chromium OS"] = "Chrome OS",
        };

    public UaInfo Parse(string ua)
    {
        if (_cache.TryGetValue(ua, out var cached)) return cached;

        var info = ParseUncached(ua);

        // Bounded: stop adding when full instead of evicting — deterministic and lock-free.
        if (_cache.Count < MaxCacheEntries) _cache.TryAdd(ua, info);
        return info;
    }

    private static UaInfo ParseUncached(string ua)
    {
        var detector = new DeviceDetector(ua);
        detector.Parse();

        string? osFamily = null;
        var os = detector.GetOs();
        if (os.Success) osFamily = NormalizeOsFamily(os.Match.Name);

        string? browserFamily = null;
        var browser = detector.GetBrowserClient();
        if (browser.Success) browserFamily = NormalizeBrowserFamily(browser.Match.Name);

        var deviceName = detector.GetDeviceName();
        var isMobileDevice = !string.IsNullOrEmpty(deviceName) && MobileDeviceTypes.Contains(deviceName);

        var model = detector.GetModel();
        var brand = detector.GetBrandName();

        return new UaInfo(
            osFamily,
            browserFamily,
            isMobileDevice,
            string.IsNullOrEmpty(model) ? null : model,
            detector.IsBot(),
            string.IsNullOrEmpty(brand) ? null : brand);
    }

    /// <summary>Normalizes an OS name (DeviceDetector or Sec-CH-UA-Platform) to
    /// {Windows, macOS, Android, iOS, Linux, Chrome OS}; null = unmapped/indeterminate
    /// (including the Client Hints "Unknown" value).</summary>
    internal static string? NormalizeOsFamily(string? osName)
    {
        if (string.IsNullOrWhiteSpace(osName)) return null;
        return OsFamilyMap.TryGetValue(osName.Trim(), out var family) ? family : null;
    }

    /// <summary>Normalizes a DeviceDetector browser NAME to a lowercase family
    /// {chrome, firefox, safari, edge, opera}; null = not a recognized browser.
    /// Name-based on purpose: DeviceDetector's own Family labels Microsoft Edge as
    /// "Internet Explorer", which would corrupt the tls_ua_mismatch comparison.
    /// Order matters — "Microsoft Edge" and "Opera" UAs also mention Chrome/Safari.</summary>
    internal static string? NormalizeBrowserFamily(string? browserName)
    {
        if (string.IsNullOrWhiteSpace(browserName)) return null;
        if (browserName.Contains("Edg", StringComparison.OrdinalIgnoreCase)) return "edge";
        if (browserName.Contains("Opera", StringComparison.OrdinalIgnoreCase)) return "opera";
        if (browserName.Contains("Chrom", StringComparison.OrdinalIgnoreCase)) return "chrome";
        if (browserName.Contains("Firefox", StringComparison.OrdinalIgnoreCase)) return "firefox";
        if (browserName.Contains("Safari", StringComparison.OrdinalIgnoreCase)) return "safari";
        return null;
    }

    /// <summary>Step-6 emulator/VM heuristic: marker substring in the UA or the
    /// DeviceDetector model, or DeviceDetector brand "Generic" on an Android x86 UA.</summary>
    internal static bool IsEmulatorOrVm(string ua, UaInfo info)
    {
        foreach (var marker in EmulatorMarkers)
        {
            if (ua.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
            if (info.DeviceModel is not null
                && info.DeviceModel.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return string.Equals(info.Brand, "Generic", StringComparison.OrdinalIgnoreCase)
            && ua.Contains("Android", StringComparison.OrdinalIgnoreCase)
            && (ua.Contains("x86", StringComparison.OrdinalIgnoreCase)
                || ua.Contains("i686", StringComparison.OrdinalIgnoreCase));
    }
}
