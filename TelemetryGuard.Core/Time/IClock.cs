namespace TelemetryGuard.Core.Time;

/// <summary>Testable time source. Domain code must use this instead of
/// DateTime.UtcNow / DateTimeOffset.UtcNow.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
