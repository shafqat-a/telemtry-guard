using System.Collections.Frozen;
using TelemetryGuard.RiskEngine.Contracts;
using TelemetryGuard.RiskEngine.Enrichment;

namespace TelemetryGuard.Tests.Unit.Enrichment;

public class AsnTypeClassificationTests
{
    private static readonly FrozenSet<long> Seed = IpEnrichmentService.LoadDatacenterAsnSeed();

    [Theory]
    [InlineData("ISP", AsnType.Residential)]
    [InlineData("MOB", AsnType.Mobile)]
    [InlineData("COM", AsnType.Business)]
    [InlineData("ORG", AsnType.Business)]
    [InlineData("DCH", AsnType.Datacenter)]
    [InlineData("EDU", AsnType.Education)]
    [InlineData("LIB", AsnType.Education)]
    [InlineData("GOV", AsnType.Government)]
    [InlineData("MIL", AsnType.Government)]
    [InlineData("CDN", AsnType.Cdn)]
    public void Usage_type_maps_to_expected_asn_type(string usageType, AsnType expected) =>
        Assert.Equal(expected, IpEnrichmentService.ClassifyAsnType(usageType, asnNumber: null, Seed));

    [Theory]
    [InlineData(24940)]  // Hetzner
    [InlineData(16509)]  // Amazon AWS
    [InlineData(13335)]  // Cloudflare
    [InlineData(20473)]  // Vultr
    public void Seed_asn_with_no_px_db_classifies_as_datacenter(long asn) =>
        Assert.Equal(AsnType.Datacenter, IpEnrichmentService.ClassifyAsnType(usageType: null, asn, Seed));

    [Theory]
    [InlineData("SES")]
    [InlineData("RSV")]
    [InlineData("-")]
    [InlineData("")]
    public void Unmapped_usage_type_falls_through_to_seed_list(string usageType)
    {
        Assert.Equal(AsnType.Datacenter, IpEnrichmentService.ClassifyAsnType(usageType, 24940, Seed));
        Assert.Equal(AsnType.Unknown, IpEnrichmentService.ClassifyAsnType(usageType, 64512, Seed));
    }

    [Fact]
    public void Mapped_usage_type_takes_precedence_over_seed_list() =>
        Assert.Equal(AsnType.Residential, IpEnrichmentService.ClassifyAsnType("ISP", 24940, Seed));

    [Fact]
    public void No_usage_type_and_no_asn_is_unknown() =>
        Assert.Equal(AsnType.Unknown, IpEnrichmentService.ClassifyAsnType(usageType: null, asnNumber: null, Seed));

    [Fact]
    public void Non_seed_asn_with_no_usage_type_is_unknown() =>
        Assert.Equal(AsnType.Unknown, IpEnrichmentService.ClassifyAsnType(usageType: null, 64512, Seed));

    [Fact]
    public void Embedded_seed_list_contains_every_documented_asn()
    {
        long[] documented =
        [
            16509, 14618, 8075, 15169, 396982, 14061, 16276, 24940,
            63949, 20473, 51167, 45102, 132203, 13335, 54113,
        ];
        foreach (var asn in documented)
            Assert.True(Seed.Contains(asn), $"seed list is missing ASN {asn}");
        Assert.Equal(documented.Length, Seed.Count);
    }
}
