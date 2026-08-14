using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using StackExchange.Redis;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Api.Edge;
using TelemetryGuard.Api.Options;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Core.Time;
using TelemetryGuard.Data.Repositories;
using TelemetryGuard.RiskEngine.Velocity;

namespace TelemetryGuard.Api.Endpoints;

/// <summary>
/// API-03: GET /p.gif — web-pixel mode for tenants who cannot add script tags
/// (spec D22). Serves a constant 43-byte transparent GIF, captures the same
/// HTTP-layer signals as the tracker (minus click IDs — an &lt;img&gt; tag never
/// carries gclid, so absence proves nothing and no click_id_invalid is recorded
/// here), increments velocity counters, maintains a session id, emits a
/// ClickEvent of kind pixel, and registers a grace entry so pixel-only sites
/// still receive verdicts (API-06 scores them on HTTP + velocity features;
/// all SDK features stay NaN — never 0, spec §7 — and has_js_beacon stays 0).
///
/// The GIF must always be served (status 200) even when Redis or the sink fail:
/// capture may be lost to analytics, never visible to the page. The bytes are
/// identical to DAT-04's anti-probing GIF so known/unknown site keys are
/// indistinguishable to a prober.
/// </summary>
public static partial class PixelEndpoints
{
    /// <summary>43-byte transparent 1x1 GIF89a — byte-identical to the
    /// success-shaped drop TenantResolutionMiddleware serves for unknown keys.</summary>
    internal static readonly byte[] Gif =
    {
        0x47,0x49,0x46,0x38,0x39,0x61,             // "GIF89a"
        0x01,0x00,0x01,0x00,0x80,0x00,0x00,        // logical screen 1x1, GCT of 2
        0x00,0x00,0x00,0xFF,0xFF,0xFF,             // palette: black, white
        0x21,0xF9,0x04,0x01,0x00,0x00,0x00,0x00,   // GCE: transparency on index 0
        0x2C,0x00,0x00,0x00,0x00,0x01,0x00,0x01,0x00,0x00, // image descriptor
        0x02,0x02,0x44,0x01,0x00,                  // LZW min code size + data
        0x3B                                        // trailer
    };

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex SessionIdShape();

    public static IEndpointRouteBuilder MapPixelEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/p.gif", HandleAsync);
        return app;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext ctx, ITenantContext tenant, IEventSink sink,
        IConnectionMultiplexer redis, IClock clock, IMemoryCache cache,
        ITenantRepository tenants, IOptions<TrackerOptions> trackerOpts,
        IOptions<RetentionOptions> retentionOpts, IEdgeSignalReader edgeSignals,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var opts = trackerOpts.Value;
        var tid = tenant.TenantId.Value.ToString("D");

