namespace TelemetryGuard.Data.Models;

/// <summary>One dbo.ModelRegistry row (list/inspection shape).</summary>
public sealed record ModelRegistryEntry(
    Guid ModelId, string? ScorerVersion, int FeatureSetVersion, string Status,
    DateTime TrainedUtc, DateTime WindowFromUtc, DateTime WindowToUtc,
    int TrainRows, int ValidationRows, int Positives, int Negatives, int DroppedConflicts,
    double Auc, double Auprc, double F1, double MinAucGate, double? ScoreP99Ms,
    string? ArtifactPath, byte[]? ArtifactSha256, string? RejectReason, string? Notes,
    DateTime? ShadowSinceUtc, DateTime? ActiveSinceUtc, DateTime? RetiredUtc, DateTime CreatedUtc);

/// <summary>The single row the serving host must load: 'active' wins over 'shadow'
/// (at most one model is loaded per process — see P2-02 step 4).</summary>
public sealed record ServingModel(
    Guid ModelId, string ScorerVersion, string Status,
    string ArtifactPath, byte[] ArtifactSha256, int FeatureSetVersion);
