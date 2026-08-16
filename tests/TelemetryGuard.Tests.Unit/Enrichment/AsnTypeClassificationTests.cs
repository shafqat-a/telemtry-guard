using System.Collections.Frozen;
using TelemetryGuard.RiskEngine.Contracts;
using TelemetryGuard.RiskEngine.Enrichment.Providers;

namespace TelemetryGuard.Tests.Unit.Enrichment;

public class AsnTypeClassificationTests
{
    private static readonly FrozenSet<long> Seed = AsnClassifier.LoadDatacenterAsnSeed();

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
        Assert.Equal(expected, AsnClassifier.FromUsageType(usageType, asnNumber: null, Seed));

    [Theory]
    [InlineData(24940)]  // Hetzner
    [InlineData(16509)]  // Amazon AWS
    [InlineData(13335)]  // Cloudflare
    [InlineData(20473)]  // Vultr
    public void Seed_asn_with_no_px_db_classifies_as_datacenter(long asn) =>
        Assert.Equal(AsnType.Datacenter, AsnClassifier.FromUsageType(usageType: null, asn, Seed));

    [Theory]
    [InlineData("SES")]
    [InlineData("RSV")]
    [InlineData("-")]
    [InlineData("")]
    public void Unmapped_usage_type_falls_through_to_seed_list(string usageType)
    {
        Assert.Equal(AsnType.Datacenter, AsnClassifier.FromUsageType(usageType, 24940, Seed));
        Assert.Equal(AsnType.Unknown, AsnClassifier.FromUsageType(usageType, 64512, Seed));
    }

    [Fact]
    public void Mapped_usage_type_takes_precedence_over_seed_list() =>
        Assert.Equal(AsnType.Residential, AsnClassifier.FromUsageType("ISP", 24940, Seed));

    [Fact]
    public void No_usage_type_and_no_asn_is_unknown() =>
        Assert.Equal(AsnType.Unknown, AsnClassifier.FromUsageType(usageType: null, asnNumber: null, Seed));

    [Fact]
    public void Non_seed_asn_with_no_usage_type_is_unknown() =>
        Assert.Equal(AsnType.Unknown, AsnClassifier.FromUsageType(usageType: null, 64512, Seed));

    [Theory]
    [InlineData("residential", AsnType.Residential)]
    [InlineData("mobile", AsnType.Mobile)]
    [InlineData("business", AsnType.Business)]
    [InlineData("education", AsnType.Education)]
    [InlineData("government", AsnType.Government)]
    [InlineData("hosting", AsnType.Datacenter)]
    public void Inferred_usage_type_maps_to_expected_asn_type(string usageType, AsnType expected) =>
        Assert.Equal(expected,
            AsnClassifier.FromTraits(usageType, isCdn: false, isHostingProvider: false, asnNumber: null, Seed));

    [Fact]
    public void Usage_type_outranks_the_datacenter_seed()
    {
        // A university on an ASN that also appears in the seed must not be a datacenter:
        // the producer already resolved that precedence when inferring the type.
        Assert.Equal(AsnType.Education,
            AsnClassifier.FromTraits("education", isCdn: false, isHostingProvider: false, 16509, Seed));
        Assert.Equal(AsnType.Mobile,
            AsnClassifier.FromTraits("mobile", isCdn: false, isHostingProvider: false, 24940, Seed));
    }

    [Fact]
    public void Hosting_usage_type_on_a_cdn_prefix_is_a_cdn() =>
        Assert.Equal(AsnType.Cdn,
            AsnClassifier.FromTraits("hosting", isCdn: true, isHostingProvider: false, asnNumber: null, Seed));

    [Fact]
    public void Cdn_trait_wins_over_hosting_and_the_seed_list() =>
        Assert.Equal(AsnType.Cdn,
            AsnClassifier.FromTraits(usageType: null, isCdn: true, isHostingProvider: true, 24940, Seed));

    [Fact]
    public void Hosting_trait_classifies_as_datacenter_even_off_the_seed_list() =>
        Assert.Equal(AsnType.Datacenter,
            AsnClassifier.FromTraits(usageType: null, isCdn: false, isHostingProvider: true, 64512, Seed));

    [Fact]
    public void Traits_fall_through_to_the_seed_list() =>
        Assert.Equal(AsnType.Datacenter,
            AsnClassifier.FromTraits(usageType: null, isCdn: false, isHostingProvider: false, 24940, Seed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("carrier-pigeon")]   // a value this mapping does not know
    public void Missing_or_unrecognized_usage_type_falls_through(string? usageType)
    {
        // ~54% of the routable IPv4 space carries no inferred type: Unknown, never a
        // guess (D24) — but the flags and the seed still get their say.
        Assert.Equal(AsnType.Unknown,
            AsnClassifier.FromTraits(usageType, isCdn: false, isHostingProvider: false, 64512, Seed));
        Assert.Equal(AsnType.Unknown,
            AsnClassifier.FromTraits(usageType, isCdn: false, isHostingProvider: false, asnNumber: null, Seed));
        Assert.Equal(AsnType.Datacenter,
            AsnClassifier.FromTraits(usageType, isCdn: false, isHostingProvider: true, asnNumber: null, Seed));
    }

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
