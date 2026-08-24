using TelemetryGuard.Analytics.Abstractions;

namespace TelemetryGuard.Tests.Unit.Analytics;

public sealed class ScoreDistributionTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(9, 0)]
    [InlineData(10, 1)]
    [InlineData(99, 9)]
    [InlineData(100, 10)]
    public void ForScore_UsesDocumentedBoundaries(int score, int expectedBucket)
    {
        var distribution = ScoreDistribution.ForScore(score);

        Assert.Equal(1, distribution.Count);
        Assert.Equal(1, distribution.Counts[expectedBucket]);
        Assert.Equal((long)score * score, distribution.SumSq);
    }

    [Fact]
    public void Addition_PreservesCountsAndSumOfSquares()
    {
        var sum = ScoreDistribution.ForScore(9) + ScoreDistribution.ForScore(10)
            + ScoreDistribution.ForScore(100);

        Assert.Equal(new long[] { 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 1 }, sum.Counts);
        Assert.Equal(3, sum.Count);
        Assert.Equal(10_181, sum.SumSq);
    }
}
