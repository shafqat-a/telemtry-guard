using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Options;
using TelemetryGuard.Api.Options;
using TelemetryGuard.Api.Services;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Data;
using TelemetryGuard.Data.Repositories;

namespace TelemetryGuard.Api.Workers;

public sealed class WebhookDeliveryService(
    IServiceScopeFactory scopes,
    ISystemConnectionFactory systemConnections,
    IOptions<WebhookOptions> options,
    ILogger<WebhookDeliveryService> logger) : BackgroundService
{
    private static readonly Meter Meter = new("TelemetryGuard.Webhooks");
    private static readonly Counter<long> Delivered = Meter.CreateCounter<long>("tg.webhooks.delivered");
    private static readonly Counter<long> Retried = Meter.CreateCounter<long>("tg.webhooks.retried");
    private static readonly Counter<long> Dead = Meter.CreateCounter<long>("tg.webhooks.dead_lettered");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            do
            {
                try { await RunOnceAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogError(ex, "Webhook outbox scan failed; next cycle will retry"); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<Guid> tids;
        await using (var conn = await systemConnections.OpenSystemAsync(ct))
            tids = (await conn.QueryAsync<Guid>("SELECT TenantId FROM dbo.Tenants WHERE Status=0")).AsList();
        foreach (var tid in tids)
        {
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(new TenantId(tid));
            var repo = scope.ServiceProvider.GetRequiredService<IWebhookOutboxRepository>();
            foreach (var item in await repo.ClaimDueAsync(25, DateTime.UtcNow, ct))
                await DeliverAsync(tid, repo, item, ct);
        }
    }

    private async Task DeliverAsync(Guid tenantId, IWebhookOutboxRepository repo, WebhookOutboxItem item, CancellationToken ct)
    {
        try
        {
            var subscriber = options.Value.Subscribers.SingleOrDefault(x => x.Enabled
                && x.TenantId == tenantId && x.Name == item.SecretRef && x.Url == item.DestinationUrl)
                ?? throw new InvalidOperationException("Webhook subscriber or secret is unavailable.");
            var addresses = await WebhookDestinationValidator.ValidateAsync(item.DestinationUrl, ct);
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var body = Encoding.UTF8.GetBytes(item.PayloadJson);
            var signingInput = Encoding.UTF8.GetBytes(timestamp + "." + item.PayloadJson);
            var signature = Convert.ToHexString(HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(subscriber.Secret), signingInput)).ToLowerInvariant();
            using var request = new HttpRequestMessage(HttpMethod.Post, item.DestinationUrl)
            {
                Content = new ByteArrayContent(body),
            };
            request.Content.Headers.ContentType = new("application/json");
            request.Headers.Add("X-TelemetryGuard-Event", item.EventType);
            request.Headers.Add("X-TelemetryGuard-Delivery", item.DeliveryId.ToString("D"));
            request.Headers.Add("X-TelemetryGuard-Timestamp", timestamp);
            request.Headers.Add("X-TelemetryGuard-Signature", "sha256=" + signature);
            using var client = CreatePinnedClient(addresses);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Subscriber returned HTTP {(int)response.StatusCode}.");
            await repo.CompleteAsync(item.DeliveryId, ct);
            Delivered.Add(1);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var dead = item.AttemptCount >= options.Value.MaxAttempts;
            var exponent = Math.Min(item.AttemptCount, 10);
            var delay = TimeSpan.FromSeconds(Math.Min(3600, Math.Pow(2, exponent) + Random.Shared.NextDouble() * 10));
            await repo.FailAsync(item.DeliveryId, ex.Message[..Math.Min(ex.Message.Length, 1000)],
                DateTime.UtcNow + delay, dead, ct);
            if (dead) Dead.Add(1); else Retried.Add(1);
            logger.LogWarning(ex, "Webhook {DeliveryId} delivery attempt {Attempt} failed", item.DeliveryId, item.AttemptCount);
        }
    }

    private static HttpClient CreatePinnedClient(IReadOnlyList<IPAddress> addresses)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = async (context, ct) =>
            {
                Exception? last = null;
                foreach (var address in addresses)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        socket.Dispose();
                    }
                }
                throw new HttpRequestException("Unable to connect to the validated webhook address.", last);
            },
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(15) };
    }
}
