using TelemetryGuard.RiskEngine.Enrichment;

namespace TelemetryGuard.Tests.Unit.Features;

/// <summary>Spec §7 carve-out: Apple Private Relay users are legitimate Safari users —
/// IpProxyOrVpn is FORCED false when the IP is in Private Relay egress ranges.</summary>
public class PrivateRelayCarveOutTests
{
    [Fact]
    public void PrivateRelay_ForcesIpProxyOrVpnFalse()
    {
        var raw = new RawSessionDataBuilder()
            .WithEnrichment(new IpEnrichment { IsPrivateRelay = true, IsProxyOrVpn = true })
            .Build();

        var vector = RawSessionDataBuilder.Extractor().Extract(raw);

        Assert.True(vector.IsPrivateRelay);
        Assert.False(vector.IpProxyOrVpn);
    }

    [Fact]
    public void NotPrivateRelay_ProxySignalPassesThrough()
    {
        var raw = new RawSessionDataBuilder()
            .WithEnrichment(new IpEnrichment { IsPrivateRelay = false, IsProxyOrVpn = true })
            .Build();

        var vector = RawSessionDataBuilder.Extractor().Extract(raw);

        Assert.False(vector.IsPrivateRelay);
        Assert.True(vector.IpProxyOrVpn);
    }

    [Fact]
    public void NotPrivateRelay_ProxyDbMissing_StaysNull()
    {
        var raw = new RawSessionDataBuilder()
            .WithEnrichment(new IpEnrichment { IsPrivateRelay = false, IsProxyOrVpn = null })
            .Build();

        Assert.Null(RawSessionDataBuilder.Extractor().Extract(raw).IpProxyOrVpn);
    }
}
