namespace TelemetryGuard.Analytics.Abstractions;

/// <summary>
/// Write path for the label loop (spec D18/D19): T1 rule hits are weak
/// positives, synthetic Playwright bots are guaranteed positives, confirmed
/// conversions / review-screen "real customer" marks are negatives.
/// </summary>
public interface ILabelSink
{
    ValueTask WriteAsync(LabelEvent label, CancellationToken ct);
}
