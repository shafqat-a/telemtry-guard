using Dapper;
using TelemetryGuard.Core.Tenancy;

namespace TelemetryGuard.Data.Repositories;

/// <summary>
/// Dapper repository for dbo.RollupWatermarks (ANA-07 rollup progress per tenant and
/// named rollup). Connections come exclusively from
/// <see cref="ITenantConnectionFactory"/> (RLS-scoped); every statement additionally
/// carries an explicit TenantId = @TenantId predicate for index seeks (D11).
/// </summary>
internal sealed class RollupWatermarkRepository(ITenantConnectionFactory connections, ITenantContext tenant)
    : IRollupWatermarkRepository
{
    public async Task<DateTime?> GetAsync(string rollupName, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<DateTime?>(new CommandDefinition(
            """
            SELECT WatermarkUtc FROM dbo.RollupWatermarks
            WHERE TenantId = @TenantId AND RollupName = @RollupName;
            """,
            new { TenantId = tenant.TenantId.Value, RollupName = rollupName },
            cancellationToken: ct));
    }

    public async Task SetAsync(string rollupName, DateTime watermarkUtc, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            MERGE dbo.RollupWatermarks WITH (HOLDLOCK) AS t
            USING (SELECT @TenantId AS TenantId, @RollupName AS RollupName) AS s
                ON t.TenantId = s.TenantId AND t.RollupName = s.RollupName
            WHEN MATCHED THEN UPDATE SET WatermarkUtc = @WatermarkUtc, UpdatedUtc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (TenantId, RollupName, WatermarkUtc)
                VALUES (@TenantId, @RollupName, @WatermarkUtc);
            """,
            new { TenantId = tenant.TenantId.Value, RollupName = rollupName, WatermarkUtc = watermarkUtc },
            cancellationToken: ct));
    }
}
