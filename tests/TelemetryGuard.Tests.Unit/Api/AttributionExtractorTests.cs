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
    public void Session_and_auth_cookies_are_never_captured()
    {
        // The guardrail: a WordPress login cookie must not reach the event store,
        // where anyone with read access could replay it.
        var result = AttributionExtractor.Extract(Ctx(
            "https://bu.edu.bd/?utm_source=google",
            cookies:
            [
                ("wordpress_logged_in_abc", "admin|1799999999|SECRETTOKEN"),
                ("PHPSESSID", "s3cr3t"),
                ("_fbc", "fb.1.1699.CLICKID"),
            ]));

        Assert.Equal("fb.1.1699.CLICKID", result.CookieFbc);   // allowlisted one kept
        var everything = string.Join("|", result.Headers.Select(h => $"{h.Key}={h.Value}"));
        Assert.DoesNotContain("SECRETTOKEN", everything);
        Assert.DoesNotContain("PHPSESSID", everything);
        Assert.DoesNotContain("s3cr3t", everything);
        Assert.False(result.Headers.ContainsKey("Cookie"));
    }

    [Fact]
    public void Non_marketing_query_values_are_recorded_by_name_only()
    {
        // A GET form can put an email in a query string; keep the shape, not the value.
        var result = AttributionExtractor.Extract(
            Ctx("https://bu.edu.bd/apply?utm_source=google&email=student%40example.com&ssn=12345"));

        Assert.Equal("google", result.UtmSource);
        Assert.Contains("email", result.LandingQueryKeys);
        Assert.Contains("ssn", result.LandingQueryKeys);

        var everything = string.Join("|", result.LandingQueryKeys)
            + "|" + result.LandingPath
            + "|" + string.Join("|", result.Headers.Values);
        Assert.DoesNotContain("student@example.com", everything);
        Assert.DoesNotContain("12345", everything);
    }

    [Fact]
    public void Allowlisted_headers_are_captured_and_authorization_is_not()
    {
        var result = AttributionExtractor.Extract(Ctx(
            "https://bu.edu.bd/",
            headers:
            [
                ("Sec-Fetch-Site", "cross-site"),
                ("Accept-Language", "bn-BD,en;q=0.9"),
                ("Authorization", "Bearer SUPERSECRET"),
            ]));

        Assert.Equal("cross-site", result.Headers["Sec-Fetch-Site"]);
        Assert.Equal("bn-BD,en;q=0.9", result.Headers["Accept-Language"]);
        Assert.False(result.Headers.ContainsKey("Authorization"));
        Assert.DoesNotContain("SUPERSECRET", string.Join("|", result.Headers.Values));
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

    [Fact]
    public void Absurdly_long_values_are_truncated()
    {
        var huge = new string('x', 5000);
        var result = AttributionExtractor.Extract(Ctx($"https://x.test/?utm_campaign={huge}"));

        Assert.Equal(512, result.UtmCampaign.Length);
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
