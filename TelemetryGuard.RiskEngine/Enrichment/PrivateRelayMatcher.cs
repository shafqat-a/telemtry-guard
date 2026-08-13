using System.Buffers.Binary;
using System.Net;

namespace TelemetryGuard.RiskEngine.Enrichment;

/// <summary>Membership test over Apple's published iCloud Private Relay egress ranges
/// (https://mask-api.icloud.com/egress-ip-ranges.csv, format "CIDR,country,region,city,").
/// CIDRs are parsed into pre-masked integer buckets split by address family;
/// Contains(IPAddress) is an allocation-free linear prefix compare — a few thousand
/// ranges scan in well under 10 µs, so no trie or external package is needed.</summary>
internal sealed class PrivateRelayMatcher
{
    internal const string EmbeddedResourceName =
        "TelemetryGuard.RiskEngine.Enrichment.Data.apple-private-relay-seed.csv";

    private readonly V4Range[] _v4;
    private readonly V6Range[] _v6;

    private readonly record struct V4Range(uint Network, uint Mask);
    private readonly record struct V6Range(ulong Hi, ulong Lo, ulong MaskHi, ulong MaskLo);

    private PrivateRelayMatcher(V4Range[] v4, V6Range[] v6)
    {
        _v4 = v4;
        _v6 = v6;
    }

    /// <summary>Loads the seed snapshot embedded in this assembly.</summary>
    public static PrivateRelayMatcher LoadEmbedded()
    {
        using var stream = typeof(PrivateRelayMatcher).Assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{EmbeddedResourceName}' is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return Load(reader);
    }

    /// <summary>Loads a refreshed on-disk copy (downloaded by scripts/update-geoip.sh).</summary>
    public static PrivateRelayMatcher LoadCsv(string path)
    {
        using var reader = new StreamReader(path);
        return Load(reader);
    }

    private static PrivateRelayMatcher Load(TextReader reader)
    {
        var v4 = new List<V4Range>();
        var v6 = new List<V6Range>();
        Span<byte> bytes = stackalloc byte[16];

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var span = line.AsSpan().Trim();
            if (span.Length == 0 || span[0] == '#')
                continue;

            var comma = span.IndexOf(',');
            var cidr = comma >= 0 ? span[..comma] : span;
            var slash = cidr.IndexOf('/');
            if (slash <= 0)
                continue;
            if (!IPAddress.TryParse(cidr[..slash], out var network))
                continue;
            if (!int.TryParse(cidr[(slash + 1)..], out var prefix))
                continue;
            if (!network.TryWriteBytes(bytes, out var len))
                continue;

            if (len == 4)
            {
                if (prefix is < 0 or > 32)
                    continue;
                var mask = Mask32(prefix);
                var value = BinaryPrimitives.ReadUInt32BigEndian(bytes[..4]);
                v4.Add(new V4Range(value & mask, mask));
            }
            else
            {
                if (prefix is < 0 or > 128)
                    continue;
                var maskHi = Mask64(prefix);
                var maskLo = Mask64(prefix - 64);
                var hi = BinaryPrimitives.ReadUInt64BigEndian(bytes[..8]);
                var lo = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..16]);
                v6.Add(new V6Range(hi & maskHi, lo & maskLo, maskHi, maskLo));
            }
        }

        return new PrivateRelayMatcher(v4.ToArray(), v6.ToArray());
    }

    /// <summary>Allocation-free CIDR membership test. IPv4-mapped IPv6 addresses are
    /// matched against the IPv4 ranges.</summary>
    public bool Contains(IPAddress ip)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!ip.TryWriteBytes(bytes, out var len))
            return false;

        if (len == 16 && ip.IsIPv4MappedToIPv6)
        {
            bytes = bytes[12..16];
            len = 4;
        }

        if (len == 4)
        {
            var value = BinaryPrimitives.ReadUInt32BigEndian(bytes[..4]);
            var ranges = _v4;
            for (var i = 0; i < ranges.Length; i++)
            {
                if ((value & ranges[i].Mask) == ranges[i].Network)
                    return true;
            }
            return false;
        }

        var hi = BinaryPrimitives.ReadUInt64BigEndian(bytes[..8]);
        var lo = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..16]);
        var ranges6 = _v6;
        for (var i = 0; i < ranges6.Length; i++)
        {
            ref readonly var r = ref ranges6[i];
            if ((hi & r.MaskHi) == r.Hi && (lo & r.MaskLo) == r.Lo)
                return true;
        }
        return false;
    }

    private static uint Mask32(int bits) =>
        bits <= 0 ? 0u : bits >= 32 ? uint.MaxValue : uint.MaxValue << (32 - bits);

    private static ulong Mask64(int bits) =>
        bits <= 0 ? 0ul : bits >= 64 ? ulong.MaxValue : ulong.MaxValue << (64 - bits);
}
