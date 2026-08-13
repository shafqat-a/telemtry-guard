using TelemetryGuard.RiskEngine.Features;

namespace TelemetryGuard.RiskEngine.Pipeline;

/// <summary>Port for the pipeline's campaign-config prefetch. The real implementation
/// lives in the API host (backed by the DAT-05 campaign repository + IMemoryCache with a
/// 60 s TTL — the risk engine never references TelemetryGuard.Data); this project ships
/// only <see cref="NullCampaignContextProvider"/> so the engine stays testable when
/// DAT-05's repository type is unavailable at wiring time.</summary>
public interface ICampaignContextProvider
{
    /// <summary>Null-safe: campaignId null/unknown → null (extraction then yields
    /// IpGeoTargetMismatch = null — absence of config is not evidence).</summary>
    Task<CampaignContext?> GetAsync(string? campaignId, CancellationToken ct);
}

/// <summary>Default (TryAdd) implementation: no campaign config available.
/// The API host overrides it with the DAT-05-backed provider.</summary>
public sealed class NullCampaignContextProvider : ICampaignContextProvider
{
    public Task<CampaignContext?> GetAsync(string? campaignId, CancellationToken ct)
        => Task.FromResult<CampaignContext?>(null);
}
