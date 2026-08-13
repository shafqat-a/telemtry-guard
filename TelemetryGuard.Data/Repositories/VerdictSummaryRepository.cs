using Dapper;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Data.Models;

namespace TelemetryGuard.Data.Repositories;

/// <summary>
/// Dapper repository for the D23 aggregate tables dbo.VerdictDailySummaries and
/// dbo.FlaggedSourcesDaily. Connections come exclusively from
/// <see cref="ITenantConnectionFactory"/> (RLS-scoped); every statement additionally
/// carries an explicit TenantId = @TenantId predicate for index seeks (D11).
/// Upserts are idempotent absolute-value MERGEs — never increments — so ANA-07
/// rollup re-runs converge instead of double-counting.
/// </summary>
internal sealed class VerdictSummaryRepository(ITenantConnectionFactory connections, ITenantContext tenant)
    : IVerdictSummaryRepository
{
    private static readonly string[] AllowedSourceTypes = ["ip", "placement", "device_id", "fingerprint"];

    public async Task UpsertDailySummaryAsync(VerdictDailySummaryRow row, CancellationToken ct)
    {
        // Throw a clear error on ambient-tenant mismatch rather than relying on the
        // RLS BLOCK predicate's opaque SqlException.
        if (row.TenantId != tenant.TenantId.Value)
        {
            throw new InvalidOperationException(
                "VerdictDailySummaryRow.TenantId does not match the ambient tenant; refusing to upsert.");
        }

        await using var conn = await connections.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            MERGE dbo.VerdictDailySummaries WITH (HOLDLOCK) AS t
            USING (SELECT @TenantId AS TenantId, @CampaignId AS CampaignId, @Date AS [Date]) AS s
                ON t.TenantId = s.TenantId AND t.CampaignId = s.CampaignId AND t.[Date] = s.[Date]
            WHEN MATCHED THEN UPDATE SET
                Allowed = @Allowed, Challenged = @Challenged, Blocked = @Blocked,
                ScoreSum = @ScoreSum, Events = @Events, UpdatedUtc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT
                (TenantId, CampaignId, [Date], Allowed, Challenged, Blocked, ScoreSum, Events)
                VALUES (@TenantId, @CampaignId, @Date, @Allowed, @Challenged, @Blocked, @ScoreSum, @Events);
            """,
            new
            {
                TenantId = tenant.TenantId.Value,
                row.CampaignId,
                Date = row.Date.ToDateTime(TimeOnly.MinValue),
                row.Allowed,
                row.Challenged,
                row.Blocked,
                row.ScoreSum,
                row.Events,
            },
            cancellationToken: ct));
    }

    /// <summary>Live-path per-verdict increment (API-06). See the interface doc for why
    /// this is a separate method from the rollup's absolute-value upsert.</summary>
    public async Task IncrementDailySummaryAsync(VerdictDailySummaryRow delta, CancellationToken ct)
    {
        if (delta.TenantId != tenant.TenantId.Value)
        {
            throw new InvalidOperationException(
                "VerdictDailySummaryRow.TenantId does not match the ambient tenant; refusing to increment.");
        }

        await using var conn = await connections.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            MERGE dbo.VerdictDailySummaries WITH (HOLDLOCK) AS t
            USING (SELECT @TenantId AS TenantId, @CampaignId AS CampaignId, @Date AS [Date]) AS s
                ON t.TenantId = s.TenantId AND t.CampaignId = s.CampaignId AND t.[Date] = s.[Date]
            WHEN MATCHED THEN UPDATE SET
                Allowed = t.Allowed + @Allowed, Challenged = t.Challenged + @Challenged,
                Blocked = t.Blocked + @Blocked, ScoreSum = t.ScoreSum + @ScoreSum,
                Events = t.Events + @Events, UpdatedUtc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT
                (TenantId, CampaignId, [Date], Allowed, Challenged, Blocked, ScoreSum, Events)
                VALUES (@TenantId, @CampaignId, @Date, @Allowed, @Challenged, @Blocked, @ScoreSum, @Events);
            """,
            new
            {
                TenantId = tenant.TenantId.Value,
                delta.CampaignId,
                Date = delta.Date.ToDateTime(TimeOnly.MinValue),
                delta.Allowed,
                delta.Challenged,
                delta.Blocked,
                delta.ScoreSum,
                delta.Events,
            },
            cancellationToken: ct));
    }

    public async Task UpsertFlaggedSourceAsync(FlaggedSourceDailyRow row, CancellationToken ct)
    {
        if (row.TenantId != tenant.TenantId.Value)
        {
            throw new InvalidOperationException(
                "FlaggedSourceDailyRow.TenantId does not match the ambient tenant; refusing to upsert.");
        }

        if (!AllowedSourceTypes.Contains(row.SourceType, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "SourceType must be one of: ip, placement, device_id, fingerprint.", nameof(row));
        }

        await using var conn = await connections.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            MERGE dbo.FlaggedSourcesDaily WITH (HOLDLOCK) AS t
            USING (SELECT @TenantId AS TenantId, @Date AS [Date], @SourceType AS SourceType, @Value AS Value) AS s
                ON t.TenantId = s.TenantId AND t.[Date] = s.[Date]
               AND t.SourceType = s.SourceType AND t.Value = s.Value
            WHEN MATCHED THEN UPDATE SET
                FlaggedCount = @FlaggedCount, BlockedCount = @BlockedCount,
                ScoreSum = @ScoreSum, UpdatedUtc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT
                (TenantId, [Date], SourceType, Value, FlaggedCount, BlockedCount, ScoreSum)
                VALUES (@TenantId, @Date, @SourceType, @Value, @FlaggedCount, @BlockedCount, @ScoreSum);
            """,
            new
            {
                TenantId = tenant.TenantId.Value,
                Date = row.Date.ToDateTime(TimeOnly.MinValue),
                row.SourceType,
                row.Value,
                row.FlaggedCount,
                row.BlockedCount,
                row.ScoreSum,
            },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<VerdictDailySummaryRow>> GetDailySummariesAsync(
        Guid campaignId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        var rows = await conn.QueryAsync<VerdictDailySummaryDbRow>(new CommandDefinition(
            """
            SELECT TenantId, CampaignId, [Date], Allowed, Challenged, Blocked, ScoreSum, Events
            FROM dbo.VerdictDailySummaries
            WHERE TenantId = @TenantId AND CampaignId = @CampaignId AND [Date] BETWEEN @From AND @To
            ORDER BY [Date];
            """,
            new
            {
                TenantId = tenant.TenantId.Value,
                CampaignId = campaignId,
                From = from.ToDateTime(TimeOnly.MinValue),
                To = to.ToDateTime(TimeOnly.MinValue),
            },
            cancellationToken: ct));
        return rows.Select(r => r.ToRecord()).ToList();
    }

    public async Task<IReadOnlyList<FlaggedSourceDailyRow>> GetTopFlaggedSourcesAsync(
        DateOnly from, DateOnly to, int limit, CancellationToken ct)
    {
        if (limit is not (> 0 and <= 1000))
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit,
                "limit must be between 1 and 1000.");
        }

        await using var conn = await connections.OpenAsync(ct);
        var rows = await conn.QueryAsync<FlaggedSourceDailyDbRow>(new CommandDefinition(
            """
            SELECT TOP (@Limit) TenantId, [Date], SourceType, Value, FlaggedCount, BlockedCount, ScoreSum
            FROM dbo.FlaggedSourcesDaily
            WHERE TenantId = @TenantId AND [Date] BETWEEN @From AND @To
            ORDER BY BlockedCount DESC, FlaggedCount DESC;
            """,
            new
            {
                Limit = limit,
                TenantId = tenant.TenantId.Value,
                From = from.ToDateTime(TimeOnly.MinValue),
                To = to.ToDateTime(TimeOnly.MinValue),
            },
            cancellationToken: ct));
        return rows.Select(r => r.ToRecord()).ToList();
    }

    // Private materialization rows: SQL Server `date` comes back as DateTime; mapping
    // through DateTime avoids Dapper/SqlClient DateOnly version pitfalls.
    private sealed class VerdictDailySummaryDbRow
    {
        public Guid TenantId { get; init; }
        public Guid CampaignId { get; init; }
        public DateTime Date { get; init; }
        public int Allowed { get; init; }
        public int Challenged { get; init; }
        public int Blocked { get; init; }
        public long ScoreSum { get; init; }
        public int Events { get; init; }

        public VerdictDailySummaryRow ToRecord() => new(
            TenantId, CampaignId, DateOnly.FromDateTime(Date),
            Allowed, Challenged, Blocked, ScoreSum, Events);
    }

    private sealed class FlaggedSourceDailyDbRow
    {
        public Guid TenantId { get; init; }
        public DateTime Date { get; init; }
        public string SourceType { get; init; } = string.Empty;
        public string Value { get; init; } = string.Empty;
        public int FlaggedCount { get; init; }
        public int BlockedCount { get; init; }
        public long ScoreSum { get; init; }

        public FlaggedSourceDailyRow ToRecord() => new(
            TenantId, DateOnly.FromDateTime(Date), SourceType, Value,
            FlaggedCount, BlockedCount, ScoreSum);
    }
}
