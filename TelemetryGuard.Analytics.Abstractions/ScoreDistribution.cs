namespace TelemetryGuard.Analytics.Abstractions;

/// <summary>
/// Mergeable score distribution over the closed 0..100 score range. Buckets 00
/// through 90 cover ten values each; Bucket100 covers exactly 100. SumSq is the
/// sum of score squared and allows downstream consumers to derive dispersion.
/// </summary>
public sealed record ScoreDistribution(
    long Bucket00,
    long Bucket10,
    long Bucket20,
    long Bucket30,
    long Bucket40,
    long Bucket50,
    long Bucket60,
    long Bucket70,
    long Bucket80,
    long Bucket90,
    long Bucket100,
    long SumSq)
{
    public const int BucketWidth = 10;
    public static IReadOnlyList<int> Edges { get; } =
        Array.AsReadOnly(new[] { 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 });

    public static ScoreDistribution Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    public IReadOnlyList<long> Counts =>
        [Bucket00, Bucket10, Bucket20, Bucket30, Bucket40, Bucket50,
         Bucket60, Bucket70, Bucket80, Bucket90, Bucket100];

    public long Count => Bucket00 + Bucket10 + Bucket20 + Bucket30 + Bucket40
        + Bucket50 + Bucket60 + Bucket70 + Bucket80 + Bucket90 + Bucket100;

    public static ScoreDistribution ForScore(int score)
    {
        if (score is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(score), score, "Score must be between 0 and 100.");

        var buckets = new long[11];
        buckets[Math.Min(score / BucketWidth, 10)] = 1;
        return FromCounts(buckets, checked((long)score * score));
    }

    public static ScoreDistribution FromCounts(IReadOnlyList<long> counts, long sumSq)
    {
        ArgumentNullException.ThrowIfNull(counts);
        if (counts.Count != 11)
            throw new ArgumentException("A score distribution must contain exactly 11 buckets.", nameof(counts));
        if (counts.Any(c => c < 0))
            throw new ArgumentOutOfRangeException(nameof(counts), "Bucket counts cannot be negative.");
        if (sumSq < 0)
            throw new ArgumentOutOfRangeException(nameof(sumSq), "SumSq cannot be negative.");

        return new ScoreDistribution(
            counts[0], counts[1], counts[2], counts[3], counts[4], counts[5],
            counts[6], counts[7], counts[8], counts[9], counts[10], sumSq);
    }

    public static ScoreDistribution operator +(ScoreDistribution left, ScoreDistribution right) => new(
        checked(left.Bucket00 + right.Bucket00), checked(left.Bucket10 + right.Bucket10),
        checked(left.Bucket20 + right.Bucket20), checked(left.Bucket30 + right.Bucket30),
        checked(left.Bucket40 + right.Bucket40), checked(left.Bucket50 + right.Bucket50),
        checked(left.Bucket60 + right.Bucket60), checked(left.Bucket70 + right.Bucket70),
        checked(left.Bucket80 + right.Bucket80), checked(left.Bucket90 + right.Bucket90),
        checked(left.Bucket100 + right.Bucket100), checked(left.SumSq + right.SumSq));
}

