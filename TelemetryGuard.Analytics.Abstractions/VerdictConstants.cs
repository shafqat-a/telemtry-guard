namespace TelemetryGuard.Analytics.Abstractions;

/// <summary>
/// Canonical verdict band strings (storage contract). These are the only
/// values ever written to <see cref="ClickEvent.Band"/>.
/// </summary>
public static class VerdictBands
{
    public const string Allow = "allow";        // score 0–30
    public const string Challenge = "challenge"; // score 31–70
    public const string Block = "block";         // score 71–100
}
