using TelemetryGuard.Api.Services;

namespace TelemetryGuard.Tests.Unit.Api;

public sealed class MarketIqPublisherTests
{
    [Theory]
    [InlineData("https://bu.edu.bd/tg-decoy/a7f91", true)]
    [InlineData("https://bu.edu.bd/tg-decoy/a7f91/", true)]
    [InlineData("https://bu.edu.bd/ordinary", false)]
    public void DecoyPage_UsesExactConfiguredPath(string pageUrl,bool expected)
        => Assert.Equal(expected,MarketIqPublisher.DecoyPage(
            "[\"/tg-decoy/a7f91\",\"internal/trap\"]","bu.edu.bd",pageUrl));

    [Fact]
    public void DecoyPage_DoesNotUseLoosePrefixOrQueryMarker()
    {
        const string paths="[\"/tg-decoy/a7f91\"]";
        Assert.False(MarketIqPublisher.DecoyPage(paths,"bu.edu.bd","https://bu.edu.bd/tg-decoy/a7f91-extra"));
        Assert.False(MarketIqPublisher.DecoyPage(paths,"bu.edu.bd","https://bu.edu.bd/?tg_honey=1"));
        Assert.False(MarketIqPublisher.DecoyPage(paths,"bu.edu.bd","https://evil.example/tg-decoy/a7f91"));
    }

    [Theory]
    [InlineData(null,null)]
    [InlineData("[]",null)]
    [InlineData("not-json",null)]
    public void DecoyPage_MissingOrInvalidConfiguration_IsUnknown(string? paths,bool? expected)
        => Assert.Equal(expected,MarketIqPublisher.DecoyPage(paths,"bu.edu.bd","https://bu.edu.bd/trap"));
}
