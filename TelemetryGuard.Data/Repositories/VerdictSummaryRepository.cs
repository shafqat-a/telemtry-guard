using Dapper;
using TelemetryGuard.Core.Analytics;
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
                ScoreSum = @ScoreSum, Events = @Events,
                ScoreBucket00 = @ScoreBucket00, ScoreBucket10 = @ScoreBucket10, ScoreBucket20 = @ScoreBucket20,
                ScoreBucket30 = @ScoreBucket30, ScoreBucket40 = @ScoreBucket40, ScoreBucket50 = @ScoreBucket50,
                ScoreBucket60 = @ScoreBucket60, ScoreBucket70 = @ScoreBucket70, ScoreBucket80 = @ScoreBucket80,
                ScoreBucket90 = @ScoreBucket90, ScoreBucket100 = @ScoreBucket100, ScoreSumSq = @ScoreSumSq,
                UpdatedUtc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT
                (TenantId, CampaignId, [Date], Allowed, Challenged, Blocked, ScoreSum, Events,
                 ScoreBucket00, ScoreBucket10, ScoreBucket20, ScoreBucket30, ScoreBucket40, ScoreBucket50,
                 ScoreBucket60, ScoreBucket70, ScoreBucket80, ScoreBucket90, ScoreBucket100, ScoreSumSq)
                VALUES (@TenantId, @CampaignId, @Date, @Allowed, @Challenged, @Blocked, @ScoreSum, @Events,
                 @ScoreBucket00, @ScoreBucket10, @ScoreBucket20, @ScoreBucket30, @ScoreBucket40, @ScoreBucket50,
                 @ScoreBucket60, @ScoreBucket70, @ScoreBucket80, @ScoreBucket90, @ScoreBucket100, @ScoreSumSq);
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
                ScoreBucket00 = row.ScoreHistogram.Bucket00,
                ScoreBucket10 = row.ScoreHistogram.Bucket10,
                ScoreBucket20 = row.ScoreHistogram.Bucket20,
                ScoreBucket30 = row.ScoreHistogram.Bucket30,
                ScoreBucket40 = row.ScoreHistogram.Bucket40,
                ScoreBucket50 = row.ScoreHistogram.Bucket50,
                ScoreBucket60 = row.ScoreHistogram.Bucket60,
                ScoreBucket70 = row.ScoreHistogram.Bucket70,
                ScoreBucket80 = row.ScoreHistogram.Bucket80,
                ScoreBucket90 = row.ScoreHistogram.Bucket90,
                ScoreBucket100 = row.ScoreHistogram.Bucket100,
                ScoreSumSq = row.ScoreHistogram.SumSq,
            },
            cancellationToken: ct));
    }

    /// <summary>Live-path per-verdict increment (API-06). See the interface doc for why
    /// this is a separate method from the rollup's absolute-value upsert. Histogram
    /// columns increment the same way Allowed/Challenged/Blocked already do — a live
    /// verdict's histogram delta is always a single 1 in one bucket (VerdictFinalizer
    /// uses ScoreHistogramMath.SingleScore).</summary>
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
                Events = t.Events + @Events,
                ScoreBucket00 = t.ScoreBucket00 + @ScoreBucket00, ScoreBucket10 = t.ScoreBucket10 + @ScoreBucket10,
                ScoreBucket20 = t.ScoreBucket20 + @ScoreBucket20, ScoreBucket30 = t.ScoreBucket30 + @ScoreBucket30,
                ScoreBucket40 = t.ScoreBucket40 + @ScoreBucket40, ScoreBucket50 = t.ScoreBucket50 + @ScoreBucket50,
                ScoreBucket60 = t.ScoreBucket60 + @ScoreBucket60, ScoreBucket70 = t.ScoreBucket70 + @ScoreBucket70,
                ScoreBucket80 = t.ScoreBucket80 + @ScoreBucket80, ScoreBucket90 = t.ScoreBucket90 + @ScoreBucket90,
                ScoreBucket100 = t.ScoreBucket100 + @ScoreBucket100, ScoreSumSq = t.ScoreSumSq + @ScoreSumSq,
                UpdatedUtc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT
                (TenantId, CampaignId, [Date], Allowed, Challenged, Blocked, ScoreSum, Events,
                 ScoreBucket00, ScoreBucket10, ScoreBucket20, ScoreBucket30, ScoreBucket40, ScoreBucket50,
                 ScoreBucket60, ScoreBucket70, ScoreBucket80, ScoreBucket90, ScoreBucket100, ScoreSumSq)
                VALUES (@TenantId, @CampaignId, @Date, @Allowed, @Challenged, @Blocked, @ScoreSum, @Events,
                 @ScoreBucket00, @ScoreBucket10, @ScoreBucket20, @ScoreBucket30, @ScoreBucket40, @ScoreBucket50,
                 @ScoreBucket60, @ScoreBucket70, @ScoreBucket80, @ScoreBucket90, @ScoreBucket100, @ScoreSumSq);
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
                ScoreBucket00 = delta.ScoreHistogram.Bucket00,
                ScoreBucket10 = delta.ScoreHistogram.Bucket10,
                ScoreBucket20 = delta.ScoreHistogram.Bucket20,
                ScoreBucket30 = delta.ScoreHistogram.Bucket30,
                ScoreBucket40 = delta.ScoreHistogram.Bucket40,
                ScoreBucket50 = delta.ScoreHistogram.Bucket50,
                ScoreBucket60 = delta.ScoreHistogram.Bucket60,
                ScoreBucket70 = delta.ScoreHistogram.Bucket70,
                ScoreBucket80 = delta.ScoreHistogram.Bucket80,
                ScoreBucket90 = delta.ScoreHistogram.Bucket90,
                ScoreBucket100 = delta.ScoreHistogram.Bucket100,
                ScoreSumSq = delta.ScoreHistogram.SumSq,
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
                ScoreSum = @ScoreSum,
                ScoreBucket00 = @ScoreBucket00, ScoreBucket10 = @ScoreBucket10, ScoreBucket20 = @ScoreBucket20,
                ScoreBucket30 = @ScoreBucket30, ScoreBucket40 = @ScoreBucket40, ScoreBucket50 = @ScoreBucket50,
                ScoreBucket60 = @ScoreBucket60, ScoreBucket70 = @ScoreBucket70, ScoreBucket80 = @ScoreBucket80,
                ScoreBucket90 = @ScoreBucket90, ScoreBucket100 = @ScoreBucket100, ScoreSumSq = @ScoreSumSq,
                UpdatedUtc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT
                (TenantId, [Date], SourceType, Value, FlaggedCount, BlockedCount, ScoreSum,
                 ScoreBucket00, ScoreBucket10, ScoreBucket20, ScoreBucket30, ScoreBucket40, ScoreBucket50,
                 ScoreBucket60, ScoreBucket70, ScoreBucket80, ScoreBucket90, ScoreBucket100, ScoreSumSq)
                VALUES (@TenantId, @Date, @SourceType, @Value, @FlaggedCount, @BlockedCount, @ScoreSum,
                 @ScoreBucket00, @ScoreBucket10, @ScoreBucket20, @ScoreBucket30, @ScoreBucket40, @ScoreBucket50,
                 @ScoreBucket60, @ScoreBucket70, @ScoreBucket80, @ScoreBucket90, @ScoreBucket100, @ScoreSumSq);
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
                ScoreBucket00 = row.ScoreHistogram.Bucket00,
                ScoreBucket10 = row.ScoreHistogram.Bucket10,
                ScoreBucket20 = row.ScoreHistogram.Bucket20,
                ScoreBucket30 = row.ScoreHistogram.Bucket30,
                ScoreBucket40 = row.ScoreHistogram.Bucket40,
                ScoreBucket50 = row.ScoreHistogram.Bucket50,
                ScoreBucket60 = row.ScoreHistogram.Bucket60,
                ScoreBucket70 = row.ScoreHistogram.Bucket70,
                ScoreBucket80 = row.ScoreHistogram.Bucket80,
                ScoreBucket90 = row.ScoreHistogram.Bucket90,
                ScoreBucket100 = row.ScoreHistogram.Bucket100,
                ScoreSumSq = row.ScoreHistogram.SumSq,
            },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<VerdictDailySummaryRow>> GetDailySummariesAsync(
        Guid campaignId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        var rows = await conn.QueryAsync<VerdictDailySummaryDbRow>(new CommandDefinition(
            """
            SELECT TenantId, CampaignId, [Date], Allowed, Challenged, Blocked, ScoreSum, Events,
                   ScoreBucket00, ScoreBucket10, ScoreBucket20, ScoreBucket30, ScoreBucket40, ScoreBucket50,
                   ScoreBucket60, ScoreBucket70, ScoreBucket80, ScoreBucket90, ScoreBucket100, ScoreSumSq
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

    /// <summary>REQ-03: tenant-wide daily summary. SUMs every campaign's (plus
    /// campaign-less, CampaignId = Guid.Empty) row per date — the CampaignId
    /// dimension is collapsed away, so this is a GROUP BY [Date], unlike the
    /// single-campaign read above which never needs to aggregate (the table's PK
    /// already guarantees at most one row per campaign/date).</summary>
    public async Task<IReadOnlyList<VerdictDailySummaryRow>> GetTenantDailySummariesAsync(
        DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        var rows = await conn.QueryAsync<TenantDailySummaryDbRow>(new CommandDefinition(
            """
            SELECT [Date],
                   SUM(Allowed)    AS Allowed,    SUM(Challenged) AS Challenged, SUM(Blocked) AS Blocked,
                   SUM(ScoreSum)   AS ScoreSum,   SUM(Events)     AS Events,
                   SUM(ScoreBucket00)  AS ScoreBucket00,  SUM(ScoreBucket10)  AS ScoreBucket10,
                   SUM(ScoreBucket20)  AS ScoreBucket20,  SUM(ScoreBucket30)  AS ScoreBucket30,
                   SUM(ScoreBucket40)  AS ScoreBucket40,  SUM(ScoreBucket50)  AS ScoreBucket50,
                   SUM(ScoreBucket60)  AS ScoreBucket60,  SUM(ScoreBucket70)  AS ScoreBucket70,
                   SUM(ScoreBucket80)  AS ScoreBucket80,  SUM(ScoreBucket90)  AS ScoreBucket90,
                   SUM(ScoreBucket100) AS ScoreBucket100, SUM(ScoreSumSq)     AS ScoreSumSq
            FROM dbo.VerdictDailySummaries
            WHERE TenantId = @TenantId AND [Date] BETWEEN @From AND @To
            GROUP BY [Date]
            ORDER BY [Date];
            """,
            new
            {
                TenantId = tenant.TenantId.Value,
                From = from.ToDateTime(TimeOnly.MinValue),
                To = to.ToDateTime(TimeOnly.MinValue),
            },
            cancellationToken: ct));
        return rows.Select(r => r.ToRecord(tenant.TenantId.Value)).ToList();
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
            SELECT TOP (@Limit) TenantId, [Date], SourceType, Value, FlaggedCount, BlockedCount, ScoreSum,
                   ScoreBucket00, ScoreBucket10, ScoreBucket20, ScoreBucket30, ScoreBucket40, ScoreBucket50,
                   ScoreBucket60, ScoreBucket70, ScoreBucket80, ScoreBucket90, ScoreBucket100, ScoreSumSq
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
        public int ScoreBucket00 { get; init; }
        public int ScoreBucket10 { get; init; }
        public int ScoreBucket20 { get; init; }
        public int ScoreBucket30 { get; init; }
        public int ScoreBucket40 { get; init; }
        public int ScoreBucket50 { get; init; }
        public int ScoreBucket60 { get; init; }
        public int ScoreBucket70 { get; init; }
        public int ScoreBucket80 { get; init; }
        public int ScoreBucket90 { get; init; }
        public int ScoreBucket100 { get; init; }
        public long ScoreSumSq { get; init; }

        public VerdictDailySummaryRow ToRecord() => new(
            TenantId, CampaignId, DateOnly.FromDateTime(Date),
            Allowed, Challenged, Blocked, ScoreSum, Events,
            new ScoreHistogramCounts(
                ScoreBucket00, ScoreBucket10, ScoreBucket20, ScoreBucket30, ScoreBucket40, ScoreBucket50,
                ScoreBucket60, ScoreBucket70, ScoreBucket80, ScoreBucket90, ScoreBucket100, ScoreSumSq));
    }

    /// <summary>Materialization row for the tenant-wide GROUP BY [Date] read
    /// (REQ-03) — no CampaignId column (it was summed away).</summary>
    private sealed class TenantDailySummaryDbRow
    {
        public DateTime Date { get; init; }
        public int Allowed { get; init; }
        public int Challenged { get; init; }
        public int Blocked { get; init; }
        public long ScoreSum { get; init; }
        public int Events { get; init; }
        public int ScoreBucket00 { get; init; }
        public int ScoreBucket10 { get; init; }
        public int ScoreBucket20 { get; init; }
        public int ScoreBucket30 { get; init; }
        public int ScoreBucket40 { get; init; }
        public int ScoreBucket50 { get; init; }
        public int ScoreBucket60 { get; init; }
        public int ScoreBucket70 { get; init; }
        public int ScoreBucket80 { get; init; }
        public int ScoreBucket90 { get; init; }
        public int ScoreBucket100 { get; init; }
        public long ScoreSumSq { get; init; }

        /// <summary>CampaignId is a Guid.Empty sentinel — see the interface doc.</summary>
        public VerdictDailySummaryRow ToRecord(Guid tenantId) => new(
            tenantId, Guid.Empty, DateOnly.FromDateTime(Date),
            Allowed, Challenged, Blocked, ScoreSum, Events,
            new ScoreHistogramCounts(
                ScoreBucket00, ScoreBucket10, ScoreBucket20, ScoreBucket30, ScoreBucket40, ScoreBucket50,
                ScoreBucket60, ScoreBucket70, ScoreBucket80, ScoreBucket90, ScoreBucket100, ScoreSumSq));
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
        public int ScoreBucket00 { get; init; }
        public int ScoreBucket10 { get; init; }
        public int ScoreBucket20 { get; init; }
        public int ScoreBucket30 { get; init; }
        public int ScoreBucket40 { get; init; }
        public int ScoreBucket50 { get; init; }
        public int ScoreBucket60 { get; init; }
        public int ScoreBucket70 { get; init; }
        public int ScoreBucket80 { get; init; }
        public int ScoreBucket90 { get; init; }
        public int ScoreBucket100 { get; init; }
        public long ScoreSumSq { get; init; }

        public FlaggedSourceDailyRow ToRecord() => new(
            TenantId, DateOnly.FromDateTime(Date), SourceType, Value,
            FlaggedCount, BlockedCount, ScoreSum,
            new ScoreHistogramCounts(
                ScoreBucket00, ScoreBucket10, ScoreBucket20, ScoreBucket30, ScoreBucket40, ScoreBucket50,
                ScoreBucket60, ScoreBucket70, ScoreBucket80, ScoreBucket90, ScoreBucket100, ScoreSumSq));
    }
}
