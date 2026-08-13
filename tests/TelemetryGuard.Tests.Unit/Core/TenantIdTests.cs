using TelemetryGuard.Core.Tenancy;

namespace TelemetryGuard.Tests.Unit.Core;

public class TenantIdTests
{
    [Fact]
    public void Parse_RoundTrips_ValidGuid()
    {
        var guid = Guid.NewGuid();

        var id = TenantId.Parse(guid.ToString());

        Assert.Equal(guid, id.Value);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_Throws_FormatException_OnInvalidInput(string input)
    {
        Assert.Throws<FormatException>(() => TenantId.Parse(input));
    }

    [Fact]
    public void Parse_Throws_FormatException_OnEmptyGuid()
    {
        Assert.Throws<FormatException>(() => TenantId.Parse(Guid.Empty.ToString()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void TryParse_ReturnsFalse_ForNullMalformedAndEmptyGuid(string? input)
    {
        var ok = TenantId.TryParse(input, out var id);

        Assert.False(ok);
        Assert.True(id.IsEmpty);
    }

    [Fact]
    public void TryParse_ReturnsTrue_WithCorrectValue_ForValidGuid()
    {
        var guid = Guid.NewGuid();

        var ok = TenantId.TryParse(guid.ToString(), out var id);

        Assert.True(ok);
        Assert.Equal(guid, id.Value);
        Assert.False(id.IsEmpty);
    }

    [Fact]
    public void ToString_Returns_LowercaseHyphenatedDFormat()
    {
        var guid = Guid.NewGuid();
        var id = new TenantId(guid);

        var text = id.ToString();

        Assert.Equal(guid.ToString("D"), text);
        Assert.Equal(text, text.ToLowerInvariant());
        Assert.Equal(36, text.Length);
    }

    [Fact]
    public void TenantIds_WithSameGuid_AreEqual()
    {
        var guid = Guid.NewGuid();

        var a = new TenantId(guid);
        var b = new TenantId(guid);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void DefaultTenantId_IsEmpty()
    {
        Assert.True(default(TenantId).IsEmpty);
    }
}
