using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TelemetryGuard.Api.Options;
using TelemetryGuard.Api.Services;
using TelemetryGuard.Data;
using TelemetryGuard.Data.Repositories;

namespace TelemetryGuard.Api.Workers;

public sealed class MarketIqDeliveryService(
    IServiceScopeFactory scopes,
    IConnectionMultiplexer redis,
    IHttpClientFactory clients,
    ISystemConnectionFactory systemConnections,
    IOptions<MarketIqOptions> options,
    ILogger<MarketIqDeliveryService> logger) : BackgroundService
{
    private static readonly Meter Meter=new("TelemetryGuard.MarketIQ");
    private static readonly Counter<long> Delivered=Meter.CreateCounter<long>("tg.marketiq.delivered");
    private static readonly Counter<long> Retried=Meter.CreateCounter<long>("tg.marketiq.retried");
    private static readonly Counter<long> Dead=Meter.CreateCounter<long>("tg.marketiq.dead_lettered");
    private readonly string _consumer=$"{Environment.MachineName}-{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if(!options.Value.Enabled) return;
        await EnsureGroupAsync();
        var nextHealth=DateTime.MinValue;
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainNotificationsAsync();
                await DeliverDueAsync(stoppingToken);
                if(DateTime.UtcNow>=nextHealth)
                {
                    await CheckHealthAsync(stoppingToken);
                    nextHealth=DateTime.UtcNow.AddMinutes(5);
                }
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){ break; }
            catch(Exception ex){ logger.LogError(ex,"MarketIQ delivery cycle failed; SQL outbox remains durable"); }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.PollSeconds),stoppingToken);
        }
    }

    private async Task EnsureGroupAsync()
    {
        try { await redis.GetDatabase().StreamCreateConsumerGroupAsync(options.Value.StreamKey,
            options.Value.ConsumerGroup,"0-0",createStream:true); }
        catch(RedisServerException ex) when(ex.Message.Contains("BUSYGROUP",StringComparison.Ordinal)){ }
    }

    private async Task DrainNotificationsAsync()
    {
        var entries=await redis.GetDatabase().StreamReadGroupAsync(options.Value.StreamKey,
            options.Value.ConsumerGroup,_consumer,">",count:options.Value.ClaimBatchSize);
        if(entries.Length>0)
            await redis.GetDatabase().StreamAcknowledgeAsync(options.Value.StreamKey,
                options.Value.ConsumerGroup,entries.Select(x=>x.Id).ToArray());
    }

    private async Task DeliverDueAsync(CancellationToken ct)
    {
        using var scope=scopes.CreateScope();
        var repo=scope.ServiceProvider.GetRequiredService<IMarketIqOutboxRepository>();
        var due=await repo.ClaimDueAsync(options.Value.ClaimBatchSize,DateTime.UtcNow,ct);
        await Parallel.ForEachAsync(due,new ParallelOptions
        { MaxDegreeOfParallelism=options.Value.MaxParallelism,CancellationToken=ct },
            async(item,token)=>await DeliverAsync(repo,item,token));
    }

    private async Task DeliverAsync(IMarketIqOutboxRepository repo,MarketIqOutboxItem item,CancellationToken ct)
    {
        try
        {
            _=await WebhookDestinationValidator.ValidateAsync(item.DestinationUrl,ct);
            using var request=new HttpRequestMessage(HttpMethod.Post,item.DestinationUrl)
            { Content=new StringContent(item.PayloadJson,Encoding.UTF8,"application/json") };
            using var response=await clients.CreateClient("marketiq").SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
            if(response.StatusCode!=HttpStatusCode.NoContent)
                throw new HttpRequestException($"MarketIQ returned HTTP {(int)response.StatusCode}.");
            await repo.CompleteAsync(item.TenantId,item.DeliveryId,ct);
            Delivered.Add(1);
        }
        catch(Exception ex) when(ex is not OperationCanceledException)
        {
            var dead=item.AttemptCount>=options.Value.MaxAttempts;
            var delay=TimeSpan.FromSeconds(Math.Min(3600,Math.Pow(2,Math.Min(item.AttemptCount,10))+Random.Shared.NextDouble()*10));
            await repo.FailAsync(item.TenantId,item.DeliveryId,ex.Message[..Math.Min(1000,ex.Message.Length)],DateTime.UtcNow+delay,dead,ct);
            if(dead) Dead.Add(1); else Retried.Add(1);
            logger.LogWarning(ex,"MarketIQ event {EventId} attempt {Attempt} failed",item.EventId,item.AttemptCount);
        }
    }

    private async Task CheckHealthAsync(CancellationToken ct)
    {
        await using var conn=await systemConnections.OpenSystemAsync(ct);
        var sites=await conn.QueryAsync<(string Domain,string HealthUrl,string TokenRef)>(new CommandDefinition(
            """SELECT Domain,MarketIqHealthUrl HealthUrl,MarketIqHealthTokenRef TokenRef FROM dbo.Sites WHERE MarketIqEnabled=1 AND MarketIqHealthUrl IS NOT NULL""",
            cancellationToken:ct));
        foreach(var site in sites)
        {
            if(string.IsNullOrEmpty(site.TokenRef)||!options.Value.HealthTokens.TryGetValue(site.TokenRef,out var token))
            { logger.LogWarning("MarketIQ health token {TokenRef} for {Domain} is not configured",site.TokenRef,site.Domain); continue; }
            using var request=new HttpRequestMessage(HttpMethod.Get,site.HealthUrl);
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
            using var response=await clients.CreateClient("marketiq").SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
            if(!response.IsSuccessStatusCode)
            { logger.LogWarning("MarketIQ health for {Domain} returned HTTP {Status}",site.Domain,(int)response.StatusCode); continue; }
            using var document=JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            var root=document.RootElement;
            if(root.TryGetProperty("lost",out var lost)&&lost.TryGetInt64(out var count)&&count>0)
                logger.LogError("MarketIQ reports {Lost} lost events for {Domain}",count,site.Domain);
        }
    }
}
