using System.Net;

namespace TelemetryGuard.Api.Edge;

/// <summary>INT-05: the embedded, refreshable Cloudflare IP range list (spec D13).
/// Loaded once from <c>Edge/cloudflare-ips.txt</c> (an EmbeddedResource — never
/// hardcoded at call sites) and cached for the process lifetime; refreshed offline via
/// <c>scripts/update-cloudflare-ips.sh</c>, not at runtime. Two consumers: Program.cs
/// merges these ranges into ForwardedHeaders' trusted-proxy set when
/// <c>Edge:Provider == Cloudflare</c>, and <see cref="EdgeSignalReader"/> uses
/// <see cref="Contains"/> as the second spoof-defense layer (the direct peer must be a
/// Cloudflare address before any X-TG-* header is honored).</summary>
public static class CloudflareIpRanges
{
    private const string ResourceName = "TelemetryGuard.Api.Edge.cloudflare-ips.txt";

    private static readonly Lazy<IReadOnlyList<(IPAddress Address, int PrefixLength)>> Cache =
        new(LoadFromEmbeddedResource);

    /// <summary>Parses the embedded cloudflare-ips.txt into (address, prefix) pairs.
    /// Blank lines and '#'-comments are skipped. Cached after first call.</summary>
    public static IReadOnlyList<(IPAddress Address, int PrefixLength)> Load() => Cache.Value;

    /// <summary>True when <paramref name="ip"/> falls inside any Cloudflare range
    /// (v4 or v6 — address families never cross-match).</summary>
    public static bool Contains(IPAddress ip)
    {
        foreach (var (network, prefixLength) in Load())
        {
            if (IsInSubnet(ip, network, prefixLength))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInSubnet(IPAddress address, IPAddress network, int prefixLength)
    {
        if (address.AddressFamily != network.AddressFamily)
        {
            return false;
        }

        var addressBytes = address.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();
        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (addressBytes[i] != networkBytes[i])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (addressBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
    }

    private static List<(IPAddress Address, int PrefixLength)> LoadFromEmbeddedResource()
    {
        var ranges = new List<(IPAddress, int)>();
        using var stream = typeof(CloudflareIpRanges).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var parts = trimmed.Split('/');
            if (parts.Length != 2
                || !IPAddress.TryParse(parts[0], out var address)
                || !int.TryParse(parts[1], out var prefixLength))
            {
                continue; // malformed line — never crash startup over a bad refresh
            }

            ranges.Add((address, prefixLength));
        }

        return ranges;
    }
}
