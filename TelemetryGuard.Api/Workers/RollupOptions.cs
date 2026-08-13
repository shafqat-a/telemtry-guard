namespace TelemetryGuard.Api.Workers;

/// <summary>Config for the ANA-07 rollup job; bound to the "Rollup" section.</summary>
public sealed class RollupOptions
{
    public double IntervalMinutes { get; init; } = 15;
    public int LookbackDays { get; init; } = 3;        // first-run / no-watermark backfill window
    public int TopFlaggedLimit { get; init; } = 100;   // flagged sources per tenant per day
    public string RollupName { get; init; } = "verdict_daily"; // key into dbo.RollupWatermarks
}
