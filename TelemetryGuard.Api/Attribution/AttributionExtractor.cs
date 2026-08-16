using System.Collections.Frozen;
using Microsoft.AspNetCore.WebUtilities;

namespace TelemetryGuard.Api.Attribution;

/// <summary>
/// ANA-08: pulls ad-attribution signals out of a request — which platform sent the
/// visit, which ad, and the UTM tagging that came with it. Shared by all three capture
/// paths, which differ only in where the landing URL comes from:
///
///   tracker (/c)  — the destination URL is the request's own query string.
///   pixel (/p.gif) and beacon (/i) — the landing URL is the Referer header, which
///                   carries the full path+query only because the endpoint is
///                   SAME-ORIGIN with the page. Mounted on a different host, the
///                   default referrer policy (strict-origin-when-cross-origin) sends
///                   the origin alone and every UTM is lost. That is a real constraint
///                   on where this API may be deployed, not a detail.
///
/// FULL-REQUEST CAPTURE (owner decision, D25): every request header and every cookie is
/// stored verbatim, along with the full landing URL, so a visitor can be followed across
/// pages using whatever identifier their own cookies carry. The marketing parameters and
/// the four platform cookies additionally get their own columns because they are what
/// reporting filters on; the raw maps are the record of everything else.
///
/// Consequences to keep in mind when granting access to tg_events: the cookie map
/// contains session and auth cookies (on a WordPress site, every logged-in editor's),
/// and the landing URL contains whatever a GET form put in the query string. Treat read
/// access to the event store as equivalent to those credentials.
///
/// Values are capped per entry (see MaxValueLength) — an ingest guard against one
/// crafted request bloating a row and the batch it rides in, not a filter.
/// </summary>
public static class AttributionExtractor
{
    /// <summary>Marketing parameters whose values are safe and useful to persist.</summary>
    private static readonly FrozenSet<string> MarketingParams = new[]
    {
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "utm_id",
        "gclid", "gbraid", "wbraid", "fbclid", "ttclid", "msclkid",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-value cap. Ad platforms and auth cookies both emit long opaque
    /// strings; this bounds one crafted request's effect on a row.</summary>
    private const int MaxValueLength = 4096;

    /// <summary>Cap on how many headers/cookies are recorded per request.</summary>
    private const int MaxEntries = 64;

    /// <summary>Attribution cookies, by platform. Values are opaque click/browser ids.</summary>
    private const string FbcCookie = "_fbc";
    private const string FbpCookie = "_fbp";
    private const string GclAwCookie = "_gcl_aw";
    private const string TtpCookie = "_ttp";

    public sealed record Result
    {
        public string Gclid { get; init; } = "";
        public string Gbraid { get; init; } = "";
        public string Wbraid { get; init; } = "";
        public string Fbclid { get; init; } = "";
        public string Ttclid { get; init; } = "";
        public string Msclkid { get; init; } = "";
        public string UtmSource { get; init; } = "";
        public string UtmMedium { get; init; } = "";
        public string UtmCampaign { get; init; } = "";
        public string UtmTerm { get; init; } = "";
        public string UtmContent { get; init; } = "";
        public string UtmId { get; init; } = "";
        public string CookieFbc { get; init; } = "";
        public string CookieFbp { get; init; } = "";
        public string CookieGclAw { get; init; } = "";
        public string CookieTtp { get; init; } = "";
        public string AttributionChannel { get; init; } = "";
        public string? LandingUrl { get; init; }
        public string? LandingPath { get; init; }
        public IReadOnlyList<string> LandingQueryKeys { get; init; } = Array.Empty<string>();
        public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
        public IReadOnlyDictionary<string, string> Cookies { get; init; } = new Dictionary<string, string>();

        public static Result Empty { get; } = new();
    }

    /// <summary>What the SDK reported about the page (SDK-09). Only non-null on the
    /// beacon path, and only load-bearing when the API is cross-origin with the page —
    /// there the server sees neither the landing URL nor the cookies.</summary>
    public sealed record ClientContext(
        string? PageUrl,
        string? Referrer,
        IReadOnlyDictionary<string, string>? Cookies);

    /// <summary>Extracts from an explicit landing URL (the tracker knows it directly),
    /// the request's own Referer, or — when the server cannot see a useful one because
    /// the API is on another origin — what the SDK reported.</summary>
    public static Result Extract(HttpContext ctx, string? landingUrl = null, ClientContext? client = null)
    {
        // Precedence for the landing URL:
        //   1. an explicit URL (the tracker knows its destination)
        //   2. the request's Referer IF informative — same-origin that is the full
        //      path+query, and a header the server observed cannot be forged
        //   3. what the SDK reported — cross-origin the Referer is trimmed to the bare
        //      origin, so this is the only place the utm_* and click ids survive
        var url = landingUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            var headerReferer = ctx.Request.Headers.Referer.ToString();
            url = IsInformative(headerReferer) ? headerReferer
                : !string.IsNullOrWhiteSpace(client?.PageUrl) ? client!.PageUrl
                : headerReferer;
        }

        string? path = null;
        string? fullUrl = null;
        var queryKeys = Array.Empty<string>();
        var marketing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(url) && TryParse(url, out var uri))
        {
            path = uri.AbsolutePath;
            fullUrl = Truncate(uri.AbsoluteUri);   // query included — full-request capture
            var parsed = QueryHelpers.ParseQuery(uri.Query);
            queryKeys = parsed.Keys.ToArray();
            foreach (var (key, values) in parsed)
            {
                if (!MarketingParams.Contains(key))
                    continue;
                var value = values.ToString();
                if (!string.IsNullOrEmpty(value))
                    marketing[key] = Truncate(value);
            }
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in ctx.Request.Headers)
        {
            if (headers.Count >= MaxEntries)
                break;
            var value = header.Value.ToString();
            if (!string.IsNullOrEmpty(value))
                headers[header.Key] = Truncate(value);
        }

