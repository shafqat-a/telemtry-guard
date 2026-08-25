using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StackExchange.Redis;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Data.Models;
using TelemetryGuard.Data.Repositories;
using TelemetryGuard.Data.Tenancy;

namespace TelemetryGuard.Api.Endpoints;

public sealed record CollectConversionRequest(
    string? K,Guid EventId,Guid GoalId,string? SessionId,string? VisitId,
    string? PageUrl,DateTime? OccurredAt,long? PageToConversionMs);

public static class ConversionEndpoints
{
    public static IEndpointRouteBuilder MapConversionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/i/conversion",CollectAsync);
        return app;
    }

    private static async Task<IResult> CollectAsync(
        HttpContext ctx,ITenantResolver resolver,TenantContext tenant,
        IConversionRepository conversions,ISiteRepository sites,IMarketIqOutboxRepository outbox,
        IConnectionMultiplexer redis,CancellationToken ct)
    {
        ctx.Response.Headers.AccessControlAllowOrigin="*";
        CollectConversionRequest? request;
        try
        {
            request=await JsonSerializer.DeserializeAsync<CollectConversionRequest>(ctx.Request.Body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web),ct);
        }
        catch(JsonException){ return Results.NoContent(); }
        if(request is null) return Results.NoContent();
        if(string.IsNullOrWhiteSpace(request.K)) return Results.NoContent();
        var resolved=await resolver.ResolveSiteKeyAsync(request.K,ct);
        if(resolved is null) return Results.NoContent();
        tenant.Resolve(new TenantId(resolved.TenantId),resolved.SiteKey);
        if(request.EventId==Guid.Empty||request.GoalId==Guid.Empty) return Results.NoContent();
        var goal=await conversions.GetGoalAsync(request.GoalId,ct);
        if(goal is null||!goal.IsActive||goal.SiteKey!=resolved.SiteKey||goal.TriggerType=="server") return Results.NoContent();
        if(!MatchesPage(goal.PagePathsJson,request.PageUrl)) return Results.NoContent();
        var occurred=(request.OccurredAt??DateTime.UtcNow).ToUniversalTime();
        if(occurred<DateTime.UtcNow.AddDays(-7)||occurred>DateTime.UtcNow.AddMinutes(10)) return Results.NoContent();
        var duration=request.PageToConversionMs is >=0 and <=86400000?request.PageToConversionMs:null;
        var inserted=await conversions.InsertEventAsync(new ConversionEventRecord(
            resolved.TenantId,request.EventId,request.GoalId,resolved.SiteKey,
            Limit(request.SessionId,64),Limit(request.VisitId,64),Limit(request.PageUrl,2048),
            occurred,duration,false,"browser",null,null),ct);
        if(!inserted||!goal.SendMarketIq) return Results.NoContent();
        var site=await sites.GetBySiteKeyAsync(resolved.SiteKey,ct);
        if(site is not { MarketIqEnabled:true,MarketIqCompanyId:>0 }||string.IsNullOrWhiteSpace(site.MarketIqCollectUrl))
            return Results.NoContent();
        var eventId=$"conversion-{request.EventId:D}";
        var payload=JsonSerializer.Serialize(new Dictionary<string,object?>
        {
            ["companyId"]=site.MarketIqCompanyId,["event_id"]=eventId,
            ["session_id"]=Limit(request.SessionId,64),["occurred_at"]=occurred.ToString("O"),
            ["ip"]=ctx.Connection.RemoteIpAddress?.ToString(),["page_to_conversion_ms"]=duration,
            ["verified_conversion"]=false,["conversion_goal_id"]=goal.GoalId,
            ["conversion_goal_name"]=goal.Name,["tg_export_mode"]="live_conversion"
        }.Where(x=>x.Value is not null).ToDictionary());
        var deliveryId=new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{resolved.TenantId:D}|{resolved.SiteKey}|{eventId}")).AsSpan(0,16));
        if(await outbox.EnqueueAsync(new(deliveryId,resolved.SiteKey,eventId,site.MarketIqCollectUrl,payload,
            site.MarketIqRelayKeyRef,DateTime.UtcNow),ct))
            await redis.GetDatabase().StreamAddAsync("tg:marketiq:deliveries",
                [new NameValueEntry("tenant_id",resolved.TenantId.ToString("D")),new NameValueEntry("delivery_id",deliveryId.ToString("D"))],
                maxLength:100000,useApproximateMaxLength:true);
        return Results.NoContent();
    }
    private static bool MatchesPage(string json,string? pageUrl)
    {
        if(!Uri.TryCreate(pageUrl,UriKind.Absolute,out var uri)) return false;
        try { return (JsonSerializer.Deserialize<string[]>(json)??[]).Any(p=>
            string.Equals(Normalize(p),Normalize(uri.AbsolutePath),StringComparison.Ordinal)); }
        catch(JsonException){ return false; }
    }
    private static string Normalize(string p)=>p.Length>1?p.Trim().TrimEnd('/'):"/";
    private static string? Limit(string? value,int max)=>string.IsNullOrWhiteSpace(value)?null:value[..Math.Min(value.Length,max)];
}
