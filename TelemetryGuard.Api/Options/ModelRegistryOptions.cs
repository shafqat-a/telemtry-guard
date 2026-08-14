namespace TelemetryGuard.Api.Options;

/// <summary>P2-02: registry-driven model selection config. Bound from the
/// "Scoring:Registry" section — a sub-section of the existing "Scoring" object
/// (ScoringOptions ignores it).</summary>
public sealed class ModelRegistryOptions
{
    public const string SectionName = "Scoring:Registry";
    /// <summary>Overrides the registry's stored absolute ArtifactPath (deployments mount
    /// the model volume elsewhere). Empty = use the stored path as-is.</summary>
    public string ArtifactRoot { get; set; } = "";
    public int PollMinutes { get; set; } = 5;
}
