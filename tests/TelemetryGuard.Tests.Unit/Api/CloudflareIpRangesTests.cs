using System.Net;
using System.Net.Sockets;
using TelemetryGuard.Api.Edge;

namespace TelemetryGuard.Tests.Unit.Api;

/// <summary>INT-05: parses the REAL embedded TelemetryGuard.Api/Edge/cloudflare-ips.txt
/// (never a fixture) — proves the embedded resource wiring and the comment/blank-line
/// skip both work end-to-end, plus a handful of known-good/known-bad membership
/// checks spanning v4 and v6.</summary>
public sealed class CloudflareIpRangesTests
{
    [Fact]
    public void Load_Returns15IPv4And7IPv6Ranges()
    {
        var ranges = CloudflareIpRanges.Load();

        Assert.Equal(15, ranges.Count(r => r.Address.AddressFamily == AddressFamily.InterNetwork));
        Assert.Equal(7, ranges.Count(r => r.Address.AddressFamily == AddressFamily.InterNetworkV6));
        // Only 22 real CIDR lines exist in the file — if '#' comments or blank lines
        // were parsed as ranges instead of skipped, this total would be wrong.
        Assert.Equal(22, ranges.Count);
    }

    [Theory]
    [InlineData("104.16.1.1", true)]       // inside 104.16.0.0/13
    [InlineData("2606:4700::1", true)]     // inside 2606:4700::/32
    [InlineData("8.8.8.8", false)]         // Google DNS — not Cloudflare
    [InlineData("2001:db8::1", false)]     // documentation range — not Cloudflare
    public void Contains_MatchesExpectedMembership(string ip, bool expected)
    {
        Assert.Equal(expected, CloudflareIpRanges.Contains(IPAddress.Parse(ip)));
    }

    [Fact]
    public void Contains_RepeatedCalls_AreConsistent_LoadIsCached()
    {
        var ip = IPAddress.Parse("172.64.1.1");
        Assert.True(CloudflareIpRanges.Contains(ip));
        Assert.True(CloudflareIpRanges.Contains(ip)); // same cached Load() list both times
        Assert.Same(CloudflareIpRanges.Load(), CloudflareIpRanges.Load());
    }
}
