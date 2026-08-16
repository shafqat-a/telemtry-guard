using Microsoft.AspNetCore.Http;
using TelemetryGuard.Api.Attribution;

namespace TelemetryGuard.Tests.Unit.Api;

/// <summary>ANA-08: which platform sent a visit, which ad, and what must never be
/// copied into the event store.</summary>
public class AttributionExtractorTests
{
    private static HttpContext Ctx(
        string? referer = null,
        (string Name, string Value)[]? cookies = null,
        (string Name, string Value)[]? headers = null)
    {
        var ctx = new DefaultHttpContext();
        if (referer is not null) ctx.Request.Headers.Referer = referer;
        foreach (var (name, value) in headers ?? [])
            ctx.Request.Headers[name] = value;
        if (cookies is { Length: > 0 })
        {
            ctx.Request.Headers.Cookie =
                string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
        }
        return ctx;
    }

    [Fact]
    public void Utm_parameters_are_extracted_from_the_landing_url()
    {
        var result = AttributionExtractor.Extract(Ctx(
            "https://bu.edu.bd/admissions?utm_source=facebook&utm_medium=paid_social" +
            "&utm_campaign=spring-intake&utm_term=bba&utm_content=carousel-3&utm_id=120210"));

        Assert.Equal("facebook", result.UtmSource);
        Assert.Equal("paid_social", result.UtmMedium);
        Assert.Equal("spring-intake", result.UtmCampaign);
        Assert.Equal("bba", result.UtmTerm);
        Assert.Equal("carousel-3", result.UtmContent);
        Assert.Equal("120210", result.UtmId);
        Assert.Equal("/admissions", result.LandingPath);
    }

    [Theory]
    [InlineData("https://x.test/?gclid=abc", "google_ads")]
    [InlineData("https://x.test/?gbraid=abc", "google_ads")]      // iOS / consent-limited
    [InlineData("https://x.test/?wbraid=abc", "google_ads")]
    [InlineData("https://x.test/?fbclid=abc", "meta_ads")]
    [InlineData("https://x.test/?ttclid=abc", "tiktok_ads")]
    [InlineData("https://x.test/?msclkid=abc", "microsoft_ads")]
    public void A_platform_click_id_identifies_the_channel(string url, string expected) =>
        Assert.Equal(expected, AttributionExtractor.Extract(Ctx(url)).AttributionChannel);

    [Theory]
    [InlineData("google", "cpc", "google_ads")]
    [InlineData("facebook", "paid_social", "meta_ads")]
    [InlineData("instagram", "cpc", "meta_ads")]
    [InlineData("tiktok", "paid", "tiktok_ads")]
    [InlineData("bing", "cpc", "microsoft_ads")]
    [InlineData("linkedin", "cpc", "paid_other")]
    [InlineData("newsletter", "email", "referral")]   // tagged, but not a paid medium
    public void Utm_tagging_classifies_when_no_click_id_is_present(
        string source, string medium, string expected)
    {
        var result = AttributionExtractor.Extract(
            Ctx($"https://x.test/?utm_source={source}&utm_medium={medium}"));

        Assert.Equal(expected, result.AttributionChannel);
    }

    [Fact]
    public void A_click_id_outranks_conflicting_utm_tagging()
    {
        // utm_* is copy-pasteable by anyone; a click id is issued by the platform.
        var result = AttributionExtractor.Extract(
            Ctx("https://x.test/?gclid=real&utm_source=facebook&utm_medium=cpc"));

        Assert.Equal("google_ads", result.AttributionChannel);
    }

    [Fact]
    public void Attribution_cookies_carry_the_channel_after_the_click_id_is_gone()
    {
        // Second pageview of a paid session: no click id in the URL any more, but the
        // platform cookie set on landing is still there.
        var result = AttributionExtractor.Extract(Ctx(
            "https://bu.edu.bd/programs",
            cookies: [("_gcl_aw", "GCL.1.abc"), ("_fbp", "fb.1.123.456")]));

        Assert.Equal("GCL.1.abc", result.CookieGclAw);
        Assert.Equal("fb.1.123.456", result.CookieFbp);
        Assert.Equal("google_ads", result.AttributionChannel);
    }

    [Theory]
    [InlineData("https://www.google.com/search?q=bu", "organic_search")]
    [InlineData("https://duckduckgo.com/", "organic_search")]
    [InlineData("https://someblog.example/post", "referral")]
    public void Referrer_decides_when_nothing_is_tagged(string referer, string expected) =>
        Assert.Equal(expected, AttributionExtractor.Extract(Ctx(referer)).AttributionChannel);

