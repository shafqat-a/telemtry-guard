namespace TelemetryGuard.RiskEngine.Pipeline;

/// <summary>Port for scheduling a whitelist cache rebuild OFF the request path when a
/// consulted t:{tid}:wl:{sourceType} key is missing (DAT-07 miss behavior: a missing key
/// means "unknown" — never a SQL fallback on the hot path). The API host replaces the
/// no-op default with an adapter that queues DAT-07's IWhitelistRepository.RebuildCacheAsync
/// on a background task.</summary>
public interface IWhitelistCacheRebuilder
{
    /// <summary>Fire-and-forget: must return immediately and never block the caller.
    /// sourceType ∈ "ip" | "device_id" | "fingerprint" (DAT-07 contract).</summary>
    void ScheduleRebuild(string sourceType);
}

/// <summary>Default no-op (registered via TryAdd so the API host's real adapter wins).
/// Keeps the risk engine buildable/testable without a SQL-side whitelist repository.</summary>
public sealed class NoOpWhitelistCacheRebuilder : IWhitelistCacheRebuilder
{
    public void ScheduleRebuild(string sourceType)
    {
        // Intentionally nothing: the cache stays cold until DAT-07's writer repopulates it.
    }
}
