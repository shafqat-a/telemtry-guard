using Dapper;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Data.Models;

namespace TelemetryGuard.Data.Repositories;

/// <summary>
/// Dapper repository for dbo.WhitelistEntries (D19). Connections come exclusively
/// from <see cref="ITenantConnectionFactory"/> (RLS-scoped); every statement
/// additionally carries an explicit TenantId = @TenantId predicate for index seeks
/// (D11). Every write invalidates and rebuilds the Redis cache set
/// t:{tenantId}:wl:{sourceType} that RSK-07 reads via SISMEMBER; cache failures are
/// logged and never fail the SQL write (SQL remains the source of truth).
/// A review-screen add with a SessionId also emits a negative (legit) training label
/// via the optional <see cref="ILabelSink"/> — the D19 override loop doubles as the
/// labeling loop. The sink registration arrives with ANA-05; until then the
/// constructor's default null keeps this repository resolvable.
/// </summary>
internal sealed class WhitelistRepository(
    ITenantConnectionFactory connections,
    ITenantContext tenant,
    IConnectionMultiplexer redis,
    ILogger<WhitelistRepository> logger,
    ILabelSink? labelSink = null) : IWhitelistRepository
{
    private static readonly string[] AllowedSourceTypes = ["ip", "device_id", "fingerprint"];
    private static readonly string[] AllowedSources = ["manual", "review_screen"];

    public async Task<long> AddAsync(NewWhitelistEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ValidateSourceType(entry.SourceType, nameof(entry));
        if (!AllowedSources.Contains(entry.Source, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "Source must be one of: manual, review_screen.", nameof(entry));
        }

        long id;
        await using (var conn = await connections.OpenAsync(ct))
        {
            id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                MERGE dbo.WhitelistEntries WITH (HOLDLOCK) AS t
                USING (SELECT @TenantId AS TenantId, @SourceType AS SourceType, @Value AS Value) AS s
                    ON t.TenantId = s.TenantId AND t.SourceType = s.SourceType AND t.Value = s.Value
                WHEN MATCHED THEN UPDATE SET Reason = @Reason, ExpiresUtc = @ExpiresUtc
                WHEN NOT MATCHED THEN INSERT (TenantId, SourceType, Value, Reason, Source, CreatedBy, ExpiresUtc)
                    VALUES (@TenantId, @SourceType, @Value, @Reason, @Source, @CreatedBy, @ExpiresUtc);
                SELECT Id FROM dbo.WhitelistEntries
                WHERE TenantId = @TenantId AND SourceType = @SourceType AND Value = @Value;
                """,
                new
                {
                    TenantId = tenant.TenantId.Value,
                    entry.SourceType,
                    entry.Value,
                    entry.Reason,
                    entry.Source,
                    entry.CreatedBy,
                    entry.ExpiresUtc,
                },
                cancellationToken: ct));
        }

        await RebuildCacheAsync(entry.SourceType, ct);

        // D19: a review-screen whitelist add is a negative (not-fraud) training label.
        // Labels join raw events by session id — without one there is nothing to join,
        // so no label is emitted.
        if (entry.Source == "review_screen")
        {
            if (string.IsNullOrEmpty(entry.SessionId))
            {
                logger.LogWarning(
                    "review_screen whitelist add for {SourceType} without SessionId; no training label emitted.",
                    entry.SourceType);
            }
            else if (labelSink is not null)
            {
                try
                {
                    await labelSink.WriteAsync(new LabelEvent(
                        tenant.TenantId, entry.SessionId,
                        LabelValues.Legit, LabelSources.ReviewScreen, DateTime.UtcNow), ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Whitelist negative-label emission failed; whitelist add succeeded.");
                }
            }
        }

        return id;
    }

    public async Task<bool> RemoveAsync(string sourceType, string value, CancellationToken ct)
    {
        ValidateSourceType(sourceType, nameof(sourceType));

        int rows;
        await using (var conn = await connections.OpenAsync(ct))
        {
            rows = await conn.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM dbo.WhitelistEntries
                WHERE TenantId = @TenantId AND SourceType = @SourceType AND Value = @Value;
                """,
                new { TenantId = tenant.TenantId.Value, SourceType = sourceType, Value = value },
                cancellationToken: ct));
        }

        await RebuildCacheAsync(sourceType, ct);
        return rows > 0;
    }

    public async Task<WhitelistEntry?> GetByIdAsync(long id, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<WhitelistEntry>(new CommandDefinition(
            """
            SELECT Id, TenantId, SourceType, Value, Reason, Source, CreatedBy, CreatedUtc, ExpiresUtc
            FROM dbo.WhitelistEntries
            WHERE TenantId = @TenantId AND Id = @Id;
            """,
            new { TenantId = tenant.TenantId.Value, Id = id },
            cancellationToken: ct));
    }

    public async Task<bool> RemoveByIdAsync(long id, CancellationToken ct)
    {
        string? sourceType;
        int rows;
        await using (var conn = await connections.OpenAsync(ct))
        {
            // Fetch SourceType first so the right cache set is rebuilt after the delete.
            sourceType = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT SourceType FROM dbo.WhitelistEntries WHERE TenantId = @TenantId AND Id = @Id;",
                new { TenantId = tenant.TenantId.Value, Id = id },
                cancellationToken: ct));
            if (sourceType is null)
            {
                return false; // nonexistent (or another tenant's — RLS): touch no cache.
            }

            rows = await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.WhitelistEntries WHERE TenantId = @TenantId AND Id = @Id;",
                new { TenantId = tenant.TenantId.Value, Id = id },
                cancellationToken: ct));
        }

        if (rows == 0)
        {
            return false; // deleted concurrently between the SELECT and the DELETE.
        }

        await RebuildCacheAsync(sourceType, ct);
        return true;
    }

    public async Task<IReadOnlyList<WhitelistEntry>> ListAsync(
        string? sourceType, int offset, int limit, CancellationToken ct)
    {
        if (sourceType is not null)
        {
            ValidateSourceType(sourceType, nameof(sourceType));
        }

        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "offset must be >= 0.");
        }

        if (limit is not (> 0 and <= 200))
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "limit must be between 1 and 200.");
        }

        await using var conn = await connections.OpenAsync(ct);
        var rows = await conn.QueryAsync<WhitelistEntry>(new CommandDefinition(
            """
            SELECT Id, TenantId, SourceType, Value, Reason, Source, CreatedBy, CreatedUtc, ExpiresUtc
            FROM dbo.WhitelistEntries
            WHERE TenantId = @TenantId AND (@SourceType IS NULL OR SourceType = @SourceType)
            ORDER BY CreatedUtc DESC, Id DESC
            OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY;
            """,
            new
            {
                TenantId = tenant.TenantId.Value,
                SourceType = sourceType,
                Offset = offset,
                Limit = limit,
            },
            cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyDictionary<string, bool>> AreWhitelistedAsync(
        string sourceType, IReadOnlyCollection<string> values, CancellationToken ct)
    {
        ValidateSourceType(sourceType, nameof(sourceType));
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return new Dictionary<string, bool>(StringComparer.Ordinal);
        }

        await using var conn = await connections.OpenAsync(ct);
        var whitelisted = (await conn.QueryAsync<string>(new CommandDefinition(
            """
            SELECT Value FROM dbo.WhitelistEntries
            WHERE TenantId = @TenantId AND SourceType = @SourceType AND Value IN @Values
              AND (ExpiresUtc IS NULL OR ExpiresUtc > SYSUTCDATETIME());
            """,
            new { TenantId = tenant.TenantId.Value, SourceType = sourceType, Values = values },
            cancellationToken: ct))).ToHashSet(StringComparer.Ordinal);

        var result = new Dictionary<string, bool>(values.Count, StringComparer.Ordinal);
        foreach (var value in values)
        {
            result[value] = whitelisted.Contains(value);
        }

        return result;
    }

    public async Task RebuildCacheAsync(string sourceType, CancellationToken ct)
    {
        ValidateSourceType(sourceType, nameof(sourceType));

        List<string> values;
        await using (var conn = await connections.OpenAsync(ct))
        {
            values = (await conn.QueryAsync<string>(new CommandDefinition(
                """
                SELECT Value FROM dbo.WhitelistEntries
                WHERE TenantId = @TenantId AND SourceType = @SourceType
                  AND (ExpiresUtc IS NULL OR ExpiresUtc > SYSUTCDATETIME());
                """,
                new { TenantId = tenant.TenantId.Value, SourceType = sourceType },
                cancellationToken: ct))).AsList();
        }

        // Cache failures must not fail the SQL write — SQL remains the source of truth;
        // a missing/stale key just means RSK-07 treats the request as not whitelisted
        // and schedules another rebuild.
        var key = $"t:{tenant.TenantId.Value:D}:wl:{sourceType}";
        try
        {
            var db = redis.GetDatabase();
            var tran = db.CreateTransaction();
            _ = tran.KeyDeleteAsync(key);
            if (values.Count > 0)
            {
                _ = tran.SetAddAsync(key, values.Select(v => (RedisValue)v).ToArray());
            }

            _ = tran.KeyExpireAsync(key, TimeSpan.FromHours(1));
            await tran.ExecuteAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to rebuild whitelist cache set {Key}; SQL write succeeded and remains the source of truth.",
                key);
        }
    }

    private static void ValidateSourceType(string sourceType, string paramName)
    {
        if (!AllowedSourceTypes.Contains(sourceType, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "SourceType must be one of: ip, device_id, fingerprint.", paramName);
        }
    }
}
