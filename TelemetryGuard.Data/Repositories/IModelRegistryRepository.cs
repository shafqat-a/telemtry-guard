using TelemetryGuard.Data.Models;

namespace TelemetryGuard.Data.Repositories;

public interface IModelRegistryRepository
{
    /// <summary>The row this process should serve: the 'active' row if one exists,
    /// otherwise the 'shadow' row, otherwise null (pure heuristic).</summary>
    Task<ServingModel?> GetServingModelAsync(CancellationToken ct);

    Task<IReadOnlyList<ModelRegistryEntry>> ListRecentAsync(int limit, CancellationToken ct);
}
