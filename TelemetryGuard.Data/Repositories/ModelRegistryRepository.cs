using Dapper;
using TelemetryGuard.Data.Models;

namespace TelemetryGuard.Data.Repositories;

/// <summary>
/// Dapper repository over dbo.ModelRegistry (P2-02). PLATFORM-scoped, so unlike every
/// tenant repository it takes <see cref="ISystemConnectionFactory"/> (DAT-03's
/// background-job factory) and is registered as a SINGLETON — there is no ambient
/// tenant and nothing on the request path may call it.
///
/// PUBLIC (the tenant repositories are internal) because TelemetryGuard.Api's
/// composition root constructs it directly, before the service provider exists, to
/// resolve the promoted model for AddScoringPipeline (P2-02 step 5).
///
/// WRITE side lives in TelemetryGuard.Training (ModelRegistryClient) — that project
/// deliberately does not reference TelemetryGuard.Data (RSK-08). The two share the
/// column contract of migration 0009; RegistryRoundTripTests proves they agree.
/// </summary>
public sealed class ModelRegistryRepository(ISystemConnectionFactory systemConnections)
    : IModelRegistryRepository
{
    public async Task<ServingModel?> GetServingModelAsync(CancellationToken ct)
    {
        await using var conn = await systemConnections.OpenSystemAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ServingModel>(new CommandDefinition(
            """
            SELECT TOP (1) ModelId, ScorerVersion, Status, ArtifactPath, ArtifactSha256, FeatureSetVersion
            FROM dbo.ModelRegistry
            WHERE Status IN (@Active, @Shadow)
            ORDER BY CASE Status WHEN @Active THEN 0 ELSE 1 END;
            """,
            new { Active = ModelStatuses.Active, Shadow = ModelStatuses.Shadow },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ModelRegistryEntry>> ListRecentAsync(int limit, CancellationToken ct)
    {
        await using var conn = await systemConnections.OpenSystemAsync(ct);
        var rows = await conn.QueryAsync<ModelRegistryEntry>(new CommandDefinition(
            """
            SELECT TOP (@Limit)
                ModelId, ScorerVersion, FeatureSetVersion, Status, TrainedUtc, WindowFromUtc, WindowToUtc,
                TrainRows, ValidationRows, Positives, Negatives, DroppedConflicts,
                Auc, Auprc, F1, MinAucGate, ScoreP99Ms,
                ArtifactPath, ArtifactSha256, RejectReason, Notes,
                ShadowSinceUtc, ActiveSinceUtc, RetiredUtc, CreatedUtc
            FROM dbo.ModelRegistry
            ORDER BY CreatedUtc DESC;
            """,
            new { Limit = limit },
            cancellationToken: ct));
        return rows.AsList();
    }
}
