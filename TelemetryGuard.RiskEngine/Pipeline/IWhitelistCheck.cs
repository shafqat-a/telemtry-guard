namespace TelemetryGuard.RiskEngine.Pipeline;

/// <summary>Hot-path whitelist membership check over the Redis mirror sets that DAT-07's
/// repository maintains (SQL is the source of truth; scoring never queries SQL — D19,
/// &lt; 50 ms budget).</summary>
public interface IWhitelistCheck
{
    /// <summary>True when ip or visitorId is tenant-whitelisted (D19 override loop).</summary>
    Task<bool> IsWhitelistedAsync(string ip, string? visitorId, CancellationToken ct);
}
