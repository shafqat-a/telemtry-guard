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
/// Two deliberate limits on what is copied into the event store:
///   * Cookies: an allowlist of the four platform attribution cookies. The raw Cookie
///     header also carries session/auth cookies (on a WordPress site, every logged-in
///     editor's) and must never be persisted alongside analytics.
///   * Query values: only the marketing parameters get stored. Everything else is
///     recorded by NAME only, mirroring how header names are already handled — a GET
///     form post can put an email address in a query string.
/// </summary>
public static class AttributionExtractor
{
    /// <summary>Marketing parameters whose values are safe and useful to persist.</summary>
    private static readonly FrozenSet<string> MarketingParams = new[]
    {
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "utm_id",
        "gclid", "gbraid", "wbraid", "fbclid", "ttclid", "msclkid",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Header values worth keeping for attribution and forensics. Cookie and
    /// Authorization are absent by design and must stay absent.</summary>
    private static readonly string[] HeaderAllowlist =
    {
        "Referer", "Origin", "Accept", "Accept-Encoding", "Accept-Language",
        "Sec-Fetch-Site", "Sec-Fetch-Mode", "Sec-Fetch-Dest", "Sec-Fetch-User",
        "Upgrade-Insecure-Requests", "DNT", "X-Requested-With", "Sec-CH-UA-Platform-Version",
    };

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
        public string? LandingPath { get; init; }
        public IReadOnlyList<string> LandingQueryKeys { get; init; } = Array.Empty<string>();
        public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

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
        var queryKeys = Array.Empty<string>();
        var marketing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(url) && TryParse(url, out var uri))
        {
            path = uri.AbsolutePath;
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
        foreach (var name in HeaderAllowlist)
        {
            var value = ctx.Request.Headers[name].ToString();
            if (string.IsNullOrEmpty(value))
                continue;
            var sanitized = Sanitize(name, value);
            if (sanitized.Length > 0)
                headers[name] = Truncate(sanitized);
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
            LandingPath = path,
            LandingQueryKeys = queryKeys,
            Headers = headers,
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

    /// <summary>Header values are stored verbatim EXCEPT Referer, which is the landing
    /// URL and therefore carries the same query string this class deliberately refuses to
    /// persist (a GET form can put an email in it). Keep scheme+host+path; the query's
    /// parameter names are already captured in LandingQueryKeys, and the marketing values
    /// have their own columns. An unparseable Referer is dropped rather than stored raw.</summary>
    private static string Sanitize(string name, string value)
    {
        if (!string.Equals(name, "Referer", StringComparison.OrdinalIgnoreCase))
            return value;
        return TryParse(value, out var uri) ? $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}" : "";
    }

    private static bool IsPaidMedium(string medium) =>
        medium.Length > 0 && medium.ToLowerInvariant() is
            "cpc" or "ppc" or "paid" or "paidsearch" or "paid_search" or "paidsocial"
            or "paid_social" or "cpm" or "display" or "banner" or "retargeting";

    private static string NormalizeSource(string source)
    {
        var s = source.ToLowerInvariant();
        var dot = s.IndexOf('.');
        return dot > 0 ? s[..dot] : s;   // "google.com" -> "google"
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

    /// <summary>Ad platforms emit long opaque ids; a cap keeps one crafted request from
    /// bloating a row (and the batch it rides in).</summary>
    private static string Truncate(string value) => value.Length <= 512 ? value : value[..512];
}
