using TelemetryGuard.Core.Analytics;
using TelemetryGuard.RiskEngine.Contracts;

namespace TelemetryGuard.Tests.Unit.Core;

/// <summary>
/// REQ-01 pure-function tests: the score-histogram bucket math that every
/// producer (ClickHouse/Kusto SQL/KQL, RollupService.MapDay, VerdictFinalizer's
/// live increment) is built on. No DB, no container — this is the reference
/// oracle the SQL/KQL countIf-per-bucket aggregates must match.
/// </summary>
public sealed class ScoreHistogramMathTests
{
    // ------------------------------------------------------------ BucketIndex =

    [Theory]
    [InlineData(0, 0)]
    [InlineData(9, 0)]
    [InlineData(10, 1)]
    [InlineData(19, 1)]
    [InlineData(29, 2)]
    [InlineData(30, 3)]   // TG's own allow/challenge boundary (30) — still bucket 3, not special-cased
    [InlineData(69, 6)]
    [InlineData(70, 7)]   // TG's own challenge/block boundary (70) — still bucket 7
    [InlineData(89, 8)]
    [InlineData(90, 9)]
    [InlineData(99, 9)]
    [InlineData(100, 10)] // the eleventh, exact-100 bucket
    public void BucketIndex_MapsScoreToExpectedBucket(int score, int expectedBucket)
    {
        Assert.Equal(expectedBucket, ScoreHistogramMath.BucketIndex(score));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void BucketIndex_OutOfRange_Throws(int score)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScoreHistogramMath.BucketIndex(score));
    }

    // ---------------------------------------------------------------- Edges =

    [Fact]
    public void Edges_AreTheElevenLiteralLowerBounds()
    {
        Assert.Equal(new[] { 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 }, ScoreHistogramMath.Edges);
        Assert.Equal(11, ScoreHistogramMath.Edges.Count);
        Assert.Equal(10, ScoreHistogramMath.BucketWidth);
    }

    // ------------------------------------------------------------ Aggregate =

    [Fact]
    public void Aggregate_SeededScores_ProducesExactBucketVectorAndSumSq()
    {
        // A hand-picked set spanning every bucket, with a duplicate in bucket 90
        // and none in bucket 20/40/60 to prove zeros are real zeros, not omitted.
        int[] scores = [0, 5, 9, 10, 19, 55, 71, 71, 90, 95, 100, 100, 100];

        var h = ScoreHistogramMath.Aggregate(scores);

        Assert.Equal(3, h.Bucket00);   // 0, 5, 9
        Assert.Equal(2, h.Bucket10);   // 10, 19
        Assert.Equal(0, h.Bucket20);
        Assert.Equal(0, h.Bucket30);
        Assert.Equal(0, h.Bucket40);
        Assert.Equal(1, h.Bucket50);   // 55
        Assert.Equal(0, h.Bucket60);
        Assert.Equal(2, h.Bucket70);   // 71, 71
        Assert.Equal(0, h.Bucket80);
        Assert.Equal(2, h.Bucket90);   // 90, 95
        Assert.Equal(3, h.Bucket100);  // 100, 100, 100

        var expectedSumSq = scores.Sum(s => (long)s * s);
        Assert.Equal(expectedSumSq, h.SumSq);
        Assert.Equal(scores.Length, h.Total);
    }

    [Fact]
    public void Aggregate_EmptyScoreSet_IsTheEmptyHistogram()
    {
        var h = ScoreHistogramMath.Aggregate([]);

        Assert.Equal(ScoreHistogramCounts.Empty, h);
        Assert.Equal(0, h.Total);
        Assert.Equal(0, h.SumSq);
    }

    [Fact]
    public void SingleScore_SetsExactlyOneBucketToOne()
    {
        var h = ScoreHistogramMath.SingleScore(73);

        Assert.Equal(1, h.Total);
        Assert.Equal(1, h.Bucket70);
        Assert.Equal(0, h.Bucket00 + h.Bucket10 + h.Bucket20 + h.Bucket30 + h.Bucket40 +
                        h.Bucket50 + h.Bucket60 + h.Bucket80 + h.Bucket90 + h.Bucket100);
        Assert.Equal(73L * 73, h.SumSq);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(70)]
    [InlineData(100)]
    public void SingleScore_BoundaryScores_LandInTheCorrectBucket(int score)
    {
        var h = ScoreHistogramMath.SingleScore(score);
        Assert.Equal(1, h.ToArray()[ScoreHistogramMath.BucketIndex(score)]);
        Assert.Equal(1, h.Total);
    }

    // ---------------------------------------------------- invariants (req c) =

    /// <summary>REQ-01 acceptance criterion (c), part 1: SUM(bucket00..100) ==
    /// Events for any set of scores — proven here as a pure property, not just
    /// eyeballed on one seeded example.</summary>
    [Fact]
    public void Aggregate_SumOfAllBuckets_AlwaysEqualsEventCount()
    {
        var rng = new Random(20260817);
        for (var trial = 0; trial < 200; trial++)
        {
            var count = rng.Next(0, 500);
            var scores = Enumerable.Range(0, count).Select(_ => rng.Next(0, 101)).ToArray();

            var h = ScoreHistogramMath.Aggregate(scores);

            Assert.Equal(scores.Length, h.Total);
            Assert.Equal(scores.Length, h.ToArray().Sum());
        }
    }

    /// <summary>REQ-01 acceptance criterion (c), part 2: Allowed + Challenged +
    /// Blocked == Events also holds, using TG's own BandMapper thresholds
    /// (30/70) alongside the same score set the histogram is built from — proves
    /// the two aggregations (band counts, decile counts) never disagree on the
    /// total event count.</summary>
    [Fact]
    public void BandCounts_PlusHistogramTotal_AgreeOnEventCount_ForAnyScoreSet()
    {
        var rng = new Random(982026);
        for (var trial = 0; trial < 200; trial++)
        {
            var count = rng.Next(0, 500);
            var scores = Enumerable.Range(0, count).Select(_ => rng.Next(0, 101)).ToArray();

            var h = ScoreHistogramMath.Aggregate(scores);
            var allowed = scores.Count(s => BandMapper.ToBand(s) == VerdictBand.Allow);
            var challenged = scores.Count(s => BandMapper.ToBand(s) == VerdictBand.Challenge);
            var blocked = scores.Count(s => BandMapper.ToBand(s) == VerdictBand.Block);

            Assert.Equal(scores.Length, allowed + challenged + blocked);
            Assert.Equal(scores.Length, h.Total);
        }
    }

    // ------------------------------------------------------- ScoreHistogramCounts =

    [Fact]
    public void Operator_Plus_SumsEachBucketAndSumSqIndependently()
    {
        var a = new ScoreHistogramCounts(1, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 100);
        var b = new ScoreHistogramCounts(0, 3, 1, 0, 0, 0, 0, 0, 0, 0, 0, 50);

        var sum = a + b;

        Assert.Equal(1, sum.Bucket00);
        Assert.Equal(5, sum.Bucket10);
        Assert.Equal(1, sum.Bucket20);
        Assert.Equal(150, sum.SumSq);
        Assert.Equal(a.Total + b.Total, sum.Total);
    }

    [Fact]
    public void ToArray_ReturnsBucketsInEdgesOrder()
    {
        var h = new ScoreHistogramCounts(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 0);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }, h.ToArray());
        Assert.Equal(ScoreHistogramMath.Edges.Count, h.ToArray().Length);
    }

    [Fact]
    public void Empty_IsAllZero()
    {
        Assert.Equal(default, ScoreHistogramCounts.Empty);
        Assert.Equal(0, ScoreHistogramCounts.Empty.Total);
        Assert.Equal(0, ScoreHistogramCounts.Empty.SumSq);
    }
}