    [Fact]
    public void A_self_referral_is_direct_not_referral()
    {
        // On the beacon/pixel paths the Referer IS the tagged page. Counting that as a
        // referral would label every untagged visit "referral" and leave "direct" empty.
        var ctx = Ctx("https://bu.edu.bd/programs");
        ctx.Request.Host = new HostString("bu.edu.bd");

        Assert.Equal("direct", AttributionExtractor.Extract(ctx).AttributionChannel);
    }

    [Fact]
    public void An_external_referrer_is_still_a_referral()
    {
        var ctx = Ctx("https://someblog.example/post");
        ctx.Request.Host = new HostString("bu.edu.bd");

        Assert.Equal("referral", AttributionExtractor.Extract(ctx).AttributionChannel);
    }

    [Fact]
    public void No_referrer_and_no_tagging_is_direct() =>
        Assert.Equal("direct", AttributionExtractor.Extract(Ctx()).AttributionChannel);

    [Fact]
    public void Every_cookie_is_captured_so_a_visitor_can_be_followed_across_pages()
    {
        // D25 (owner decision): the whole request is stored. The cookie jar is the
        // identifier that links one visitor's pageviews together, so it is kept whole —
        // session and auth cookies included. Read access to tg_events is therefore
        // equivalent to holding them.
        var result = AttributionExtractor.Extract(Ctx(
            "https://bu.edu.bd/?utm_source=google",
            cookies:
            [
                ("wordpress_logged_in_abc", "editor|1799999999|TOKEN"),
                ("PHPSESSID", "s3ss10n"),
                ("_ga", "GA1.1.1234567890.1699999999"),
                ("_fbc", "fb.1.1699.CLICKID"),
            ]));

        Assert.Equal("editor|1799999999|TOKEN", result.Cookies["wordpress_logged_in_abc"]);
        Assert.Equal("s3ss10n", result.Cookies["PHPSESSID"]);
        Assert.Equal("GA1.1.1234567890.1699999999", result.Cookies["_ga"]);
        Assert.Equal("fb.1.1699.CLICKID", result.Cookies["_fbc"]);
        // The platform cookies keep their own columns as well — reporting filters on them.
        Assert.Equal("fb.1.1699.CLICKID", result.CookieFbc);
    }

    [Fact]
    public void The_full_landing_url_is_kept_including_its_query()
    {
        var result = AttributionExtractor.Extract(
            Ctx("https://bu.edu.bd/apply?utm_source=google&program=bba&ref=poster7"));

        Assert.Equal("google", result.UtmSource);
        Assert.Equal("https://bu.edu.bd/apply?utm_source=google&program=bba&ref=poster7",
            result.LandingUrl);
        Assert.Equal("/apply", result.LandingPath);          // grouping by page stays cheap
        Assert.Contains("program", result.LandingQueryKeys);
        Assert.Contains("ref", result.LandingQueryKeys);
    }

    [Fact]
    public void Every_header_is_captured_verbatim()
    {
        var result = AttributionExtractor.Extract(Ctx(
            "https://bu.edu.bd/",
            headers:
            [
                ("Sec-Fetch-Site", "cross-site"),
                ("Accept-Language", "bn-BD,en;q=0.9"),
                ("X-Custom-Thing", "whatever"),
            ]));

        Assert.Equal("cross-site", result.Headers["Sec-Fetch-Site"]);
        Assert.Equal("bn-BD,en;q=0.9", result.Headers["Accept-Language"]);
        Assert.Equal("whatever", result.Headers["X-Custom-Thing"]);
        Assert.Equal("https://bu.edu.bd/", result.Headers["Referer"]);
    }

    [Fact]
    public void An_explicit_landing_url_wins_over_the_referer()
    {
        // The tracker knows the destination directly; Referer on /c is the ad platform.
        var result = AttributionExtractor.Extract(
            Ctx("https://www.facebook.com/"),
            "https://tracker.test/c?cid=1&gclid=abc&utm_source=google&utm_medium=cpc");

        Assert.Equal("abc", result.Gclid);
        Assert.Equal("google_ads", result.AttributionChannel);
    }

    // ---------------------------------------------------- SDK-09 page context --