        // 1. Session id: tg_sid query param (the tracker's redirect appends it,
        //    so the landing-page pixel can join the session) > tg_sid cookie >
        //    freshly minted. A Set-Cookie is appended ONLY when minted — a sid
        //    supplied by query/cookie is reused without issuing a new cookie.
        string sid;
        var querySid = ctx.Request.Query["tg_sid"].ToString();
        if (SessionIdShape().IsMatch(querySid))
        {
            sid = querySid;
        }
        else if (ctx.Request.Cookies.TryGetValue(opts.SessionCookieName, out var cookieSid)
                 && cookieSid is not null && SessionIdShape().IsMatch(cookieSid))
        {
            sid = cookieSid;
        }
        else
        {
            sid = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            ctx.Response.Cookies.Append(opts.SessionCookieName, sid, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                MaxAge = TimeSpan.FromSeconds(opts.SessionTtlSeconds),
            });
        }

        // 2. HTTP-layer signals — exactly API-02 step 3.4. RemoteIpAddress is
        //    already proxy-corrected by API-01's ForwardedHeaders middleware;
        //    Kestrel preserves wire order of headers. Missing signal != zero:
        //    absent values stay null (empty string only in the Redis encoding).
        //    NO click-id params are read on this path (tracker-only, API-02).
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

        // 2b. INT-05: Cloudflare edge signals (X-TG-*) — see API-02 step 4b for the
        //     gating/spoof-defense contract. tls_fp mirrors the tracker's contract
        //     field for RSK-04 (JA3 wins, JA4 fallback).
        var edge = edgeSignals.Read(ctx);
        var tlsFp = edge.Ja3 ?? edge.Ja4;

        // Steps 3–6 must never break the response: capture may be lost to
        // analytics on a Redis/sink/SQL hiccup, but the GIF is always served.
        try
        {
            // 3. Velocity counters (RSK-03). Resolved optionally so this task
            //    does not hard-depend on the velocity store being registered.
            //    clickId is null by design — pixels carry no click ids, so the
            //    null return ("no click id") is correct here, not an error.
            var velocity = ctx.RequestServices.GetService<IVelocityStore>();
            if (velocity is not null)
                await velocity.RecordClickAsync(ip, ua, clickId: null, ct);

            var db = redis.GetDatabase();
            var now = clock.UtcNow;

            // 4. Click-context hash (same field contract as API-02 step 3.7,
            //    consumed by RSK-04/RSK-07) — non-clobbering: when the tracker
            //    hit came first the hash already exists with kind=tracker and a
            //    superset of these fields, so skip the write entirely; never
            //    overwrite kind=tracker with pixel.
            var clickKey = $"t:{tid}:click:{sid}";
            var existingKind = await db.HashGetAsync(clickKey, "kind");
            if (existingKind.IsNullOrEmpty)
            {
                var fields = new HashEntry[]
                {
                    new("kind", "pixel"),
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
                    // INT-05: RSK-04's actual TlsUaMismatch contract field.
                    new("tls_fp", tlsFp ?? ""),
                    // INT-05: forward-compat extras (no current reader; empty = absent).
                    new("tls_ja3", edge.Ja3 ?? ""),
                    new("tls_ja4", edge.Ja4 ?? ""),
                    new("cf_asn", edge.Asn?.ToString() ?? ""),
                    new("cf_bot_score", edge.BotScore?.ToString() ?? ""),
                };
                await db.HashSetAsync(clickKey, fields);
                await db.KeyExpireAsync(clickKey, TimeSpan.FromSeconds(opts.SessionTtlSeconds));
            }

            // 5. Grace entry with NX semantics: a tracker-created entry's
            //    deadline must not be reset by a later pixel hit. API-06 scores
            //    the session on HTTP + velocity alone after the deadline.
            var deadline = now.ToUnixTimeSeconds() + opts.GraceSeconds;
            await db.SortedSetAddAsync($"t:{tid}:grace", sid, deadline, When.NotExists);
            await db.SetAddAsync("grace:tenants", tid); // bookkeeping only — holds tenant ids, not tenant data

            // 6. Emit the pixel ClickEvent. No click-id fields, no SDK/
            //    behavioral fields (left at their absent defaults — NaN/null
            //    semantics happen downstream, spec §7); HasJsBeacon stays false
            //    because no beacon ever arrives in pixel mode. RetentionDays is
            //    denormalized from tenant config at ingest (D20), cached 60 s;
            //    ANA-03's sink only enqueues (non-blocking).
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
                Kind = EventKind.Pixel,
                ClickIdInvalid = null,         // not applicable — absence of a click id here proves nothing
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
                HasJsBeacon = false,           // pixel path: SDK features stay NaN/null (missing != zero)
                RetentionDays = retentionDays,
                TimestampUtc = now.UtcDateTime,
            };
            await sink.WriteBatchAsync(new[] { evt }, ct);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("TelemetryGuard.Api.Endpoints.PixelEndpoints").LogWarning(ex,
                "Pixel capture failed for tenant {TenantId}; still serving the GIF (view lost to analytics, not the page)",
                tid);
        }

        // 7. Respond: the constant GIF, never cacheable — every page view must
        //    hit the server, so no browser/proxy may replay a cached pixel.
        ctx.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        ctx.Response.Headers.Pragma = "no-cache";
        ctx.Response.Headers.Expires = "0";
        return Results.Bytes(Gif, "image/gif");
    }

    private static string? NullIfEmpty(StringValues values)
    {
        var s = values.ToString();
        return string.IsNullOrEmpty(s) ? null : s;
    }
}
