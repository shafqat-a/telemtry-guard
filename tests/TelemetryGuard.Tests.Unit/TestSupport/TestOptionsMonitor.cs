using Microsoft.Extensions.Options;

namespace TelemetryGuard.Tests.Unit.TestSupport;

/// <summary>Minimal <see cref="IOptionsMonitor{T}"/> stub returning a fixed instance.
/// <see cref="CurrentValue"/> is settable so tests can prove options are re-read per call
/// (config tunability without rebuild/re-registration).</summary>
public sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; set; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