    [Fact]
    public void Cross_origin_recovers_attribution_from_what_the_sdk_reported()
    {
        // Cross-origin the browser trims Referer to the bare origin and sends no
        // cookies, so everything below would otherwise be lost.
        var ctx = Ctx("https://bu.edu.bd/");            // origin only — no path, no query
        ctx.Request.Host = new HostString("tg.example");

        var result = AttributionExtractor.Extract(ctx, client: new AttributionExtractor.ClientContext(
            "https://bu.edu.bd/admissions?utm_source=facebook&utm_medium=paid_social&fbclid=XO_TEST",
            "https://www.facebook.com/",
            new Dictionary<string, string> { ["_ga"] = "GA1.1.X", ["_fbp"] = "fb.1.X" }));

        Assert.Equal("facebook", result.UtmSource);
        Assert.Equal("XO_TEST", result.Fbclid);
        Assert.Equal("meta_ads", result.AttributionChannel);
        Assert.Equal("/admissions", result.LandingPath);
        Assert.Equal("GA1.1.X", result.Cookies["_ga"]);
        Assert.Equal("fb.1.X", result.Cookies["_fbp"]);
    }

    [Fact]
    public void Same_origin_prefers_what_the_server_observed()
    {
        // An informative Referer is a header the server saw; the SDK's report only
        // fills gaps, so a forged page URL cannot override it.
        var ctx = Ctx("https://bu.edu.bd/real?utm_source=google&utm_medium=cpc");
        ctx.Request.Host = new HostString("bu.edu.bd");

        var result = AttributionExtractor.Extract(ctx, client: new AttributionExtractor.ClientContext(
            "https://bu.edu.bd/fake?utm_source=facebook&utm_medium=paid_social", null, null));

        Assert.Equal("google", result.UtmSource);
        Assert.Equal("/real", result.LandingPath);
    }

    [Fact]
    public void Server_cookies_win_over_reported_ones_and_the_two_are_merged()
    {
        // Same-origin the request carries HttpOnly cookies no script can read; the SDK
        // reports the rest. Both should end up on the row, server value winning a clash.
        var ctx = Ctx("https://bu.edu.bd/x", cookies: [("wordpress_logged_in_a", "SERVER"), ("_ga", "SERVER_GA")]);
        ctx.Request.Host = new HostString("bu.edu.bd");

        var result = AttributionExtractor.Extract(ctx, client: new AttributionExtractor.ClientContext(
            "https://bu.edu.bd/x", null,
            new Dictionary<string, string> { ["_ga"] = "CLIENT_GA", ["_fbp"] = "CLIENT_FBP" }));

        Assert.Equal("SERVER", result.Cookies["wordpress_logged_in_a"]);
        Assert.Equal("SERVER_GA", result.Cookies["_ga"]);      // server wins the clash
        Assert.Equal("CLIENT_FBP", result.Cookies["_fbp"]);    // client fills the gap
    }

    [Fact]
    public void The_sdks_referrer_distinguishes_organic_search_from_direct()
    {
        // The request's own Referer is the tagged page itself, so without the SDK's
        // document.referrer every untagged visit looks `direct`.
        var ctx = Ctx("https://bu.edu.bd/programs");
        ctx.Request.Host = new HostString("bu.edu.bd");

        var fromSearch = AttributionExtractor.Extract(ctx, client: new AttributionExtractor.ClientContext(
            "https://bu.edu.bd/programs", "https://www.google.com/search?q=bangladesh+university", null));
        Assert.Equal("organic_search", fromSearch.AttributionChannel);

        var typedIn = AttributionExtractor.Extract(ctx, client: new AttributionExtractor.ClientContext(
            "https://bu.edu.bd/programs", "", null));      // empty referrer means direct
        Assert.Equal("direct", typedIn.AttributionChannel);
    }

    [Fact]
    public void Absurdly_long_values_are_truncated()
    {
        // A cap on value length is an ingest guard (one crafted request must not bloat a
        // row and the batch it rides in), not a content filter.
        var huge = new string('x', 9000);
        var result = AttributionExtractor.Extract(Ctx($"https://x.test/?utm_campaign={huge}"));

        Assert.Equal(4096, result.UtmCampaign.Length);
    }

    [Fact]
    public void A_malformed_referer_degrades_to_direct_without_throwing()
    {
        var result = AttributionExtractor.Extract(Ctx("not-a-url"));

        Assert.Equal("direct", result.AttributionChannel);
        Assert.Null(result.LandingPath);
        Assert.Empty(result.LandingQueryKeys);
    }
}
