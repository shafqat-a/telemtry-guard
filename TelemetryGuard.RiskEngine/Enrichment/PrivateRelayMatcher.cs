using System.Buffers.Binary;
using System.Net;

namespace TelemetryGuard.RiskEngine.Enrichment;

/// <summary>Membership test over Apple's published iCloud Private Relay egress ranges
/// (https://mask-api.icloud.com/egress-ip-ranges.csv, format "CIDR,country,region,city,").
/// CIDRs are normalized to [start, end] intervals, sorted and merged per address family;
/// Contains(IPAddress) is an allocation-free binary search.
///
/// The live list is far larger than the embedded seed suggests — a real download carries
/// ~288k ranges, of which ~246k are IPv6 — and this runs on every enrichment lookup, so
/// the search has to be logarithmic: a linear scan of that list measured 365 µs per IPv6
/// lookup against a &lt; 50 ms budget for the whole scoring path (D3).</summary>
internal sealed class PrivateRelayMatcher
{
    internal const string EmbeddedResourceName =
        "TelemetryGuard.RiskEngine.Enrichment.Data.apple-private-relay-seed.csv";

    // Parallel arrays, sorted ascending by Start and merged so no two intervals overlap
    // or touch. Ends[i] is the inclusive last address of the i-th interval.
    private readonly uint[] _v4Starts;
    private readonly uint[] _v4Ends;
    private readonly UInt128[] _v6Starts;
    private readonly UInt128[] _v6Ends;

    private PrivateRelayMatcher(uint[] v4Starts, uint[] v4Ends, UInt128[] v6Starts, UInt128[] v6Ends)
    {
        _v4Starts = v4Starts;
        _v4Ends = v4Ends;
        _v6Starts = v6Starts;
        _v6Ends = v6Ends;
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

    /// <summary>Loads a refreshed on-disk copy (downloaded by scripts/update-geoip.sh or
    /// scripts/update-iplegence.sh).</summary>
    public static PrivateRelayMatcher LoadCsv(string path)
    {
        using var reader = new StreamReader(path);
        return Load(reader);
    }

    private static PrivateRelayMatcher Load(TextReader reader)
    {
        var v4 = new List<(uint Start, uint End)>();
        var v6 = new List<(UInt128 Start, UInt128 End)>();
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
                var start = BinaryPrimitives.ReadUInt32BigEndian(bytes[..4]) & mask;
                v4.Add((start, start | ~mask));
            }
            else
            {
                if (prefix is < 0 or > 128)
                    continue;
                var mask = Mask128(prefix);
                var start = ReadUInt128BigEndian(bytes) & mask;
                v6.Add((start, start | ~mask));
            }
        }

        var (v4Starts, v4Ends) = SortAndMergeV4(v4);
        var (v6Starts, v6Ends) = SortAndMergeV6(v6);
        return new PrivateRelayMatcher(v4Starts, v4Ends, v6Starts, v6Ends);
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
            var i = FloorIndex(_v4Starts, value);
            return i >= 0 && value <= _v4Ends[i];
        }

        var value6 = ReadUInt128BigEndian(bytes);
        var j = FloorIndex(_v6Starts, value6);
        return j >= 0 && value6 <= _v6Ends[j];
    }

    /// <summary>Index of the last interval whose Start is &lt;= value, or -1.</summary>
    private static int FloorIndex<T>(T[] starts, T value) where T : IComparable<T>
    {
        var lo = 0;
        var hi = starts.Length - 1;
        var found = -1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (starts[mid].CompareTo(value) <= 0)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return found;
    }

    // Two near-identical merges rather than one generic one: UInt128/uint arithmetic on a
    // generic T would need INumber<T> plus overflow care on the "End + 1" adjacency test,
    // and this runs once per load, not per lookup.
    private static (uint[] Starts, uint[] Ends) SortAndMergeV4(List<(uint Start, uint End)> ranges)
    {
        if (ranges.Count == 0)
            return ([], []);

        ranges.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));

        var starts = new List<uint>(ranges.Count);
        var ends = new List<uint>(ranges.Count);
        var (curStart, curEnd) = ranges[0];

        for (var i = 1; i < ranges.Count; i++)
        {
            var (start, end) = ranges[i];
            // Merge when overlapping OR exactly adjacent (curEnd + 1 == start), guarding
            // the uint overflow at 255.255.255.255.
            if (start <= curEnd || (curEnd != uint.MaxValue && start == curEnd + 1))
            {
                if (end > curEnd)
                    curEnd = end;
                continue;
            }
            starts.Add(curStart);
            ends.Add(curEnd);
            (curStart, curEnd) = (start, end);
        }
        starts.Add(curStart);
        ends.Add(curEnd);

        return (starts.ToArray(), ends.ToArray());
    }

    private static (UInt128[] Starts, UInt128[] Ends) SortAndMergeV6(List<(UInt128 Start, UInt128 End)> ranges)
    {
        if (ranges.Count == 0)
            return ([], []);

        ranges.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));

        var starts = new List<UInt128>(ranges.Count);
        var ends = new List<UInt128>(ranges.Count);
        var (curStart, curEnd) = ranges[0];

        for (var i = 1; i < ranges.Count; i++)
        {
            var (start, end) = ranges[i];
            if (start <= curEnd || (curEnd != UInt128.MaxValue && start == curEnd + 1))
            {
                if (end > curEnd)
                    curEnd = end;
                continue;
            }
            starts.Add(curStart);
            ends.Add(curEnd);
            (curStart, curEnd) = (start, end);
        }
        starts.Add(curStart);
        ends.Add(curEnd);

        return (starts.ToArray(), ends.ToArray());
    }

    private static UInt128 ReadUInt128BigEndian(ReadOnlySpan<byte> bytes) =>
        new(BinaryPrimitives.ReadUInt64BigEndian(bytes[..8]), BinaryPrimitives.ReadUInt64BigEndian(bytes[8..16]));

    private static uint Mask32(int bits) =>
        bits <= 0 ? 0u : bits >= 32 ? uint.MaxValue : uint.MaxValue << (32 - bits);

    private static UInt128 Mask128(int bits) =>
        bits <= 0 ? UInt128.Zero : bits >= 128 ? UInt128.MaxValue : UInt128.MaxValue << (128 - bits);
}
