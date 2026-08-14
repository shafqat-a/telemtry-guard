using System.Security.Cryptography;
using System.Text.Json;
using TelemetryGuard.Data;
using TelemetryGuard.Data.Models;
using TelemetryGuard.Data.Repositories;

namespace TelemetryGuard.Api.Startup;

/// <summary>Outcome of resolving the promoted model at startup. <see cref="ModelDir"/>
/// is null when nothing may be loaded (no promoted row, or the artifact failed
/// verification) — the heuristic then enforces, which is always the SAFE direction.</summary>
public sealed record ServingModelState(
    string? ScorerVersion, string? Status, string? ModelDir, IReadOnlyList<string> Log);

public static class ModelRegistryBootstrap
{
    /// <summary>
    /// P2-02: reads dbo.ModelRegistry's promoted row and verifies its artifact before
    /// the host loads anything. Fail-SAFE by construction: ANY failure (SQL down,
    /// missing dir, hash mismatch, scorer_version mismatch, feature-set mismatch)
    /// logs and returns a state with ModelDir = null, i.e. the heuristic keeps
    /// enforcing. It can never fail OPEN toward model enforcement.
    /// Blocking on the async read is deliberate: this runs once, at composition time,
    /// before the host starts (no synchronization context to deadlock on).
    /// </summary>
    public static ServingModelState Resolve(
        IConfiguration configuration, int expectedFeatureSetVersion, TimeSpan timeout)
    {
        var log = new List<string>();
        try
        {
            var repo = new ModelRegistryRepository(SystemConnections.FromConfiguration(configuration));
            using var cts = new CancellationTokenSource(timeout);
            var serving = repo.GetServingModelAsync(cts.Token).GetAwaiter().GetResult();
            if (serving is null)
            {
                log.Add("Model registry holds no active/shadow row — heuristic enforcing, no model loaded.");
                return new ServingModelState(null, null, null, log);
            }

            var dir = ResolveArtifactDir(configuration, serving);
            var error = VerifyArtifact(dir, serving, expectedFeatureSetVersion);
            if (error is not null)
            {
                log.Add($"Model {serving.ScorerVersion} ({serving.Status}) REJECTED at load: {error}. " +
                        "Heuristic enforcing, no model loaded.");
                return new ServingModelState(serving.ScorerVersion, serving.Status, null, log);
            }

            log.Add($"Model registry: serving {serving.ScorerVersion} as '{serving.Status}' from {dir}.");
            return new ServingModelState(serving.ScorerVersion, serving.Status, dir, log);
        }
        catch (Exception ex)
        {
            log.Add($"Model registry read failed ({ex.GetType().Name}: {ex.Message}); " +
                    "heuristic enforcing, no model loaded.");
            return new ServingModelState(null, null, null, log);
        }
    }

    /// <summary>Scoring:Registry:ArtifactRoot (when set) + scorer_version wins over the
    /// stored absolute path — deployments mount the artifact volume elsewhere than the
    /// machine that trained the model.</summary>
    internal static string ResolveArtifactDir(IConfiguration configuration, ServingModel serving)
    {
        var root = configuration["Scoring:Registry:ArtifactRoot"];
        return string.IsNullOrWhiteSpace(root)
            ? serving.ArtifactPath
            : Path.Combine(root, serving.ScorerVersion);
    }

    /// <summary>Returns null when the artifact is loadable, else the reason. Checks, in
    /// order: model.zip exists → SHA-256 matches the registry → metadata.json's
    /// scorer_version equals the registry's → metadata.json's feature_set_version equals
    /// the running FraudFeatureVector.FeatureSetVersion (D18 lineage: never serve a model
    /// trained against a different feature set).</summary>
    internal static string? VerifyArtifact(string dir, ServingModel serving, int expectedFeatureSetVersion)
    {
        var modelZip = Path.Combine(dir, "model.zip");
        if (!File.Exists(modelZip))
        {
            return $"model.zip not found at '{modelZip}'";
        }

        byte[] actualHash;
        using (var stream = File.OpenRead(modelZip))
        {
            actualHash = SHA256.HashData(stream);
        }
        if (!actualHash.AsSpan().SequenceEqual(serving.ArtifactSha256))
        {
            return $"model.zip SHA-256 mismatch: expected {Convert.ToHexString(serving.ArtifactSha256)}, " +
                   $"got {Convert.ToHexString(actualHash)}";
        }

        var metadataPath = Path.Combine(dir, "metadata.json");
        if (!File.Exists(metadataPath))
        {
            return $"metadata.json not found at '{metadataPath}'";
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(metadataPath));
        }
        catch (JsonException ex)
        {
            return $"metadata.json is not valid JSON: {ex.Message}";
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("scorer_version", out var svEl) || svEl.GetString() is not { Length: > 0 } sv)
            {
                return "metadata.json has no non-empty scorer_version field";
            }
            if (!string.Equals(sv, serving.ScorerVersion, StringComparison.Ordinal))
            {
                return $"metadata.json scorer_version '{sv}' does not match registry scorer_version '{serving.ScorerVersion}'";
            }

            if (!root.TryGetProperty("feature_set_version", out var fsvEl) || fsvEl.ValueKind != JsonValueKind.Number)
            {
                return "metadata.json has no numeric feature_set_version field";
            }
            var fsv = fsvEl.GetInt32();
            if (fsv != expectedFeatureSetVersion)
            {
                return $"metadata.json feature_set_version {fsv} does not match the running " +
                       $"FraudFeatureVector.FeatureSetVersion {expectedFeatureSetVersion}";
            }
        }

        return null;
    }

    /// <summary>Translates the state into the RSK-08 Scoring keys. This is the ONLY
    /// promotion mechanism: 'active' -> MlNet enforces, 'shadow' -> heuristic enforces +
    /// listen-only shadow scoring, nothing -> pure heuristic (and Scoring:ModelPath from
    /// appsettings is explicitly cleared, so a stale config value can never resurrect a
    /// demoted model).</summary>
    public static Dictionary<string, string?> ToScoringOverrides(ServingModelState state) =>
        state.ModelDir is null
            ? new() { ["Scoring:ModelPath"] = "", ["Scoring:Scorer"] = "Heuristic", ["Scoring:Mode"] = "Enforce" }
            : state.Status == ModelStatuses.Active
                ? new() { ["Scoring:ModelPath"] = state.ModelDir, ["Scoring:Scorer"] = "MlNet",     ["Scoring:Mode"] = "Enforce" }
                : new() { ["Scoring:ModelPath"] = state.ModelDir, ["Scoring:Scorer"] = "Heuristic", ["Scoring:Mode"] = "ListenOnly" };
}