        // Cookies the SERVER saw come first — same-origin that includes HttpOnly ones
        // (a site's login/session cookies), which no script can read. Then anything the
        // SDK reported that is not already present: cross-origin the request carries no
        // cookies at all, so this is the whole jar minus the HttpOnly ones.
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cookie in ctx.Request.Cookies)
        {
            if (cookies.Count >= MaxEntries)
                break;
            if (!string.IsNullOrEmpty(cookie.Value))
                cookies[cookie.Key] = Truncate(cookie.Value);
        }
        if (client?.Cookies is { Count: > 0 } reported)
        {
            foreach (var (name, value) in reported)
            {
                if (cookies.Count >= MaxEntries)
                    break;
                if (!string.IsNullOrEmpty(value) && !cookies.ContainsKey(name))
                    cookies[name] = Truncate(value);
            }
        }

        var gclid = Get(marketing, "gclid");
        var gbraid = Get(marketing, "gbraid");
        var wbraid = Get(marketing, "wbraid");
        var fbclid = Get(marketing, "fbclid");
        var ttclid = Get(marketing, "ttclid");
        var msclkid = Get(marketing, "msclkid");
        var utmSource = Get(marketing, "utm_source");
        var utmMedium = Get(marketing, "utm_medium");

        var cookieFbc = Cookie(ctx, FbcCookie);
        var cookieFbp = Cookie(ctx, FbpCookie);
        var cookieGclAw = Cookie(ctx, GclAwCookie);
        var cookieTtp = Cookie(ctx, TtpCookie);

        return new Result
        {
            Gclid = gclid,
            Gbraid = gbraid,
            Wbraid = wbraid,
            Fbclid = fbclid,
            Ttclid = ttclid,
            Msclkid = msclkid,
            UtmSource = utmSource,
            UtmMedium = utmMedium,
            UtmCampaign = Get(marketing, "utm_campaign"),
            UtmTerm = Get(marketing, "utm_term"),
            UtmContent = Get(marketing, "utm_content"),
            UtmId = Get(marketing, "utm_id"),
            CookieFbc = cookieFbc,
            CookieFbp = cookieFbp,
            CookieGclAw = cookieGclAw,
            CookieTtp = cookieTtp,
            AttributionChannel = Classify(
                gclid, gbraid, wbraid, fbclid, ttclid, msclkid,
                utmSource, utmMedium, cookieGclAw, cookieFbc,
                // document.referrer is the TRUE external referrer. The request's own
                // Referer is the tagged page itself, which is why an untagged visit
                // otherwise records as `direct` even when it came from a search engine.
                // When the SDK reported page context at all its referrer is
                // authoritative — including when empty, which means direct.
                client?.PageUrl is not null ? (client.Referrer ?? "") : ctx.Request.Headers.Referer.ToString(),
                ctx.Request.Host.Host),
            LandingUrl = fullUrl,
            LandingPath = path,
            LandingQueryKeys = queryKeys,
            Headers = headers,
            Cookies = cookies,
        };
    }

    /// <summary>Precedence: a platform click id is proof of a paid click, so it outranks
    /// UTM tagging (which anyone can copy into a link) and cookies (which persist long
    /// after the click that set them). Only then does utm_medium decide paid-vs-organic,
    /// and only then does the referrer decide search-vs-referral.</summary>
    internal static string Classify(
        string gclid, string gbraid, string wbraid, string fbclid, string ttclid, string msclkid,
        string utmSource, string utmMedium, string cookieGclAw, string cookieFbc, string referer,
        string? selfHost = null)
    {
        if (gclid.Length > 0 || gbraid.Length > 0 || wbraid.Length > 0) return "google_ads";
        if (fbclid.Length > 0) return "meta_ads";
        if (ttclid.Length > 0) return "tiktok_ads";
        if (msclkid.Length > 0) return "microsoft_ads";

        if (IsPaidMedium(utmMedium))
        {
            return NormalizeSource(utmSource) switch
            {
                "google" => "google_ads",
                "facebook" or "meta" or "instagram" or "fb" => "meta_ads",
                "tiktok" => "tiktok_ads",
                "bing" or "microsoft" => "microsoft_ads",
                _ => "paid_other",
            };
        }

        // A click id cookie without a click id on this request means the visit happened
        // in a session that started with a paid click — attribution, one step removed.
        if (cookieGclAw.Length > 0) return "google_ads";
        if (cookieFbc.Length > 0) return "meta_ads";

        if (utmSource.Length > 0 || utmMedium.Length > 0) return "referral";

        // An absent OR unparseable Referer is "direct": there is nothing to attribute to.
        if (!TryParse(referer, out var refererUri)) return "direct";

        // SELF-REFERRAL. On the pixel and beacon paths the Referer is the tagged page
        // itself, not where the visitor came from, so treating it as a referral would
        // label every untagged visit "referral" and leave "direct" permanently empty.
        // The visitor's true external referrer is document.referrer, which lives in the
        // browser and is not sent on these requests — until the SDK forwards it,
        // untagged JS visits are honestly "direct", not misattributed.
        if (selfHost is { Length: > 0 }
            && string.Equals(refererUri.Host, selfHost, StringComparison.OrdinalIgnoreCase))
        {
            return "direct";
        }

        return IsSearchEngine(refererUri) ? "organic_search" : "referral";
    }

    private static bool IsPaidMedium(string medium) =>
        medium.Length > 0 && medium.ToLowerInvariant() is
            "cpc" or "ppc" or "paid" or "paidsearch" or "paid_search" or "paidsocial"
            or "paid_social" or "cpm" or "display" or "banner" or "retargeting";

    /// <summary>"google.com" -> "google", so utm_source spelling variants collapse.</summary>
    private static string NormalizeSource(string source)
    {
        var s = source.ToLowerInvariant();
        var dot = s.IndexOf('.');
        return dot > 0 ? s[..dot] : s;
    }

    private static bool IsSearchEngine(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        return host.Contains("google.") || host.Contains("bing.") || host.Contains("duckduckgo.")
            || host.Contains("yahoo.") || host.Contains("yandex.") || host.Contains("baidu.")
            || host.Contains("ecosia.") || host.Contains("brave.");
    }

    /// <summary>Whether a URL says more than "some page on this host" — i.e. it kept a
    /// path or a query. `strict-origin-when-cross-origin` trims a cross-origin Referer to
    /// exactly the bare origin, which is what this detects.</summary>
    private static bool IsInformative(string value) =>
        TryParse(value, out var uri) && (uri.Query.Length > 0 || uri.AbsolutePath.Length > 1);

    private static bool TryParse(string value, out Uri uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri!)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string Get(Dictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var v) ? v : "";

    private static string Cookie(HttpContext ctx, string name) =>
        ctx.Request.Cookies.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v) ? Truncate(v) : "";

    private static string Truncate(string value) =>
        value.Length <= MaxValueLength ? value : value[..MaxValueLength];
}
