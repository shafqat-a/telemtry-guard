using Dapper;
using TelemetryGuard.Core.Tenancy;

namespace TelemetryGuard.Data.Repositories;

public interface IWebhookOutboxRepository
{
    Task EnqueueAsync(WebhookOutboxInsert item, CancellationToken ct);
    Task<IReadOnlyList<WebhookOutboxItem>> ClaimDueAsync(int limit, DateTime nowUtc, CancellationToken ct);
    Task CompleteAsync(Guid deliveryId, CancellationToken ct);
    Task FailAsync(Guid deliveryId, string error, DateTime nextAttemptUtc, bool deadLetter, CancellationToken ct);
}

public sealed record WebhookOutboxInsert(Guid DeliveryId, string EventType, string DestinationUrl,
    string SecretRef, string PayloadJson, DateTime CreatedUtc);
public sealed record WebhookOutboxItem(Guid DeliveryId, string EventType, string DestinationUrl,
    string SecretRef, string PayloadJson, int AttemptCount, DateTime CreatedUtc);

internal sealed class WebhookOutboxRepository(ITenantConnectionFactory connections, ITenantContext tenant)
    : IWebhookOutboxRepository
{
    public async Task EnqueueAsync(WebhookOutboxInsert item, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.WebhookOutbox WITH (UPDLOCK,HOLDLOCK)
                           WHERE TenantId=@TenantId AND DeliveryId=@DeliveryId)
              INSERT dbo.WebhookOutbox
                (TenantId,DeliveryId,EventType,DestinationUrl,SecretRef,PayloadJson,NextAttemptUtc,CreatedUtc)
              VALUES (@TenantId,@DeliveryId,@EventType,@DestinationUrl,@SecretRef,@PayloadJson,@CreatedUtc,@CreatedUtc);
            """,
            new { TenantId = tenant.TenantId.Value, item.DeliveryId, item.EventType, item.DestinationUrl,
                item.SecretRef, item.PayloadJson, item.CreatedUtc }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<WebhookOutboxItem>> ClaimDueAsync(int limit, DateTime nowUtc, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        var rows = await conn.QueryAsync<WebhookOutboxItem>(new CommandDefinition(
            """
            UPDATE picked SET Status=1, AttemptCount=AttemptCount+1,
                              NextAttemptUtc=DATEADD(minute,5,@NowUtc)
            OUTPUT inserted.DeliveryId,inserted.EventType,inserted.DestinationUrl,inserted.SecretRef,
                   inserted.PayloadJson,inserted.AttemptCount,inserted.CreatedUtc
            FROM (SELECT TOP (@Limit) * FROM dbo.WebhookOutbox WITH (UPDLOCK,READPAST,ROWLOCK)
                  WHERE TenantId=@TenantId AND Status IN (0,1) AND NextAttemptUtc<=@NowUtc
                  ORDER BY NextAttemptUtc) picked;
            """,
            new { TenantId = tenant.TenantId.Value, Limit = limit, NowUtc = nowUtc }, cancellationToken: ct));
        return rows.AsList();
    }

    public Task CompleteAsync(Guid deliveryId, CancellationToken ct) => UpdateAsync(
        "UPDATE dbo.WebhookOutbox SET Status=2,DeliveredUtc=SYSUTCDATETIME(),LastError=NULL WHERE TenantId=@TenantId AND DeliveryId=@DeliveryId;",
        deliveryId, null, null, ct);

    public Task FailAsync(Guid deliveryId, string error, DateTime nextAttemptUtc, bool deadLetter, CancellationToken ct) => UpdateAsync(
        "UPDATE dbo.WebhookOutbox SET Status=@Status,NextAttemptUtc=@NextAttemptUtc,LastError=@Error WHERE TenantId=@TenantId AND DeliveryId=@DeliveryId;",
        deliveryId, error, nextAttemptUtc, ct, deadLetter ? (byte)3 : (byte)0);

    private async Task UpdateAsync(string sql, Guid deliveryId, string? error, DateTime? nextAttemptUtc,
        CancellationToken ct, byte status = 0)
    {
        await using var conn = await connections.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { TenantId = tenant.TenantId.Value, DeliveryId = deliveryId, Error = error,
                NextAttemptUtc = nextAttemptUtc, Status = status }, cancellationToken: ct));
    }
}
