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

    /// <summary>Extracts from an explicit landing URL (the tracker knows it directly)
    /// or, when that is null/unusable, from the request's Referer.</summary>
    public static Result Extract(HttpContext ctx, string? landingUrl = null)
    {
        var url = landingUrl;
        if (string.IsNullOrWhiteSpace(url))
            url = ctx.Request.Headers.Referer.ToString();

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

        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cookie in ctx.Request.Cookies)
        {
            if (cookies.Count >= MaxEntries)
                break;
            if (!string.IsNullOrEmpty(cookie.Value))
                cookies[cookie.Key] = Truncate(cookie.Value);
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
                ctx.Request.Headers.Referer.ToString(),
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
