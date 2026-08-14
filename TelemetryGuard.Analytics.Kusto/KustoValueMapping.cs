using System.Net;
using System.Net.Sockets;

namespace TelemetryGuard.Analytics.Kusto;

/// <summary>
/// The null/NaN contract in one place, used by the sinks, the queries and the
/// contract fixture. Q2 (see README.md step-0 reconnaissance) decides whether a
/// non-finite <c>float</c> is written as JSON <c>"NaN"</c> or JSON <c>null</c>;
/// either way <see cref="RealToFloat"/> is the single place that turns a stored
/// value back into <see cref="float.NaN"/> when absent, so §7 ("missing != zero")
/// holds end to end regardless of which encoding the emulator/cluster accepts.
/// </summary>
public static class KustoValueMapping
{
    /// <summary>§7 missing != zero. A Kusto `real` that is null (absent) reads back
    /// as NaN, never 0. Under Q2-primary the stored value is already NaN and this is
    /// a pass-through; under Q2-fallback (null-encoding) this IS the contract.</summary>
    public static float RealToFloat(object? value) =>
        value is null or DBNull ? float.NaN : Convert.ToSingle(value);

    /// <summary>Nullable Kusto sums come back as DBNull over an empty scope; 0 is
    /// the correct mergeable value for a sum (unlike averages, which stay NaN).</summary>
    public static long ToInt64OrZero(object value) => value is DBNull ? 0L : Convert.ToInt64(value);

    /// <summary>Missing != zero (§7): empty-scope averages surface as NaN, never 0.</summary>
    public static double ToDoubleOrNaN(object value) => value is DBNull ? double.NaN : Convert.ToDouble(value);

    public static DateTime AsUtc(object value) =>
        DateTime.SpecifyKind(Convert.ToDateTime(value), DateTimeKind.Utc);

    /// <summary>IPv4 inputs must compare equal to stored values on both read and
    /// write paths — the Kusto twin of ANA-03's ToIpV6 (no cross-provider reference).</summary>
    public static string NormalizeIp(string ip)
    {
        var addr = IPAddress.TryParse(ip, out var parsed) ? parsed : IPAddress.IPv6None;
        return (addr.AddressFamily == AddressFamily.InterNetwork ? addr.MapToIPv6() : addr).ToString();
    }
}
