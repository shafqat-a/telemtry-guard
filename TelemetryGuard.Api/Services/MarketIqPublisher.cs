using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StackExchange.Redis;
using Microsoft.Extensions.Options;
using TelemetryGuard.Api.Options;
using TelemetryGuard.Data.Repositories;
using TelemetryGuard.RiskEngine.Contracts;

namespace TelemetryGuard.Api.Services;

public interface IMarketIqPublisher
{
    Task PublishVisitAsync(string siteKey, string visitId, string sessionId, DateTime occurredUtc,
        string? userAgent, IReadOnlyDictionary<string,string> click,
        IReadOnlyDictionary<string,string> session, FraudFeatureVector? features, CancellationToken ct);
}

public sealed class MarketIqPublisher(
    ISiteRepository sites,
    IMarketIqOutboxRepository outbox,
    IConnectionMultiplexer redis,
    IOptions<MarketIqOptions> options) : IMarketIqPublisher
{
    public async Task PublishVisitAsync(string siteKey, string visitId, string sessionId, DateTime occurredUtc,
        string? userAgent, IReadOnlyDictionary<string,string> click,
        IReadOnlyDictionary<string,string> session, FraudFeatureVector? f, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(visitId)) return;
        var site=await sites.GetBySiteKeyAsync(siteKey,ct);
        if (site is not { MarketIqEnabled:true, MarketIqCompanyId:>0 }
            || string.IsNullOrWhiteSpace(site.MarketIqCollectUrl)) return;

        var payload=new Dictionary<string,object?>(StringComparer.Ordinal)
        {
            ["companyId"]=site.MarketIqCompanyId,
            ["event_id"]=visitId,
            ["session_id"]=sessionId,
            ["device_id"]=Get(session,"visitor_id"),
            ["occurred_at"]=occurredUtc.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture),
            ["user_agent"]=userAgent,
            ["utm_campaign_id"]=Get(click,"campaign_id"),
            ["utm_platform"]=Platform(Get(click,"click_id_type")),
            ["webdriver_flag"]=f?.WebdriverFlag,
            ["headless_browser"]=f?.HeadlessBrowser,
            ["emulator_or_vm"]=f?.EmulatorOrVm,
            ["ua_os_mismatch"]=f?.UaOsMismatch,
            ["screen_res_anomalous"]=f?.ScreenResAnomalous,
            ["cookies_disabled"]=f?.CookiesDisabled,
            ["canvas_fp_blocked"]=f?.CanvasFpBlocked,
            ["is_mobile"]=f?.IsMobile,
            ["mouse_events"]=Long(session,"mm_n"),
            ["touch_events"]=Long(session,"pt_touch"),
            ["scroll_events"]=Long(session,"n_scroll"),
            ["keystrokes"]=Long(session,"n_key"),
            ["mouse_path_linearity"]=Finite(f?.MousePathLinearity),
            ["mean_inter_event_ms"]=Finite(f?.MeanInterEventMs),
            ["std_inter_event_ms"]=Finite(f?.StdInterEventMs),
            ["click_before_render"]=f?.ClickBeforeRender,
            ["time_on_page_sec"]=Finite(f?.TimeOnPageSec),
            ["pages_viewed"]=Long(session,"n_pv"),
            ["form_submitted"]=f?.FormSubmitted,
            ["form_fill_time_sec"]=Finite(f?.FormFillTimeSec),
            ["paste_in_identity_fields"]=f?.PasteInIdentityFields,
            ["referrer_missing"]=f?.ReferrerMissing,
            ["honeypot_touched"]=f?.HoneypotTouched,
        };
        foreach(var key in payload.Where(x=>x.Value is null).Select(x=>x.Key).ToArray()) payload.Remove(key);
        var json=JsonSerializer.Serialize(payload,new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var deliveryId=DeterministicId($"{site.TenantId:D}|{siteKey}|{visitId}");
        if (!await outbox.EnqueueAsync(new(deliveryId,siteKey,visitId,site.MarketIqCollectUrl,json,DateTime.UtcNow),ct)) return;

        await redis.GetDatabase().StreamAddAsync(options.Value.StreamKey,
        [new NameValueEntry("tenant_id",site.TenantId.ToString("D")),new NameValueEntry("delivery_id",deliveryId.ToString("D"))],
        maxLength:100000,useApproximateMaxLength:true);
    }

    private static string? Get(IReadOnlyDictionary<string,string> values,string key)
        => values.TryGetValue(key,out var value)&&value.Length>0?value:null;
    private static long? Long(IReadOnlyDictionary<string,string> values,string key)
        => values.TryGetValue(key,out var value)&&long.TryParse(value,out var parsed)?parsed:null;
    private static float? Finite(float? value)
        => value is { } v&&!float.IsNaN(v)&&!float.IsInfinity(v)?v:null;
    private static string? Platform(string? clickIdType)=>clickIdType switch
    {
        "fbclid"=>"facebook","ttclid"=>"tiktok","gclid" or "gbraid" or "wbraid"=>"google",
        "msclkid"=>"microsoft",_=>clickIdType
    };
    private static Guid DeterministicId(string value)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0,16));
}
