using System.Text.Json;

namespace TelemetryGuard.RiskEngine.Scoring;

/// <summary>
/// RSK-08 (D18 version stamping): reads the <c>scorer_version</c> field out of the
/// Trainer's <c>metadata.json</c> (written alongside model.zip, step 4) at DI
/// registration time. <see cref="MlNetScorer.ScorerVersion"/> is NEVER hard-coded —
/// this is the only place a model's version string enters the serving process.
/// </summary>
public static class ModelMetadataReader
{
    /// <summary>Reads {ModelPath}/metadata.json and returns its "scorer_version" string.
    /// Throws InvalidOperationException with an actionable message when the file/field
    /// is missing — a missing/blank scorer_version at startup is a fail-fast condition,
    /// not something to silently default (D18: heuristic-era and model-era rows must
    /// always be distinguishable).</summary>
    public static string ReadScorerVersion(string modelDir)
    {
        var path = Path.Combine(modelDir, "metadata.json");
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Scoring:ModelPath '{modelDir}' has no metadata.json (RSK-08 Trainer export shape). " +
                "Cannot determine scorer_version.");
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("scorer_version", out var el)
            || el.GetString() is not { Length: > 0 } version)
        {
            throw new InvalidOperationException(
                $"'{path}' has no non-empty scorer_version field (RSK-08 Trainer export shape).");
        }

        return version;
    }
}
