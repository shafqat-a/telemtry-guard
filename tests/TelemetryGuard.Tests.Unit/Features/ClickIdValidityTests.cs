namespace TelemetryGuard.Tests.Unit.Features;

/// <summary>Step-7 truth table for click_id_invalid — all five rows.</summary>
public class ClickIdValidityTests
{
    [Fact]
    public void Organic_NotPaid_Null()
    {
        var raw = new RawSessionDataBuilder().Build();

        Assert.Null(RawSessionDataBuilder.Extractor().Extract(raw).ClickIdInvalid);
    }

    [Fact]
    public void Paid_MissingClickId_True()
    {
        var raw = new RawSessionDataBuilder().AsPaidClick(clickId: null, clickIdFresh: null).Build();

        Assert.True(RawSessionDataBuilder.Extractor().Extract(raw).ClickIdInvalid);
    }

    [Fact]
    public void Paid_ReplayedClickId_True()
    {
        var raw = new RawSessionDataBuilder().AsPaidClick("gclid-abc", clickIdFresh: false).Build();

        Assert.True(RawSessionDataBuilder.Extractor().Extract(raw).ClickIdInvalid);
    }

    [Fact]
    public void Paid_FreshClickId_False()
    {
        var raw = new RawSessionDataBuilder().AsPaidClick("gclid-abc", clickIdFresh: true).Build();

        Assert.False(RawSessionDataBuilder.Extractor().Extract(raw).ClickIdInvalid);
    }

    [Fact]
    public void Paid_ClickIdPresent_DedupeUnavailable_Null()
    {
        var raw = new RawSessionDataBuilder().AsPaidClick("gclid-abc", clickIdFresh: null).Build();

        Assert.Null(RawSessionDataBuilder.Extractor().Extract(raw).ClickIdInvalid);
    }
}
