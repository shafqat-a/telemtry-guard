using TelemetryGuard.Core.Tenancy;

namespace TelemetryGuard.Analytics.Abstractions;

/// <summary>
/// A single training label written to the analytics store (spec D18/D19).
/// </summary>
public sealed record LabelEvent(
    TenantId TenantId,
    string SessionId,
    string Label,          // LabelValues constants
    string LabelSource,    // LabelSources constants
    DateTime CreatedAtUtc);

/// <summary>Canonical label value strings (storage contract).</summary>
public static class LabelValues
{
    public const string Fraud = "fraud";
    public const string Legit = "legit";
}

/// <summary>Canonical label source strings (storage contract).</summary>
public static class LabelSources
{
    public const string T1Rule = "t1_rule";
    public const string SyntheticBot = "synthetic_bot";
    public const string Conversion = "conversion";
    public const string ReviewScreen = "review_screen";
}
