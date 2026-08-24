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
    public void Overlapping_and_adjacent_ranges_merge_without_losing_members()
    {
        // The lookup is a binary search over merged intervals, so unsorted input,
        // duplicates, nesting and exact adjacency all have to collapse correctly.
        var matcher = LoadFromLines(
            "10.0.1.0/24,ZZ,,A,",       // out of order
            "10.0.0.0/24,ZZ,,B,",       // adjacent below 10.0.1.0/24
            "10.0.0.128/25,ZZ,,C,",     // nested inside 10.0.0.0/24
            "10.0.9.0/24,ZZ,,D,",       // detached island
            "2606:54c0:aa00::/40,ZZ,,E,",
            "2606:54c0:aa01::/48,ZZ,,F,");

        Assert.True(matcher.Contains(IPAddress.Parse("10.0.0.0")));
        Assert.True(matcher.Contains(IPAddress.Parse("10.0.0.200")));
        Assert.True(matcher.Contains(IPAddress.Parse("10.0.1.255")));
        Assert.False(matcher.Contains(IPAddress.Parse("10.0.2.0")));   // gap between the merged block and the island
        Assert.False(matcher.Contains(IPAddress.Parse("10.0.8.255")));
        Assert.True(matcher.Contains(IPAddress.Parse("10.0.9.9")));
        Assert.False(matcher.Contains(IPAddress.Parse("10.0.10.0")));

        Assert.True(matcher.Contains(IPAddress.Parse("2606:54c0:aa01::1")));  // nested /48
        Assert.True(matcher.Contains(IPAddress.Parse("2606:54c0:aaff::1")));  // still inside the /40
        Assert.False(matcher.Contains(IPAddress.Parse("2606:54c0:ab00::1"))); // just past it
    }

    [Fact]
    public void Ranges_touching_the_top_of_each_address_space_do_not_overflow()
    {
        var matcher = LoadFromLines(
            "255.255.255.254/31,ZZ,,A,",
            "ffff:ffff:ffff:ffff:ffff:ffff:ffff:fffe/127,ZZ,,B,");

        Assert.True(matcher.Contains(IPAddress.Parse("255.255.255.255")));
        Assert.False(matcher.Contains(IPAddress.Parse("255.255.255.253")));
        Assert.True(matcher.Contains(IPAddress.Parse("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")));
        Assert.False(matcher.Contains(IPAddress.Parse("ffff:ffff:ffff:ffff:ffff:ffff:ffff:fffd")));
    }

    [Fact]
    public void Empty_list_matches_nothing()
    {
        var matcher = LoadFromLines("# nothing but a comment");

        Assert.False(matcher.Contains(IPAddress.Parse("8.8.8.8")));
        Assert.False(matcher.Contains(IPAddress.Parse("2001:db8::1")));
    }

    private static PrivateRelayMatcher LoadFromLines(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), "tg-relay-" + Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllLines(path, lines);
        try
        {
            return PrivateRelayMatcher.LoadCsv(path);
        }
        finally
        {
            File.Delete(path);
        }
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
