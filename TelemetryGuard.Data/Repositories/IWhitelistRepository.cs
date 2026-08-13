using TelemetryGuard.Data.Models;

namespace TelemetryGuard.Data.Repositories;

/// <summary>
/// Tenant whitelist (D19): a whitelisted source must not be challenged/blocked.
/// SQL Server (dbo.WhitelistEntries, RLS-protected) is the source of truth; the
/// Redis SET <c>t:{tenantId}:wl:{sourceType}</c> is a cache rebuilt on every write
/// (1-hour TTL). Scoring (RSK-07) reads ONLY the Redis set via SISMEMBER inside the
/// &lt;50 ms budget — a missing key means "unknown": treat as not whitelisted for the
/// current request and schedule <see cref="RebuildCacheAsync"/> off the request path;
/// never fall back to SQL on the hot path.
/// </summary>
public interface IWhitelistRepository
{
    /// <summary>Insert (idempotent on (SourceType, Value): re-adding an existing entry
    /// returns its existing Id and updates Reason/ExpiresUtc). Rebuilds the Redis cache set.
    /// Source='review_screen' with a SessionId additionally emits a negative
    /// (LabelValues.Legit) label via ILabelSink (D19).</summary>
    Task<long> AddAsync(NewWhitelistEntry entry, CancellationToken ct);

    /// <summary>Delete by natural key; true when a row was removed. Rebuilds the cache set.</summary>
    Task<bool> RemoveAsync(string sourceType, string value, CancellationToken ct);

    /// <summary>Single entry by Id, or null when it does not exist for this tenant
    /// (RLS makes cross-tenant ids look nonexistent). Used by API-07's DELETE route.</summary>
    Task<WhitelistEntry?> GetByIdAsync(long id, CancellationToken ct);

    /// <summary>Delete by Id; true when a row was removed. Looks up the row's SourceType
    /// first and rebuilds that cache set afterwards. Used by API-07's DELETE route.</summary>
    Task<bool> RemoveByIdAsync(long id, CancellationToken ct);

    /// <summary>Entries (optionally one sourceType), including expired; newest first;
    /// paged for API-07 (offset >= 0, limit clamped 1..200 by the caller).</summary>
    Task<IReadOnlyList<WhitelistEntry>> ListAsync(string? sourceType, int offset, int limit, CancellationToken ct);

    /// <summary>Batch membership from SQL truth (NOT the hot path — RSK-07 uses Redis).
    /// Returns value -> isWhitelisted (expired entries count as false).</summary>
    Task<IReadOnlyDictionary<string, bool>> AreWhitelistedAsync(
        string sourceType, IReadOnlyCollection<string> values, CancellationToken ct);

    /// <summary>Rewrites the Redis set t:{tenantId}:wl:{sourceType} from SQL
    /// (non-expired values only), TTL 1 hour. Safe to call concurrently. Called by
    /// writes here and, off the hot path, by RSK-07 on cache miss.</summary>
    Task RebuildCacheAsync(string sourceType, CancellationToken ct);
}
