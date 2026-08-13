namespace TelemetryGuard.RiskEngine.Rules;

/// <summary>Floor == null when no rule fired. Hits lists fired rule names (snake_case,
/// matching spec §7 signal names) in evaluation order.</summary>
public sealed record T1Result(int? Floor, IReadOnlyList<string> Hits)
{
    public static T1Result None { get; } = new(null, Array.Empty<string>());
}

public interface IT1RuleEngine
{
    T1Result Evaluate(in TelemetryGuard.RiskEngine.Contracts.FraudFeatureVector v);
}
