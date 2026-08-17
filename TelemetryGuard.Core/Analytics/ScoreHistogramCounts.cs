namespace TelemetryGuard.Core.Analytics;

/// <summary>
/// Score distribution across eleven fixed decile buckets — [0-9], [10-19], ...,
/// [90-99], plus an eleventh bucket for the single value 100 (scores are 0-100
/// inclusive, spec §6.3) — alongside the sum of squared scores (for variance).
///
/// MERGEABILITY CONTRACT (mirrors DAT-06/P2-01's ScoreSum): every field here is
/// an absolute, summable count — never an average or a derived ratio — so it can
/// be summed across rows/days (e.g. PublisherSummaryRepository.GetTopPlacementsAsync's
/// range SUM) or overwritten wholesale by an idempotent rollup MERGE without ever
/// double-counting. Bucket fields are `int` and SumSq is `long`, matching the
/// dbo.*DailySummaries column types added by migration 0010 exactly — callers
/// reading from a wider-typed source (e.g. ClickHouse/Kusto count aggregates)
/// convert into this shape at the query boundary.
/// </summary>
public readonly record struct ScoreHistogramCounts(
    int Bucket00, int Bucket10, int Bucket20, int Bucket30, int Bucket40,
    int Bucket50, int Bucket60, int Bucket70, int Bucket80, int Bucket90,
    int Bucket100, long SumSq)
{
    /// <summary>All-zero histogram — the correct "no data" value (never fabricated;
    /// spec §7 missing != zero still holds because Events/ScoredEvents stays the
    /// authoritative "was there any data" signal, exactly as it already does for
    /// ScoreSum == 0).</summary>
    public static readonly ScoreHistogramCounts Empty = default;

    /// <summary>The eleven bucket counts in <see cref="ScoreHistogramMath.Edges"/> order.</summary>
    public int[] ToArray() =>
    [
        Bucket00, Bucket10, Bucket20, Bucket30, Bucket40,
        Bucket50, Bucket60, Bucket70, Bucket80, Bucket90, Bucket100,
    ];

    /// <summary>Sum of every bucket. Must equal the owning row's Events/ScoredEvents
    /// count — asserted as a property test in ScoreHistogramMathTests.</summary>
    public long Total =>
        (long)Bucket00 + Bucket10 + Bucket20 + Bucket30 + Bucket40 +
        Bucket50 + Bucket60 + Bucket70 + Bucket80 + Bucket90 + Bucket100;

    /// <summary>Bucket-wise merge (e.g. combining per-day histograms into a range
    /// total) — the struct-level equivalent of summing ScoreSum across rows.</summary>
    public static ScoreHistogramCounts operator +(ScoreHistogramCounts a, ScoreHistogramCounts b) => new(
        a.Bucket00 + b.Bucket00, a.Bucket10 + b.Bucket10, a.Bucket20 + b.Bucket20,
        a.Bucket30 + b.Bucket30, a.Bucket40 + b.Bucket40, a.Bucket50 + b.Bucket50,
        a.Bucket60 + b.Bucket60, a.Bucket70 + b.Bucket70, a.Bucket80 + b.Bucket80,
        a.Bucket90 + b.Bucket90, a.Bucket100 + b.Bucket100, a.SumSq + b.SumSq);
}
