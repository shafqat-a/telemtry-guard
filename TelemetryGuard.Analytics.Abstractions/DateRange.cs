namespace TelemetryGuard.Analytics.Abstractions;

/// <summary>
/// Half-open UTC interval <c>[FromUtc, ToUtc)</c>.
/// </summary>
public sealed record DateRange
{
    public DateRange(DateTime fromUtc, DateTime toUtc)
    {
        if (fromUtc.Kind != DateTimeKind.Utc || toUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("DateRange bounds must be DateTimeKind.Utc.");
        if (toUtc < fromUtc)
            throw new ArgumentException("ToUtc must be >= FromUtc.");
        FromUtc = fromUtc;
        ToUtc = toUtc;
    }

    public DateTime FromUtc { get; }
    /// <summary>Exclusive upper bound.</summary>
    public DateTime ToUtc { get; }

    public static DateRange LastDays(int days, DateTime nowUtc) =>
        new(nowUtc.AddDays(-days), nowUtc);
}
