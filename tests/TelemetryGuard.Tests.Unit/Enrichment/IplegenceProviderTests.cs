using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TelemetryGuard.RiskEngine.Contracts;
using TelemetryGuard.RiskEngine.Enrichment;
using TelemetryGuard.RiskEngine.Enrichment.Providers;

namespace TelemetryGuard.Tests.Unit.Enrichment;

/// <summary>D24: the iplegence provider decodes Superior-IP.mmdb's record layout and maps
/// its traits onto IpEnrichment. Runs against a 3 KB fixture in the real format — see
/// Enrichment/Data/README.md for the rows and how it was generated.</summary>
public sealed class IplegenceProviderTests : IDisposable
{
    private const string FixtureName = "iplegence-fixture.mmdb";

    private readonly string _dataDir;
    private readonly IplegenceIpIntelligenceProvider _provider;
    private readonly IpEnrichmentService _service;

    public IplegenceProviderTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "tg-iplegence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        File.Copy(FixturePath, Path.Combine(_dataDir, FixtureName));

        _provider = new IplegenceIpIntelligenceProvider(
            Options.Create(new IpEnrichmentOptions { DataDir = _dataDir, IplegenceDb = FixtureName }),
            NullLogger<IplegenceIpIntelligenceProvider>.Instance);
        _service = new IpEnrichmentService(_provider);
    }

    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Enrichment", "Data", FixtureName);

    public void Dispose()
    {
        _service.Dispose();
        Directory.Delete(_dataDir, recursive: true);
    }

    [Fact]
    public void Full_row_maps_every_geo_and_asn_field()
    {
        var result = _service.Enrich("8.8.8.8");

        Assert.Equal("US", result.CountryCode);
        Assert.Equal("Mountain View", result.City);
        Assert.Equal(37.386, result.Latitude!.Value, 3);
        Assert.Equal(-122.0838, result.Longitude!.Value, 3);
        Assert.Equal("America/Los_Angeles", result.TimeZone);
        Assert.Equal(15169, result.AsnNumber);
        Assert.Equal("Google LLC", result.AsnOrganization);
    }

    [Fact]
    public void Row_without_flags_reports_them_false_not_null()
    {
        // The row exists and iplegence omits false traits, so an absent flag means the
        // dataset asserts nothing — a known-clean IP, not an unknown one.
        var result = _service.Enrich("24.48.1.1");

        Assert.Equal("CA", result.CountryCode);
        Assert.Equal(5769, result.AsnNumber);
        Assert.False(result.IsProxyOrVpn);
        Assert.False(result.IsTor);
        Assert.False(result.IsDatacenter);
        Assert.False(result.IsPrivateRelay);
        Assert.Equal(AsnType.Residential, result.AsnType);
    }

    [Theory]
    [InlineData("24.48.1.1", AsnType.Residential)]     // usage_type residential (peeringdb)
    [InlineData("208.54.4.1", AsnType.Mobile)]         // usage_type mobile (asn_name)
    [InlineData("194.60.1.1", AsnType.Business)]       // usage_type business
    [InlineData("44.224.0.1", AsnType.Education)]      // usage_type education
    [InlineData("149.101.1.1", AsnType.Government)]    // usage_type government
    [InlineData("45.32.9.9", AsnType.Datacenter)]      // usage_type hosting + is_hosting_provider
    [InlineData("104.16.1.1", AsnType.Cdn)]            // usage_type hosting + is_cdn
    [InlineData("196.201.1.1", AsnType.Unknown)]       // no usage_type could be inferred
    public void Inferred_usage_type_drives_asn_type(string ip, AsnType expected) =>
        Assert.Equal(expected, _service.Enrich(ip).AsnType);

    [Fact]
    public void Usage_type_outranks_the_datacenter_asn_seed()
    {
        // 44.224.0.0/11 is typed education while sitting on ASN 16509, which IS in the
        // embedded datacenter seed. The inferred type has to win, and the address must
        // not be reported as a datacenter either.
        var result = _service.Enrich("44.224.0.1");

        Assert.Equal(AsnType.Education, result.AsnType);
        Assert.False(result.IsDatacenter);
    }

    [Fact]
    public void Hosting_usage_type_sets_datacenter_without_any_prefix_flag()
    {
        // 8.8.8.0/24 carries usage_type=hosting from PeeringDB and no is_hosting_provider
        // flag at all — IsDatacenter must stay consistent with AsnType.
        var result = _service.Enrich("8.8.8.8");

        Assert.Equal(AsnType.Datacenter, result.AsnType);
        Assert.True(result.IsDatacenter);
        Assert.False(result.IsProxyOrVpn);   // hosting is still not a proxy signal
    }

    [Fact]
    public void Hosting_provider_trait_sets_datacenter()
    {
        var result = _service.Enrich("45.32.9.9");

        Assert.True(result.IsDatacenter);
        Assert.Equal(AsnType.Datacenter, result.AsnType);
        Assert.False(result.IsProxyOrVpn);   // hosting alone is NOT a proxy signal
    }

    [Fact]
    public void Cdn_trait_wins_over_the_datacenter_asn_seed()
    {
        // ASN 13335 (Cloudflare) is in the embedded seed, so IsDatacenter still holds —
        // but the classification a feature consumer sees must be Cdn.
        var result = _service.Enrich("104.16.1.1");

        Assert.Equal(AsnType.Cdn, result.AsnType);
        Assert.True(result.IsDatacenter);
    }

    [Fact]
    public void Tor_exit_node_sets_both_tor_and_proxy()
    {
        var result = _service.Enrich("185.220.101.7");

        Assert.True(result.IsTor);
        Assert.True(result.IsProxyOrVpn);
        Assert.Equal("DE", result.CountryCode);
    }

    [Fact]
    public void Public_proxy_and_anonymous_vpn_set_proxy_but_not_tor()
    {
        var result = _service.Enrich("51.15.3.3");

        Assert.True(result.IsProxyOrVpn);
        Assert.False(result.IsTor);
    }

    [Fact]
    public void Relay_trait_sets_private_relay_outside_the_apple_seed_list()
    {
        // 203.0.113.0/24 is not in the embedded Apple egress CIDRs, so a true flag here
        // can only have come from the dataset's is_relay trait.
        var result = _service.Enrich("203.0.113.9");

        Assert.True(result.IsPrivateRelay);
        Assert.False(result.IsProxyOrVpn);   // RSK-04 carve-out relies on this staying raw
    }

    [Fact]
    public void Ipv6_rows_resolve()
    {
        var result = _service.Enrich("2001:db8::1");

        Assert.Equal("DE", result.CountryCode);
        Assert.Equal("Europe/Berlin", result.TimeZone);
        Assert.Equal(64512, result.AsnNumber);
        Assert.Equal(AsnType.Unknown, result.AsnType);
    }

    [Fact]
    public void Datacenter_asn_seed_still_applies_to_untyped_rows()
    {
        // The seed remains the last resort for a row with neither a usage_type nor any
        // prefix flag. 2001:db8::/32 sits on ASN 64512, which is NOT in the seed…
        Assert.Equal(AsnType.Unknown, _service.Enrich("2001:db8::1").AsnType);
        // …while the classifier still promotes a seeded ASN when nothing else typed it.
        Assert.Equal(AsnType.Datacenter,
            AsnClassifier.FromTraits(usageType: null, isCdn: false, isHostingProvider: false, 24940,
                AsnClassifier.LoadDatacenterAsnSeed()));
    }

    [Fact]
    public void Uncovered_address_is_unknown_not_clean()
    {
        var result = _service.Enrich("9.9.9.9");

        Assert.Null(result.CountryCode);
        Assert.Null(result.AsnNumber);
        Assert.Null(result.IsProxyOrVpn);
        Assert.Null(result.IsTor);
        Assert.Null(result.IsDatacenter);
        Assert.Equal(AsnType.Unknown, result.AsnType);
    }

    [Fact]
    public void Ipv4_mapped_ipv6_resolves_to_the_ipv4_row()
    {
        var result = _service.Enrich("::ffff:8.8.8.8");

        Assert.Equal("US", result.CountryCode);
        Assert.Equal(15169, result.AsnNumber);
    }

    [Fact]
    public void Reload_is_a_no_op_until_the_file_changes()
    {
        Assert.False(_provider.ReloadIfChanged());

        var before = _provider.CurrentHandle;
        File.SetLastWriteTimeUtc(Path.Combine(_dataDir, FixtureName), DateTime.UtcNow.AddMinutes(1));

        Assert.True(_provider.ReloadIfChanged());
        Assert.NotSame(before, _provider.CurrentHandle);
        Assert.Equal("US", _service.Enrich("8.8.8.8").CountryCode);   // still serving after the swap
    }

    [Fact]
    public async Task Reloading_during_a_tight_enrich_loop_never_throws()
    {
        var stop = false;
        Exception? failure = null;
        long lookups = 0;

        var enrichLoop = Task.Run(() =>
        {
            try
            {
                while (!Volatile.Read(ref stop))
                {
                    if (_service.Enrich("8.8.8.8").CountryCode != "US")
                        throw new InvalidOperationException("lost the row during a reader swap");
                    Interlocked.Increment(ref lookups);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        var path = Path.Combine(_dataDir, FixtureName);
        for (var i = 1; i <= 200; i++)
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(i));
            _provider.ReloadIfChanged();
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Interlocked.Read(ref lookups) == 0 && failure is null && DateTime.UtcNow < deadline)
            await Task.Yield();

        Volatile.Write(ref stop, true);
        await enrichLoop;

        Assert.Null(failure);
        Assert.True(Interlocked.Read(ref lookups) > 0, "enrich loop never ran");
    }
}
