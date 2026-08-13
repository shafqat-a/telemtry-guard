namespace TelemetryGuard.Core.Time;

/// <summary>Production clock. Register as a singleton.</summary>
public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
