using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using StackExchange.Redis;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Api.Edge;
using TelemetryGuard.Api.Options;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Core.Time;
using TelemetryGuard.Data.Models;
using TelemetryGuard.Data.Repositories;
using TelemetryGuard.RiskEngine.Velocity;

namespace TelemetryGuard.Api.Endpoints;

/// <summary>
/// API-02: GET /c — the server-side click/redirect tracker ad destination URLs
/// point at (spec §4 component 2, §6.1). Captures HTTP-layer signals for every
/// click (including bots that never execute JS), dedupes ad click IDs via the
/// velocity store's SETNX, registers a grace-period entry for the verdict
/// finalizer (API-06), emits a ClickEvent of kind tracker, and 302-redirects to
/// the campaign's configured landing page. NO scoring here — server budget is
/// &lt;10 ms; scoring is API-05/API-06's separate &lt;50 ms budget.
///
/// OPEN-REDIRECT GUARDRAIL: the redirect target is Campaign.LandingUrl from SQL
/// config ONLY (DAT-05 GetRedirectAsync). It is NEVER derived from a query
/// parameter, header, or any other request-supplied value.
/// </summary>
public static partial class TrackerEndpoints
{
    private static readonly string[] ClickIdParams = ["gclid", "fbclid", "msclkid", "ttclid"];

    /// <summary>Perf observability seam: the [RUN_PERF_TESTS=1] SkippableFact
    /// listens to this source and asserts p50 handler duration &lt; 10 ms.</summary>
    internal static readonly ActivitySource ActivitySource = new("TelemetryGuard.Api.Tracker");

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex SessionIdShape();

    public static IEndpointRouteBuilder MapTrackerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/c", HandleAsync);
        return app;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext ctx, ITenantContext tenant, ICampaignRepository campaigns,
        ITenantRepository tenants, IMemoryCache cache, IVelocityStore velocity,
        IEventSink sink, IConnectionMultiplexer redis, IClock clock,
        IOptions<TrackerOptions> trackerOpts, IOptions<RetentionOptions> retentionOpts,
        IEdgeSignalReader edgeSignals,
        IConfiguration config, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity("tracker.handle");
        var opts = trackerOpts.Value;
        var tid = tenant.TenantId.Value.ToString("D");

        // 1. Campaign id: missing/empty/non-GUID -> plain 404 (bot-facing; leak nothing).
        if (!Guid.TryParse(ctx.Request.Query["cid"], out var campaignGuid))
            return Results.NotFound();

