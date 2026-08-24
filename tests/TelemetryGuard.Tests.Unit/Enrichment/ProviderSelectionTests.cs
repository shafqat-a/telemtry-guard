using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TelemetryGuard.RiskEngine.Enrichment;
using TelemetryGuard.RiskEngine.Enrichment.Providers;

namespace TelemetryGuard.Tests.Unit.Enrichment;

/// <summary>D24: "IpEnrichment:Provider" is the only thing that decides which dataset
/// answers lookups, and a typo must fail startup rather than silently enrich nothing —
/// the same rule the analytics provider switch follows (D7).</summary>
public class ProviderSelectionTests
{
    private static ServiceProvider Build(params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
        services.AddIpEnrichment(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Iplegence_is_the_default_provider()
    {
        using var sp = Build(("IpEnrichment:DataDir", NonexistentDataDir));

        Assert.IsType<IplegenceIpIntelligenceProvider>(sp.GetRequiredService<IIpIntelligenceProvider>());
        Assert.Equal("Iplegence", sp.GetRequiredService<IpEnrichmentService>().ProviderName);
    }

    [Theory]
    [InlineData("MaxMind")]
    [InlineData("maxmind")]   // the switch is case-insensitive
    public void MaxMind_can_be_selected(string configured)
    {
        using var sp = Build(("IpEnrichment:Provider", configured), ("IpEnrichment:DataDir", NonexistentDataDir));

        Assert.IsType<MaxMindIpIntelligenceProvider>(sp.GetRequiredService<IIpIntelligenceProvider>());
        Assert.Equal("MaxMind", sp.GetRequiredService<IpEnrichmentService>().ProviderName);
    }

    [Fact]
    public void Unknown_provider_aborts_at_registration()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Build(("IpEnrichment:Provider", "GeoLite3")));

        Assert.Contains("GeoLite3", ex.Message);
    }

    [Fact]
    public void Enrichment_service_and_interface_resolve_to_the_same_singleton()
    {
        using var sp = Build(("IpEnrichment:DataDir", NonexistentDataDir));

        Assert.Same(sp.GetRequiredService<IpEnrichmentService>(), sp.GetRequiredService<IIpEnrichmentService>());
    }

    /// <summary>A path that cannot exist: registration and resolution must work anyway,
    /// because a missing database degrades to null fields instead of failing (D13).</summary>
    private static string NonexistentDataDir =>
        Path.Combine(Path.GetTempPath(), "tg-no-geo-data-" + Guid.NewGuid().ToString("N"));
}
