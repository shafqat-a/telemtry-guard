using Dapper;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Data.Models;

namespace TelemetryGuard.Data.Repositories;

/// <summary>
/// Dapper repository for the P2-01 aggregate tables dbo.PublisherDailySummaries
/// and dbo.SiteDailySummaries. Connections come exclusively from
/// <see cref="ITenantConnectionFactory"/> (RLS-scoped); every statement additionally
/// carries an explicit TenantId = @TenantId predicate for index seeks (D11).
/// Upserts are idempotent absolute-value MERGEs — never increments — so ANA-07
/// rollup re-runs converge instead of double-counting.
/// </summary>
internal sealed class PublisherSummaryRepository(ITenantConnectionFactory connections, ITenantContext tenant)
    : IPublisherSummaryRepository
{
    public async Task UpsertPlacementDailyAsync(PublisherDailySummaryRow row, CancellationToken ct)
    {
        if (row.TenantId != tenant.TenantId.Value)
        {
            throw new InvalidOperationException(
                "PublisherDailySummaryRow.TenantId does not match the ambient tenant; refusing to upsert.");
        }

        await using var conn = await connections.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            MERGE dbo.PublisherDailySummaries WITH (HOLDLOCK) AS t
            USING (SELECT @TenantId AS TenantId, @Date AS [Date], @Placement AS Placement) AS s
                ON t.TenantId = s.TenantId AND t.[Date] = s.[Date] AND t.Placement = s.Placement
            WHEN MATCHED THEN UPDATE SET
                Events = @Events, Allowed = @Allowed, Challenged = @Challenged, Blocked = @Blocked,
                ScoreSum = @ScoreSum, NoJsBeaconCount = @NoJsBeaconCount,
                ScoreBucket00 = @ScoreBucket00, ScoreBucket10 = @ScoreBucket10,
                ScoreBucket20 = @ScoreBucket20, ScoreBucket30 = @ScoreBucket30,
                ScoreBucket40 = @ScoreBucket40, ScoreBucket50 = @ScoreBucket50,
                ScoreBucket60 = @ScoreBucket60, ScoreBucket70 = @ScoreBucket70,
                ScoreBucket80 = @ScoreBucket80, ScoreBucket90 = @ScoreBucket90,
                ScoreBucket100 = @ScoreBucket100, ScoreSumSq = @ScoreSumSq,
                UpdatedUtc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT
                (TenantId, [Date], Placement, Events, Allowed, Challenged, Blocked, ScoreSum, NoJsBeaconCount,
                 ScoreBucket00, ScoreBucket10, ScoreBucket20, ScoreBucket30, ScoreBucket40,
                 ScoreBucket50, ScoreBucket60, ScoreBucket70, ScoreBucket80, ScoreBucket90,
                 ScoreBucket100, ScoreSumSq)
                VALUES (@TenantId, @Date, @Placement, @Events, @Allowed, @Challenged, @Blocked, @ScoreSum, @NoJsBeaconCount,
                 @ScoreBucket00, @ScoreBucket10, @ScoreBucket20, @ScoreBucket30, @ScoreBucket40,
                 @ScoreBucket50, @ScoreBucket60, @ScoreBucket70, @ScoreBucket80, @ScoreBucket90,
                 @ScoreBucket100, @ScoreSumSq);
            """,
            WithDistribution(new
            {
                TenantId = tenant.TenantId.Value,
                Date = row.Date.ToDateTime(TimeOnly.MinValue),
                row.Placement,
                row.Events,
                row.Allowed,
                row.Challenged,
                row.Blocked,
                row.ScoreSum,
                row.NoJsBeaconCount,
            }, row.ScoreDistribution),
            cancellationToken: ct));
    }

    public async Task UpsertSiteDailyAsync(SiteDailySummaryRow row, CancellationToken ct)
    {
        if (row.TenantId != tenant.TenantId.Value)
        {
            throw new InvalidOperationException(
                "SiteDailySummaryRow.TenantId does not match the ambient tenant; refusing to upsert.");
        }

        await using var conn = await connections.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            MERGE dbo.SiteDailySummaries WITH (HOLDLOCK) AS t
            USING (SELECT @TenantId AS TenantId, @Date AS [Date], @SiteKey AS SiteKey) AS s
                ON t.TenantId = s.TenantId AND t.[Date] = s.[Date] AND t.SiteKey = s.SiteKey
            WHEN MATCHED THEN UPDATE SET
                TotalEvents = @TotalEvents, Events = @Events, Allowed = @Allowed,
                Challenged = @Challenged, Blocked = @Blocked, ScoreSum = @ScoreSum,
                NoJsBeaconCount = @NoJsBeaconCount,
                ScoreBucket00 = @ScoreBucket00, ScoreBucket10 = @ScoreBucket10,
                ScoreBucket20 = @ScoreBucket20, ScoreBucket30 = @ScoreBucket30,
                ScoreBucket40 = @ScoreBucket40, ScoreBucket50 = @ScoreBucket50,
                ScoreBucket60 = @ScoreBucket60, ScoreBucket70 = @ScoreBucket70,
                ScoreBucket80 = @ScoreBucket80, ScoreBucket90 = @ScoreBucket90,
                ScoreBucket100 = @ScoreBucket100, ScoreSumSq = @ScoreSumSq,
                UpdatedUtc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT
                (TenantId, [Date], SiteKey, TotalEvents, Events, Allowed, Challenged, Blocked, ScoreSum, NoJsBeaconCount,
                 ScoreBucket00, ScoreBucket10, ScoreBucket20, ScoreBucket30, ScoreBucket40,
                 ScoreBucket50, ScoreBucket60, ScoreBucket70, ScoreBucket80, ScoreBucket90,
                 ScoreBucket100, ScoreSumSq)
                VALUES (@TenantId, @Date, @SiteKey, @TotalEvents, @Events, @Allowed, @Challenged, @Blocked, @ScoreSum, @NoJsBeaconCount,
                 @ScoreBucket00, @ScoreBucket10, @ScoreBucket20, @ScoreBucket30, @ScoreBucket40,
                 @ScoreBucket50, @ScoreBucket60, @ScoreBucket70, @ScoreBucket80, @ScoreBucket90,
                 @ScoreBucket100, @ScoreSumSq);
            """,
            WithDistribution(new
            {
                TenantId = tenant.TenantId.Value,
                Date = row.Date.ToDateTime(TimeOnly.MinValue),
                row.SiteKey,
                row.TotalEvents,
                row.Events,
                row.Allowed,
                row.Challenged,
                row.Blocked,
                row.ScoreSum,
                row.NoJsBeaconCount,
            }, row.ScoreDistribution),
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<PlacementRangeTotalsRow>> GetTopPlacementsAsync(
        DateOnly from, DateOnly to, int limit, CancellationToken ct)
    {
        if (limit is not (> 0 and <= 1000))
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit,
                "limit must be between 1 and 1000.");
        }

        await using var conn = await connections.OpenAsync(ct);
        var rows = await conn.QueryAsync<PlacementRangeTotalsDbRow>(new CommandDefinition(
            """
            SELECT TOP (@Limit)
                Placement,
                SUM(Events)          AS Events,
                SUM(Allowed)         AS Allowed,
                SUM(Challenged)      AS Challenged,
                SUM(Blocked)         AS Blocked,
                SUM(ScoreSum)        AS ScoreSum,
                SUM(NoJsBeaconCount) AS NoJsBeaconCount,
                SUM(ScoreBucket00)   AS ScoreBucket00,
                SUM(ScoreBucket10)   AS ScoreBucket10,
                SUM(ScoreBucket20)   AS ScoreBucket20,
                SUM(ScoreBucket30)   AS ScoreBucket30,
                SUM(ScoreBucket40)   AS ScoreBucket40,
                SUM(ScoreBucket50)   AS ScoreBucket50,
                SUM(ScoreBucket60)   AS ScoreBucket60,
                SUM(ScoreBucket70)   AS ScoreBucket70,
                SUM(ScoreBucket80)   AS ScoreBucket80,
                SUM(ScoreBucket90)   AS ScoreBucket90,
                SUM(ScoreBucket100)  AS ScoreBucket100,
                SUM(ScoreSumSq)      AS ScoreSumSq,
                MIN([Date])          AS FirstDay,
                MAX([Date])          AS LastDay
            FROM dbo.PublisherDailySummaries
            WHERE TenantId = @TenantId AND [Date] BETWEEN @From AND @To
            GROUP BY Placement
            ORDER BY SUM(Blocked) DESC, SUM(Challenged) + SUM(Blocked) DESC, Placement ASC;
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

    public async Task<IReadOnlyList<SiteDailySummaryRow>> GetSiteDailyAsync(
        DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        var rows = await conn.QueryAsync<SiteDailySummaryDbRow>(new CommandDefinition(
            """
            SELECT TenantId, [Date], SiteKey, TotalEvents, Events, Allowed, Challenged,
                   Blocked, ScoreSum, NoJsBeaconCount,
                   ScoreBucket00, ScoreBucket10, ScoreBucket20, ScoreBucket30, ScoreBucket40,
                   ScoreBucket50, ScoreBucket60, ScoreBucket70, ScoreBucket80, ScoreBucket90,
                   ScoreBucket100, ScoreSumSq
            FROM dbo.SiteDailySummaries
            WHERE TenantId = @TenantId AND [Date] BETWEEN @From AND @To
            ORDER BY [Date], SiteKey;
            """,
            new
            {
                TenantId = tenant.TenantId.Value,
                From = from.ToDateTime(TimeOnly.MinValue),
                To = to.ToDateTime(TimeOnly.MinValue),
            },
            cancellationToken: ct));
        return rows.Select(r => r.ToRecord()).ToList();
    }

    // Private materialization rows: SQL Server `date` comes back as DateTime; mapping
    // through DateTime avoids Dapper/SqlClient DateOnly version pitfalls.
    private sealed class PlacementRangeTotalsDbRow
    {
        public string Placement { get; init; } = string.Empty;
        public int Events { get; init; }
        public int Allowed { get; init; }
        public int Challenged { get; init; }
        public int Blocked { get; init; }
        public long ScoreSum { get; init; }
        public int NoJsBeaconCount { get; init; }
        public DateTime FirstDay { get; init; }
        public DateTime LastDay { get; init; }

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

        public PlacementRangeTotalsRow ToRecord() => new(
            Placement, Events, Allowed, Challenged, Blocked, ScoreSum, NoJsBeaconCount,
            DateOnly.FromDateTime(FirstDay), DateOnly.FromDateTime(LastDay))
        {
            ScoreDistribution = Distribution(
                ScoreBucket00, ScoreBucket10, ScoreBucket20, ScoreBucket30, ScoreBucket40,
                ScoreBucket50, ScoreBucket60, ScoreBucket70, ScoreBucket80, ScoreBucket90,
                ScoreBucket100, ScoreSumSq),
        };
    }

    private sealed class SiteDailySummaryDbRow
    {
        public Guid TenantId { get; init; }
        public DateTime Date { get; init; }
        public string SiteKey { get; init; } = string.Empty;
        public int TotalEvents { get; init; }
        public int Events { get; init; }
        public int Allowed { get; init; }
        public int Challenged { get; init; }
        public int Blocked { get; init; }
        public long ScoreSum { get; init; }
        public int NoJsBeaconCount { get; init; }

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

        public SiteDailySummaryRow ToRecord() => new(
            TenantId, DateOnly.FromDateTime(Date), SiteKey,
            TotalEvents, Events, Allowed, Challenged, Blocked, ScoreSum, NoJsBeaconCount)
        {
            ScoreDistribution = Distribution(
                ScoreBucket00, ScoreBucket10, ScoreBucket20, ScoreBucket30, ScoreBucket40,
                ScoreBucket50, ScoreBucket60, ScoreBucket70, ScoreBucket80, ScoreBucket90,
                ScoreBucket100, ScoreSumSq),
        };
    }

    private static DynamicParameters WithDistribution(object values, TelemetryGuard.Analytics.Abstractions.ScoreDistribution d)
    {
        var p = new DynamicParameters(values);
        p.Add("ScoreBucket00", d.Bucket00); p.Add("ScoreBucket10", d.Bucket10);
        p.Add("ScoreBucket20", d.Bucket20); p.Add("ScoreBucket30", d.Bucket30);
        p.Add("ScoreBucket40", d.Bucket40); p.Add("ScoreBucket50", d.Bucket50);
        p.Add("ScoreBucket60", d.Bucket60); p.Add("ScoreBucket70", d.Bucket70);
        p.Add("ScoreBucket80", d.Bucket80); p.Add("ScoreBucket90", d.Bucket90);
        p.Add("ScoreBucket100", d.Bucket100); p.Add("ScoreSumSq", d.SumSq);
        return p;
    }

    private static TelemetryGuard.Analytics.Abstractions.ScoreDistribution Distribution(
        long b00, long b10, long b20, long b30, long b40, long b50,
        long b60, long b70, long b80, long b90, long b100, long sumSq) =>
        new(b00, b10, b20, b30, b40, b50, b60, b70, b80, b90, b100, sumSq);
}
