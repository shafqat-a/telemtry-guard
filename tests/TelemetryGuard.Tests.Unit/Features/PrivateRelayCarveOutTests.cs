using TelemetryGuard.RiskEngine.Contracts;
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

    // ip_datacenter_asn carve-outs — the T1 ip_datacenter_paid rule (floor 70 = Challenge
    // on every paid click) must never fire on shared consumer egress.

    [Fact]
    public void PrivateRelay_ForcesIpDatacenterAsnFalse()
    {
        // Private Relay egresses through Cloudflare/Fastly/Akamai, which the provider
        // classifies as Cdn with IsDatacenter = true (D24). The visitor is a person.
        var raw = new RawSessionDataBuilder()
            .WithEnrichment(new IpEnrichment
            {
                IsPrivateRelay = true, IsDatacenter = true, AsnType = AsnType.Cdn, AsnNumber = 13335,
            })
            .Build();

        var vector = RawSessionDataBuilder.Extractor().Extract(raw);

        Assert.False(vector.IpDatacenterAsn);
        Assert.Equal(AsnType.Cdn, vector.AsnType);   // the model still sees the CTX one-hot
    }

    [Fact]
    public void CdnEgress_WithoutPrivateRelay_ForcesIpDatacenterAsnFalse()
    {
        // WARP / Google One VPN: not on the relay list, but the ASN is typed Cdn.
        var raw = new RawSessionDataBuilder()
            .WithEnrichment(new IpEnrichment { IsPrivateRelay = false, IsDatacenter = true, AsnType = AsnType.Cdn })
            .Build();

        Assert.False(RawSessionDataBuilder.Extractor().Extract(raw).IpDatacenterAsn);
    }

    [Fact]
    public void HostingEgress_KeepsIpDatacenterAsn_AndUnknownStaysNull()
    {
        var hosting = new RawSessionDataBuilder()
            .WithEnrichment(new IpEnrichment { IsPrivateRelay = false, IsDatacenter = true, AsnType = AsnType.Datacenter })
            .Build();
        Assert.True(RawSessionDataBuilder.Extractor().Extract(hosting).IpDatacenterAsn);

        var unknown = new RawSessionDataBuilder()
            .WithEnrichment(new IpEnrichment { IsPrivateRelay = false, IsDatacenter = null, AsnType = AsnType.Unknown })
            .Build();
        Assert.Null(RawSessionDataBuilder.Extractor().Extract(unknown).IpDatacenterAsn);
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
