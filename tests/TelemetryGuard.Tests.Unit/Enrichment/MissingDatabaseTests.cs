using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TelemetryGuard.RiskEngine.Contracts;
using TelemetryGuard.RiskEngine.Enrichment;
using TelemetryGuard.RiskEngine.Enrichment.Providers;

namespace TelemetryGuard.Tests.Unit.Enrichment;

/// <summary>Degradation principle (D13): with no database files on disk the service must
/// construct, serve lookups with null fields, and never throw. Every provider (D24) owes
/// the same guarantee, so the suite runs once per provider.</summary>
public class MissingDatabaseTests : IDisposable
{
    private readonly string _emptyDataDir;

    public MissingDatabaseTests()
    {
        _emptyDataDir = Path.Combine(Path.GetTempPath(), "tg-geo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_emptyDataDir);
    }

    public void Dispose() => Directory.Delete(_emptyDataDir, recursive: true);

    public static TheoryData<string> Providers => new()
    {
        IpIntelligenceProviders.Iplegence,
        IpIntelligenceProviders.MaxMind,
    };

    private IpEnrichmentService CreateService(string provider)
    {
        var options = Options.Create(new IpEnrichmentOptions { Provider = provider, DataDir = _emptyDataDir });
        IIpIntelligenceProvider impl = provider == IpIntelligenceProviders.Iplegence
            ? new IplegenceIpIntelligenceProvider(options, NullLogger<IplegenceIpIntelligenceProvider>.Instance)
            : new MaxMindIpIntelligenceProvider(options, NullLogger<MaxMindIpIntelligenceProvider>.Instance);
        return new IpEnrichmentService(impl);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void Public_ip_yields_null_fields_without_throwing(string provider)
    {
        using var service = CreateService(provider);

        var result = service.Enrich("8.8.8.8");

        Assert.NotNull(result);
        Assert.Null(result.CountryCode);
        Assert.Null(result.City);
        Assert.Null(result.Latitude);
        Assert.Null(result.Longitude);
        Assert.Null(result.TimeZone);
        Assert.Null(result.AsnNumber);
        Assert.Null(result.AsnOrganization);
        Assert.Equal(AsnType.Unknown, result.AsnType);
        Assert.Null(result.IsProxyOrVpn);
        Assert.Null(result.IsTor);
        Assert.Null(result.IsDatacenter);
        Assert.False(result.IsPrivateRelay);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void Unparseable_ip_returns_empty(string provider)
    {
        using var service = CreateService(provider);

        Assert.Same(IpEnrichment.Empty, service.Enrich("not-an-ip"));
        Assert.Same(IpEnrichment.Empty, service.Enrich(""));
    }

    [Theory]
    [InlineData("127.0.0.1")]     // loopback
    [InlineData("10.1.2.3")]      // RFC1918
    [InlineData("172.16.0.1")]    // RFC1918
    [InlineData("172.31.255.9")]  // RFC1918 upper edge of /12
    [InlineData("192.168.1.1")]   // RFC1918
    [InlineData("169.254.1.1")]   // link-local
    [InlineData("::1")]           // IPv6 loopback
    [InlineData("fe80::1")]       // IPv6 link-local
    public void Private_and_loopback_addresses_return_empty(string ip)
    {
        using var iplegence = CreateService(IpIntelligenceProviders.Iplegence);
        using var maxmind = CreateService(IpIntelligenceProviders.MaxMind);

        Assert.Same(IpEnrichment.Empty, iplegence.Enrich(ip));
        Assert.Same(IpEnrichment.Empty, maxmind.Enrich(ip));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void Private_relay_flag_is_computed_even_with_no_databases(string provider)
    {
        using var service = CreateService(provider);

        // 172.224.226.5 sits inside an embedded Private Relay CIDR (172.224.226.0/27).
        var result = service.Enrich("172.224.226.5");
        Assert.True(result.IsPrivateRelay);
        Assert.Null(result.CountryCode);
        Assert.Null(result.IsProxyOrVpn); // RAW flag stays null — carve-out belongs to RSK-04
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void Empty_path_handles_100k_calls_under_a_second(string provider)
    {
        using var service = CreateService(provider);

        // Warm-up (JIT).
        _ = service.Enrich("127.0.0.1");
        _ = service.Enrich("not-an-ip");

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 100_000; i++)
            _ = service.Enrich("127.0.0.1");
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1),
            $"100k Empty-path Enrich calls took {sw.Elapsed.TotalMilliseconds:F0} ms (budget 1000 ms)");
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void Disposing_twice_is_safe(string provider)
    {
        // The DI container disposes the provider singleton AND IpEnrichmentService
        // disposes the provider it was given — that must not double-dispose readers.
        var service = CreateService(provider);
        service.Dispose();
        service.Dispose();
    }
}
