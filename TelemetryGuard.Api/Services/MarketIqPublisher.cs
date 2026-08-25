using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StackExchange.Redis;
using Microsoft.Extensions.Options;
using TelemetryGuard.Api.Options;
using TelemetryGuard.Data.Repositories;
using TelemetryGuard.RiskEngine.Contracts;
using TelemetryGuard.RiskEngine.Enrichment;

namespace TelemetryGuard.Api.Services;

public interface IMarketIqPublisher
{
    Task PublishVisitAsync(string siteKey, string visitId, string sessionId, DateTime occurredUtc,
        string? userAgent, IReadOnlyDictionary<string,string> click,
        IReadOnlyDictionary<string,string> session, FraudFeatureVector? features,
        ScoreResult result, string band, string action, int? shadowScore,
        string? shadowScorerVersion, CancellationToken ct);
}

public sealed class MarketIqPublisher(
    ISiteRepository sites,
    IMarketIqOutboxRepository outbox,
    IConnectionMultiplexer redis,
    IIpEnrichmentService enrichment,
    IOptions<MarketIqOptions> options) : IMarketIqPublisher
{
    public async Task PublishVisitAsync(string siteKey, string visitId, string sessionId, DateTime occurredUtc,
        string? userAgent, IReadOnlyDictionary<string,string> click,
        IReadOnlyDictionary<string,string> session, FraudFeatureVector? f,
        ScoreResult result, string band, string action, int? shadowScore,
        string? shadowScorerVersion, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(visitId)) return;
        var site=await sites.GetBySiteKeyAsync(siteKey,ct);
        if (site is not { MarketIqEnabled:true, MarketIqCompanyId:>0 }
            || string.IsNullOrWhiteSpace(site.MarketIqCollectUrl)) return;

        var ip=Get(click,"ip");
        var geo=enrichment.Enrich(ip ?? "");
        var payload=new Dictionary<string,object?>(StringComparer.Ordinal)
        {
            ["companyId"]=site.MarketIqCompanyId,
            ["event_id"]=visitId,
            ["session_id"]=sessionId,
            ["device_id"]=Get(session,"device_id") ?? Get(session,"visitor_id"),
            ["occurred_at"]=occurredUtc.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture),
            ["user_agent"]=userAgent,
            ["ip"]=ip,
            ["utm_publisher_id"]=First(click,"utm_publisher_id","utm_content"),
            ["utm_campaign_id"]=First(click,"utm_campaign_id","utm_id","utm_campaign","campaign_id"),
            ["utm_platform"]=Platform(First(click,"utm_platform","utm_source"),Get(click,"click_id_type")),
            ["webdriver_flag"]=f?.WebdriverFlag,
            ["headless_browser"]=f?.HeadlessBrowser,
            ["emulator_or_vm"]=f?.EmulatorOrVm,
            ["ua_os_mismatch"]=f?.UaOsMismatch,
            ["screen_res_anomalous"]=f?.ScreenResAnomalous,
            ["cookies_disabled"]=f?.CookiesDisabled,
            ["canvas_fp_blocked"]=f?.CanvasFpBlocked,
            ["is_mobile"]=f?.IsMobile,
            ["mouse_events"]=MousePoints(session) ?? 0,
            ["touch_events"]=Long(session,"pt_touch") ?? 0,
            ["scroll_events"]=Long(session,"n_scroll") ?? 0,
            ["keystrokes"]=Long(session,"n_key") ?? 0,
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
            ["honeypot_field_filled"]=Bool(session,"hp_field_filled"),
            ["honeypot_link_clicked"]=Bool(session,"hp_link_clicked"),
            ["honey_identifier_seen"]=Bool(session,"honey_identifier_seen"),
            ["decoy_page"]=DecoyPage(site.MarketIqDecoyPathsJson,site.Domain,
                Get(session,"page_url") ?? Get(click,"landing_url")),
            ["score"]=result.Score,
            ["band"]=band,
            ["action"]=action,
            ["rule_hits"]=result.RuleHits,
            ["scorer_version"]=result.ScorerVersion,
            ["feature_set_version"]=result.FeatureSetVersion,
            ["shadow_score"]=shadowScore,
            ["shadow_scorer_version"]=shadowScorerVersion,
            ["fraud_features"]=FeatureNode(f),
            ["country"]=geo.CountryCode,
            ["city"]=geo.City,
            ["latitude"]=geo.Latitude,
            ["longitude"]=geo.Longitude,
            ["ip_timezone"]=geo.TimeZone,
            ["asn"]=geo.AsnNumber,
            ["asn_org"]=geo.AsnOrganization,
            ["ip_type"]=geo.AsnType.ToString().ToLowerInvariant(),
            ["is_datacenter"]=geo.IsDatacenter,
            ["is_proxy_or_vpn"]=geo.IsProxyOrVpn,
            ["is_tor"]=geo.IsTor,
            ["is_private_relay"]=geo.IsPrivateRelay,
            // Allows MarketIQ to keep live telemetry separate from corrected
            // historical replays when evaluating signal coverage and scores.
            ["tg_export_mode"]="live",
        };
        foreach(var key in payload.Where(x=>x.Value is null).Select(x=>x.Key).ToArray()) payload.Remove(key);
        var json=JsonSerializer.Serialize(payload,new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var deliveryId=DeterministicId($"{site.TenantId:D}|{siteKey}|{visitId}");
        if (!await outbox.EnqueueAsync(new(deliveryId,siteKey,visitId,site.MarketIqCollectUrl,json,
            site.MarketIqRelayKeyRef,DateTime.UtcNow),ct)) return;

        await redis.GetDatabase().StreamAddAsync(options.Value.StreamKey,
        [new NameValueEntry("tenant_id",site.TenantId.ToString("D")),new NameValueEntry("delivery_id",deliveryId.ToString("D"))],
        maxLength:100000,useApproximateMaxLength:true);
    }

    private static string? Get(IReadOnlyDictionary<string,string> values,string key)
        => values.TryGetValue(key,out var value)&&value.Length>0?value:null;
    private static string? First(IReadOnlyDictionary<string,string> values,params string[] keys)
        => keys.Select(key=>Get(values,key)).FirstOrDefault(value=>value is not null);
    private static long? Long(IReadOnlyDictionary<string,string> values,string key)
        => values.TryGetValue(key,out var value)&&long.TryParse(value,out var parsed)?parsed:null;
    private static bool? Bool(IReadOnlyDictionary<string,string> values,string key)
        => values.TryGetValue(key,out var value)?value switch { "1"=>true,"0"=>false,_=>null }:null;
    internal static bool? DecoyPage(string? configuredPathsJson,string? siteDomain,string? pageUrl)
    {
        if(string.IsNullOrWhiteSpace(configuredPathsJson)||string.IsNullOrWhiteSpace(siteDomain)
            ||string.IsNullOrWhiteSpace(pageUrl)) return null;
        if(!Uri.TryCreate(pageUrl,UriKind.Absolute,out var uri)) return null;
        if(!string.Equals(uri.IdnHost,siteDomain.Trim().TrimEnd('.'),StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var paths=JsonSerializer.Deserialize<string[]>(configuredPathsJson);
            if(paths is null||paths.Length==0) return null;
            return paths.Any(path=>path is not null
                && string.Equals(NormalizePath(path),NormalizePath(uri.AbsolutePath),StringComparison.Ordinal));
        }
        catch(JsonException){ return null; }
    }
    private static string NormalizePath(string path)
    {
        var normalized=path.Trim();
        if(!normalized.StartsWith('/')) normalized="/"+normalized;
        return normalized.Length>1?normalized.TrimEnd('/'):normalized;
    }
    private static long? MousePoints(IReadOnlyDictionary<string,string> values)
    {
        // mm_n is the number of gaps between mouse samples. Presence of the first
        // coordinate proves at least one point; N gaps therefore means N+1 points.
        if (!values.ContainsKey("mm_first_x")) return null;
        return (Long(values,"mm_n") ?? 0) + 1;
    }
    private static float? Finite(float? value)
        => value is { } v&&!float.IsNaN(v)&&!float.IsInfinity(v)?v:null;
    private static JsonNode? FeatureNode(FraudFeatureVector? value)
    {
        if(value is null) return null;
        var jsonOptions=new JsonSerializerOptions(FraudFeatureVectorJson.Options)
        { PropertyNamingPolicy=JsonNamingPolicy.CamelCase };
        var node=JsonSerializer.SerializeToNode(value,jsonOptions);
        ReplaceNonFinite(node);
        return node;
    }
    private static void ReplaceNonFinite(JsonNode? node)
    {
        if(node is JsonObject obj)
            foreach(var key in obj.Select(x=>x.Key).ToArray())
            {
                if(obj[key] is JsonValue v&&v.TryGetValue<string>(out var s)
                    && s is "NaN" or "Infinity" or "-Infinity") obj[key]=null;
                else ReplaceNonFinite(obj[key]);
            }
        else if(node is JsonArray arr)
            foreach(var child in arr) ReplaceNonFinite(child);
    }
    private static string? Platform(string? tagged,string? clickIdType)
    {
        if(!string.IsNullOrWhiteSpace(tagged))
        {
            var value=tagged.Trim().ToLowerInvariant();
            return value switch
            {
                "fb" or "facebook.com" or "meta"=>"facebook",
                "tt" or "tiktok.com"=>"tiktok",
                "google.com" or "googleads" or "google_ads"=>"google",
                _=>value
            };
        }
        return clickIdType switch
    {
        "fbclid"=>"facebook","ttclid"=>"tiktok","gclid" or "gbraid" or "wbraid"=>"google",
        "msclkid"=>"microsoft",_=>clickIdType
    };
    }
    private static Guid DeterministicId(string value)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0,16));
}