        // 2. Campaign lookup with a short in-memory cache; negative results are
        //    cached with the same TTL. GetRedirectAsync is a single clustered-PK
        //    seek (DAT-05) — do not add another campaign read.
        var campaign = await cache.GetOrCreateAsync($"campaign:{tid}:{campaignGuid:D}", e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(opts.CampaignCacheSeconds);
            return campaigns.GetRedirectAsync(campaignGuid, ct);
        });
        if (campaign is null || campaign.Status != 0)   // Status: 0 = Active (DAT-05)
            return Results.NotFound();

        // 3. Session id: reuse a well-formed cookie value for repeat-click
        //    continuity, else 16 random bytes as 32 lowercase hex chars.
        var sid = ctx.Request.Cookies.TryGetValue(opts.SessionCookieName, out var cookieSid)
                  && cookieSid is not null && SessionIdShape().IsMatch(cookieSid)
            ? cookieSid
            : Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        activity?.SetTag("tg.sid", sid);

        // 4. HTTP-layer signals. RemoteIpAddress is already proxy-corrected by
        //    API-01's ForwardedHeaders middleware. Kestrel preserves wire order
        //    of headers. Missing signal != zero: absent values stay null here
        //    (empty string only in the Redis hash encoding).
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
        var headerNames = ctx.Request.Headers.Select(h => h.Key).ToArray();
        var headerOrder = string.Join(",", headerNames);
        var ua = NullIfEmpty(ctx.Request.Headers.UserAgent);
        var chUa = NullIfEmpty(ctx.Request.Headers["Sec-CH-UA"]);
        var chMobile = NullIfEmpty(ctx.Request.Headers["Sec-CH-UA-Mobile"]);
        var chPlatform = NullIfEmpty(ctx.Request.Headers["Sec-CH-UA-Platform"]);
        var acceptLanguage = NullIfEmpty(ctx.Request.Headers.AcceptLanguage);
        var referrer = NullIfEmpty(ctx.Request.Headers.Referer);
        var siteKey = tenant.SiteKey ?? ctx.Request.Query["k"].ToString();

        // 4b. INT-05: Cloudflare edge signals (X-TG-*), already gated on
        //     Edge:Provider AND a Cloudflare-range direct peer inside the reader —
        //     all-null (never fabricated) when not Cloudflare-fronted. tls_fp is the
        //     contract field RSK-04 reads for tls_ua_mismatch: JA3 wins, JA4 is the
        //     fallback (both fingerprint the TLS handshake; JA3 is the format the
        //     seeded family map is keyed on today).
        var edge = edgeSignals.Read(ctx);
        var tlsFp = edge.Ja3 ?? edge.Ja4;

        // 5. Click-ID extraction: first present param wins; both null when absent
        //    (organic traffic still gets tracked — type + flag are recorded
        //    separately so the rule engine can condition on paid vs organic).
        string? clickIdType = null;
        string? clickIdValue = null;
        foreach (var param in ClickIdParams)
        {
            if (ctx.Request.Query.TryGetValue(param, out var v) && !StringValues.IsNullOrEmpty(v))
            {
                clickIdType = param;
                clickIdValue = v.ToString();
                break;
            }
        }

        // Steps 6–9 + 3b must never break the redirect: the click may be lost to
        // analytics on a Redis/sink/SQL hiccup, never to the advertiser.
        try
        {
            // 6. Velocity + click-id dedupe in ONE Redis round trip (RSK-03).
            //    Pass the RAW click-id value; the type goes in the context hash.
            var firstUse = await velocity.RecordClickAsync(ip, ua, clickIdValue, ct);
            var clickIdInvalid = firstUse is null   // missing click id on a paid tracker click (T1 click_id_invalid)
                              || firstUse == false; // replayed click id (SETNX already claimed)
            activity?.SetTag("tg.click_id_invalid", clickIdInvalid);

            var db = redis.GetDatabase();
            var now = clock.UtcNow;

            // 7. Click-context hash for feature extraction (read by RSK-07 at
            //    scoring time — the HTTP request is long gone by then). Field
            //    names are a binding contract; empty string encodes absent.
            var clickKey = $"t:{tid}:click:{sid}";
            var fields = new HashEntry[]
            {
                new("kind", "tracker"),
                new("ts", now.ToUnixTimeMilliseconds()),
                new("ip", ip),
                new("ua", ua ?? ""),
                new("ch_ua", chUa ?? ""),
                new("ch_mobile", chMobile ?? ""),
                new("ch_platform", chPlatform ?? ""),
                new("accept_language", acceptLanguage ?? ""),
                new("referrer", referrer ?? ""),
                new("header_order", headerOrder),
                new("site_key", siteKey),
                new("campaign_id", campaignGuid.ToString("D")),
                new("click_id_type", clickIdType ?? ""),
                new("click_id", clickIdValue ?? ""),
                new("click_id_invalid", clickIdInvalid ? "1" : "0"),
                // INT-05: RSK-04's actual TlsUaMismatch contract field.
                new("tls_fp", tlsFp ?? ""),
                // INT-05: forward-compat extras (no current reader; kept for future
                // signals/debugging — empty string encodes absent, never "0"/fabricated).
                new("tls_ja3", edge.Ja3 ?? ""),
                new("tls_ja4", edge.Ja4 ?? ""),
                new("cf_asn", edge.Asn?.ToString() ?? ""),
                new("cf_bot_score", edge.BotScore?.ToString() ?? ""),
            };
            await db.HashSetAsync(clickKey, fields);
            await db.KeyExpireAsync(clickKey, TimeSpan.FromSeconds(opts.SessionTtlSeconds));

            // 8. Grace entry: the verdict finalizer (API-06) scores the session
            //    on HTTP + velocity alone when no beacon arrives by the deadline.
            var deadline = now.ToUnixTimeSeconds() + opts.GraceSeconds;
            await db.SortedSetAddAsync($"t:{tid}:grace", sid, deadline);
            await db.SetAddAsync("grace:tenants", tid); // bookkeeping only — holds tenant ids, not tenant data

            // 9. Emit the tracker ClickEvent. RetentionDays is denormalized from
            //    tenant config at ingest (D20), cached 60 s; ANA-03's sink only
            //    enqueues (non-blocking).
            var tenantRecord = await cache.GetOrCreateAsync($"tenantcfg:{tid}", e =>
            {
                e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
                return tenants.GetCurrentAsync(ct);
            });
            var retentionDays = (ushort)(tenantRecord?.RetentionDays ?? retentionOpts.Value.DefaultDays);

            var evt = new ClickEvent
            {
                TenantId = tenant.TenantId,
                SiteKey = siteKey,
                SessionId = sid,
                Kind = EventKind.Tracker,
                CampaignId = campaignGuid.ToString("D"),
                Gclid = clickIdType == "gclid" ? clickIdValue! : "",
                Fbclid = clickIdType == "fbclid" ? clickIdValue! : "",
                Msclkid = clickIdType == "msclkid" ? clickIdValue! : "",
                Ttclid = clickIdType == "ttclid" ? clickIdValue! : "",
                ClickIdInvalid = clickIdInvalid,
                Ip = ip,
                HeaderNames = headerNames,
                UserAgent = ua,
                SecChUa = chUa,
                SecChUaMobile = chMobile,
                SecChUaPlatform = chPlatform,
                AcceptLanguage = acceptLanguage,
                Referrer = referrer,
                TlsJa3 = edge.Ja3,              // INT-05: null unless Cloudflare-fronted (D13)
                TlsJa4 = edge.Ja4,
                CfAsn = edge.Asn,
                HasJsBeacon = false,           // tracker path: SDK features stay NaN/null (missing != zero)
                RetentionDays = retentionDays,
                TimestampUtc = now.UtcDateTime,
            };
            await sink.WriteBatchAsync(new[] { evt }, ct);

            // 3b. Synthetic-bot labeling (SDK-06 contract, D18): Development-only
            //     config flag, guaranteed-positive label once per session.
            if (config.GetValue("Synthetic:Enabled", false)
                && ctx.Request.Headers.ContainsKey("X-TG-Synthetic"))
            {
                var labelSink = ctx.RequestServices.GetService<ILabelSink>();
                if (labelSink is not null)
                {
                    var claimed = await db.StringSetAsync(
                        $"t:{tid}:synth:{sid}", "1", TimeSpan.FromSeconds(3600), When.NotExists);
                    if (claimed)
                    {
                        await labelSink.WriteAsync(new LabelEvent(
                            tenant.TenantId, sid, LabelValues.Fraud, LabelSources.SyntheticBot,
                            clock.UtcNow.UtcDateTime), ct);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("TelemetryGuard.Api.Endpoints.TrackerEndpoints").LogWarning(ex,
                "Tracker capture failed for tenant {TenantId}; continuing to redirect (click lost to analytics, not the advertiser)",
                tid);
        }

        // 10. Redirect — target built from Campaign.LandingUrl ONLY (never from
        //     the request). tg_sid rides the query string because the tracker
        //     domain's cookie is invisible to the landing-page domain; the click
        //     id is passed through for the tenant's own attribution.
        var url = campaign.LandingUrl;
        url = QueryHelpers.AddQueryString(url, "tg_sid", sid);
        if (clickIdValue is not null)
            url = QueryHelpers.AddQueryString(url, clickIdType!, clickIdValue);

        ctx.Response.Cookies.Append(opts.SessionCookieName, sid, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = TimeSpan.FromSeconds(opts.SessionTtlSeconds),
        });
        ctx.Response.Headers.CacheControl = "no-store";
        return Results.Redirect(url, permanent: false);
    }

    private static string? NullIfEmpty(StringValues values)
    {
        var s = values.ToString();
        return string.IsNullOrEmpty(s) ? null : s;
    }
}
