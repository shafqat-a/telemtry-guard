using TelemetryGuard.RiskEngine.Contracts;

namespace TelemetryGuard.Tests.Unit.RiskContracts;

public class BandMapperTests
{
    [Theory]
    [InlineData(0, VerdictBand.Allow)]
    [InlineData(15, VerdictBand.Allow)]
    [InlineData(30, VerdictBand.Allow)]
    [InlineData(31, VerdictBand.Challenge)]
    [InlineData(50, VerdictBand.Challenge)]
    [InlineData(70, VerdictBand.Challenge)]
    [InlineData(71, VerdictBand.Block)]
    [InlineData(100, VerdictBand.Block)]
    public void ToBand_MapsScoreToExpectedBand(int score, VerdictBand expected)
    {
        Assert.Equal(expected, BandMapper.ToBand(score));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void ToBand_ThrowsOutsideZeroToHundred(int score)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BandMapper.ToBand(score));
    }
}
