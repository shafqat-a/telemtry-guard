using System.Net;
using TelemetryGuard.RiskEngine.Enrichment;

namespace TelemetryGuard.Tests.Unit.Enrichment;

public class PrivateRelayMatcherTests
{
    // Rows known to be in the embedded seed snapshot:
    //   172.224.226.0/27  (covers .0–.31; the next covered v4 block starts at .96)
    //   2606:54c0:aaa0::/45
    [Fact]
    public void Embedded_ipv4_inside_cidr_matches()
    {
        var matcher = PrivateRelayMatcher.LoadEmbedded();
        Assert.True(matcher.Contains(IPAddress.Parse("172.224.226.0")));
        Assert.True(matcher.Contains(IPAddress.Parse("172.224.226.5")));
        Assert.True(matcher.Contains(IPAddress.Parse("172.224.226.31")));
    }

    [Fact]
    public void Embedded_ipv4_adjacent_outside_cidr_does_not_match()
    {
        var matcher = PrivateRelayMatcher.LoadEmbedded();
        Assert.False(matcher.Contains(IPAddress.Parse("172.224.226.32"))); // just past the /27
        Assert.False(matcher.Contains(IPAddress.Parse("1.2.3.4")));
        Assert.False(matcher.Contains(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void Embedded_ipv6_inside_cidr_matches()
    {
        var matcher = PrivateRelayMatcher.LoadEmbedded();
        Assert.True(matcher.Contains(IPAddress.Parse("2606:54c0:aaa0::1")));
        Assert.True(matcher.Contains(IPAddress.Parse("2606:54c0:aaa7:ffff::1"))); // last /48 inside the /45
    }

    [Fact]
    public void Embedded_ipv6_outside_does_not_match()
    {
        var matcher = PrivateRelayMatcher.LoadEmbedded();
        Assert.False(matcher.Contains(IPAddress.Parse("2001:db8::1"))); // documentation prefix, never Apple
    }

    [Fact]
    public void Ipv4_mapped_ipv6_input_matches_ipv4_ranges()
    {
        var matcher = PrivateRelayMatcher.LoadEmbedded();
        Assert.True(matcher.Contains(IPAddress.Parse("::ffff:172.224.226.5")));
        Assert.False(matcher.Contains(IPAddress.Parse("::ffff:8.8.8.8")));
    }

    [Fact]
    public void LoadCsv_honors_exact_prefix_boundaries_for_both_families()
    {
        var path = Path.Combine(Path.GetTempPath(), "tg-relay-" + Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(path,
            "# comment line\n" +
            "\n" +
            "10.99.0.0/24,ZZ,,TestCity,\n" +
            "2606:54c0:aaaa::/48,ZZ,,TestCity,\n" +
            "not-a-cidr-line\n");
        try
        {
            var matcher = PrivateRelayMatcher.LoadCsv(path);

            Assert.True(matcher.Contains(IPAddress.Parse("10.99.0.0")));
            Assert.True(matcher.Contains(IPAddress.Parse("10.99.0.7")));
            Assert.True(matcher.Contains(IPAddress.Parse("10.99.0.255")));
            Assert.False(matcher.Contains(IPAddress.Parse("10.99.1.0")));      // adjacent, outside /24
            Assert.False(matcher.Contains(IPAddress.Parse("10.98.255.255"))); // adjacent, below

            Assert.True(matcher.Contains(IPAddress.Parse("2606:54c0:aaaa::")));
            Assert.True(matcher.Contains(IPAddress.Parse("2606:54c0:aaaa:ffff::1")));
            Assert.False(matcher.Contains(IPAddress.Parse("2606:54c0:aaab::"))); // adjacent /48
            Assert.False(matcher.Contains(IPAddress.Parse("2606:54c0:aaa9:ffff:ffff:ffff:ffff:ffff")));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
