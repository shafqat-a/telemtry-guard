namespace TelemetryGuard.Core.Analytics;

/// <summary>
/// Pure score-histogram math shared by every writer of the REQ-01 columns: the
/// ClickHouse/Kusto aggregate queries (SQL/KQL countIf-per-bucket, mirrored here
/// as the reference oracle the unit tests check them against), the ANA-07
/// RollupService mapping, and API-06's live per-verdict increment
/// (VerdictFinalizer). Ten-point decile buckets covering 0-99 plus an eleventh
/// bucket for the single value 100 (scores are always 0-100 inclusive — see
/// BandMapper.ToBand, which this mirrors for the out-of-range guard).
/// </summary>
public static class ScoreHistogramMath
{
    public const int BucketCount = 11;
    public const int BucketWidth = 10;

    /// <summary>Lower edge of each of the eleven buckets, in bucket order. Literal
    /// values (not derived) so the API-07 response contract is stable even if the
    /// computation below ever changes shape.</summary>
    public static readonly IReadOnlyList<int> Edges = [0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100];

    /// <summary>0-based bucket index (0..10) for a single 0-100 score. Throws outside
    /// that range — an out-of-range score is a scorer bug, never silently clamped
    /// (mirrors BandMapper.ToBand).</summary>
    public static int BucketIndex(int score) => score switch
    {
        >= 0 and <= 99 => score / BucketWidth,
        100 => 10,
        _ => throw new ArgumentOutOfRangeException(nameof(score), score, "score must be within 0-100."),
    };

    /// <summary>One event's histogram contribution: a single 1 in its bucket.</summary>
    public static ScoreHistogramCounts SingleScore(int score) => Aggregate([score]);

    /// <summary>Pure aggregation over raw scores — the reference oracle for what the
    /// ClickHouse/Kusto per-bucket countIf/sumif aggregate queries must compute.
    /// Never called over raw event data in production (ClickHouse/Kusto do that
    /// aggregation server-side); its production use is the single-score live path.</summary>
    public static ScoreHistogramCounts Aggregate(IEnumerable<int> scores)
    {
        Span<int> buckets = stackalloc int[BucketCount];
        long sumSq = 0;
        foreach (var score in scores)
        {
            buckets[BucketIndex(score)]++;
            sumSq += (long)score * score;
        }

        return new ScoreHistogramCounts(
            buckets[0], buckets[1], buckets[2], buckets[3], buckets[4],
            buckets[5], buckets[6], buckets[7], buckets[8], buckets[9], buckets[10],
            sumSq);
    }
}
