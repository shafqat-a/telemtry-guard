using Dapper;
using TelemetryGuard.Core.Tenancy;

namespace TelemetryGuard.Data.Repositories;

public sealed record MarketIqOutboxInsert(Guid DeliveryId, string SiteKey, string EventId,
    string DestinationUrl, string PayloadJson, DateTime CreatedUtc);
public sealed record MarketIqOutboxItem(Guid TenantId, Guid DeliveryId, string SiteKey,
    string EventId, string DestinationUrl, string PayloadJson, int AttemptCount);

public interface IMarketIqOutboxRepository
{
    Task<bool> EnqueueAsync(MarketIqOutboxInsert item, CancellationToken ct);
    Task<IReadOnlyList<MarketIqOutboxItem>> ClaimDueAsync(int limit, DateTime nowUtc, CancellationToken ct);
    Task CompleteAsync(Guid tenantId, Guid deliveryId, CancellationToken ct);
    Task FailAsync(Guid tenantId, Guid deliveryId, string error, DateTime nextAttemptUtc, bool dead, CancellationToken ct);
}

internal sealed class MarketIqOutboxRepository(
    ITenantConnectionFactory tenantConnections,
    ISystemConnectionFactory systemConnections,
    ITenantContext tenant) : IMarketIqOutboxRepository
{
    public async Task<bool> EnqueueAsync(MarketIqOutboxInsert item, CancellationToken ct)
    {
        await using var conn = await tenantConnections.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.MarketIqOutbox WITH (UPDLOCK,HOLDLOCK)
                           WHERE TenantId=@TenantId AND SiteKey=@SiteKey AND EventId=@EventId)
            BEGIN
              INSERT dbo.MarketIqOutbox
                (TenantId,DeliveryId,SiteKey,EventId,DestinationUrl,PayloadJson,NextAttemptUtc,CreatedUtc)
              VALUES (@TenantId,@DeliveryId,@SiteKey,@EventId,@DestinationUrl,@PayloadJson,@CreatedUtc,@CreatedUtc);
              SELECT 1;
            END
            ELSE SELECT 0;
            """, new { TenantId=tenant.TenantId.Value, item.DeliveryId, item.SiteKey, item.EventId,
                item.DestinationUrl, item.PayloadJson, item.CreatedUtc }, cancellationToken:ct)) == 1;
    }

    public async Task<IReadOnlyList<MarketIqOutboxItem>> ClaimDueAsync(int limit, DateTime nowUtc, CancellationToken ct)
    {
        await using var conn = await systemConnections.OpenSystemAsync(ct);
        var rows = await conn.QueryAsync<MarketIqOutboxItem>(new CommandDefinition(
            """
            UPDATE picked SET Status=1,AttemptCount=AttemptCount+1,NextAttemptUtc=DATEADD(minute,5,@NowUtc)
            OUTPUT inserted.TenantId,inserted.DeliveryId,inserted.SiteKey,inserted.EventId,
                   inserted.DestinationUrl,inserted.PayloadJson,inserted.AttemptCount
            FROM (SELECT TOP (@Limit) * FROM dbo.MarketIqOutbox WITH (UPDLOCK,READPAST,ROWLOCK)
                  WHERE Status IN (0,1) AND NextAttemptUtc<=@NowUtc ORDER BY NextAttemptUtc) picked;
            """, new { Limit=limit,NowUtc=nowUtc }, cancellationToken:ct));
        return rows.AsList();
    }

    public Task CompleteAsync(Guid tenantId, Guid deliveryId, CancellationToken ct) => UpdateAsync(
        "UPDATE dbo.MarketIqOutbox SET Status=2,DeliveredUtc=SYSUTCDATETIME(),LastError=NULL WHERE TenantId=@TenantId AND DeliveryId=@DeliveryId;",
        tenantId,deliveryId,null,null,2,ct);

    public Task FailAsync(Guid tenantId, Guid deliveryId, string error, DateTime nextAttemptUtc, bool dead, CancellationToken ct) => UpdateAsync(
        "UPDATE dbo.MarketIqOutbox SET Status=@Status,NextAttemptUtc=@NextAttemptUtc,LastError=@Error WHERE TenantId=@TenantId AND DeliveryId=@DeliveryId;",
        tenantId,deliveryId,error,nextAttemptUtc,dead?(byte)3:(byte)0,ct);

    private async Task UpdateAsync(string sql, Guid tenantId, Guid deliveryId, string? error,
        DateTime? nextAttemptUtc, byte status, CancellationToken ct)
    {
        await using var conn=await systemConnections.OpenSystemAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql,new {TenantId=tenantId,DeliveryId=deliveryId,
            Error=error,NextAttemptUtc=nextAttemptUtc,Status=status},cancellationToken:ct));
    }
}
