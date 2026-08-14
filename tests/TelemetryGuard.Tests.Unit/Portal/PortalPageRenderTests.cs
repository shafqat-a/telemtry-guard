using System.Net;
using TelemetryGuard.Portal.Api;

namespace TelemetryGuard.Tests.Unit.Portal;

/// <summary>P2-03: rendered-HTML assertions for the four screens against a stubbed
/// admin API — security headers, the missing-!=-zero avgScore rule, the D22
/// campaign-less sentinel, per-panel failure isolation (no 500 on a partial
/// outage), XSS escaping, and the key-hint-not-the-key rule.</summary>
public sealed class PortalPageRenderTests
{
    [Fact]
    public async Task Dashboard_Renders_SecurityHeaders()
    {
        using var app = new PortalApp();
        using var client = await app.SignedInClientAsync();

        var resp = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("script-src 'none'", string.Join(" ", resp.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", resp.Headers.GetValues("X-Content-Type-Options").Single());
        // The app's own middleware sets exactly "no-store"; downstream framework
        // components (antiforgery cookie issuance) may append their own
        // cache-prevention directives — "no-store" being present is what matters.
        Assert.Contains("no-store", resp.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Dashboard_RendersKeyHint_NeverTheRawKey()
    {
        using var app = new PortalApp();
        using var client = await app.SignedInClientAsync();

        var html = await (await client.GetAsync("/")).Content.ReadAsStringAsync();

        // Razor's default HtmlEncoder escapes the non-ASCII "…" as a numeric
        // character reference, so assert on the digits (the actual security
        // property under test: only the last 4 chars are ever rendered).
        Assert.Contains("6789", html); // last 4 of StubAdminApiHandler.ValidKey
        Assert.DoesNotContain(StubAdminApiHandler.ValidKey, html);
    }

    [Fact]
    public async Task NoResponseBody_EverContainsAScriptTag()
    {
        using var app = new PortalApp();
        using var client = await app.SignedInClientAsync();

        foreach (var path in new[] { "/", "/review", "/enforcement", "/whitelist" })
        {
            var html = await (await client.GetAsync(path)).Content.ReadAsStringAsync();
            Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Dashboard_ZeroEventDay_RendersDash_NeverZero_ForAvgScore()
    {
        using var app = new PortalApp();
        app.Handler.OnRouteJson(HttpMethod.Get, "/admin/reports/summary", new SummaryReportDto(
            new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 1), Guid.Empty,
            [new SummaryDayDto(new DateOnly(2026, 8, 1), 0, 0, 0, 0, null)]));
        using var client = await app.SignedInClientAsync();

        var html = await (await client.GetAsync("/")).Content.ReadAsStringAsync();

        Assert.Contains("—", html);
        // The totals row's Avg column is also "—", never a fabricated re-aggregation.
        Assert.DoesNotContain(">0.0<", html);
    }

    [Fact]
    public async Task Dashboard_CampaignPicker_IncludesTheOrganicPixelSentinel()
    {
        using var app = new PortalApp();
        app.Handler.OnRouteJson(HttpMethod.Get, "/admin/campaigns", new CampaignListDto(
            [new CampaignSummaryDto(Guid.NewGuid(), "google", "ext-1", 0)]));
        using var client = await app.SignedInClientAsync();

        var html = await (await client.GetAsync("/")).Content.ReadAsStringAsync();

        Assert.Contains("00000000-0000-0000-0000-000000000000", html);
        Assert.Contains("(no campaign — organic / pixel)", html);
    }

    [Fact]
    public async Task Dashboard_OnePanelFailing_StillRendersTheRestOfThePage()
    {
        using var app = new PortalApp();
        app.Handler.OnRoute(HttpMethod.Get, "/admin/sites/integration-status",
            (_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("""{"title":"redis unavailable"}""", System.Text.Encoding.UTF8, "application/problem+json"),
            });
        using var client = await app.SignedInClientAsync();

        var resp = await client.GetAsync("/");
        var html = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // no 500
        Assert.Contains("redis unavailable", html);
        Assert.Contains("Verdict summary", html); // the rest of the page still rendered
    }

    [Fact]
    public async Task Review_HostileValue_IsHtmlEscaped_NeverALiveTag()
    {
        using var app = new PortalApp();
        app.Handler.OnRouteJson(HttpMethod.Get, "/admin/reports/flagged-sources", new FlaggedSourcesReportDto(
            new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 7),
            [new FlaggedSourceDto(new DateOnly(2026, 8, 1), "ip", "<script>alert(1)</script>", 3, 1, 200)]));
        using var client = await app.SignedInClientAsync();

        var html = await (await client.GetAsync("/review")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
    }

    [Fact]
    public async Task Whitelist_HostileReason_IsHtmlEscaped()
    {
        using var app = new PortalApp();
        app.Handler.OnRouteJson(HttpMethod.Get, "/admin/whitelist",
            new List<WhitelistEntryDto> { new(1, "ip", "203.0.113.4", "<img src=x onerror=alert(1)>", "manual", "op", DateTime.UtcNow, null) });
        using var client = await app.SignedInClientAsync();

        var html = await (await client.GetAsync("/whitelist")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("<img src=x onerror=alert(1)>", html);
        Assert.Contains("&lt;img", html);
    }

    [Fact]
    public async Task Enforcement_UnsupportedRow_ShowsTheInt04SentenceVerbatim()
    {
        using var app = new PortalApp();
        app.Handler.OnRouteJson(HttpMethod.Get, "/admin/enforcement",
            new List<ExclusionQueueEntryDto>
            {
                new(1, "meta", "ip", "203.0.113.5", null, "score=91", "unsupported", DateTime.UtcNow, DateTime.UtcNow),
            });
        using var client = await app.SignedInClientAsync();

        var html = await (await client.GetAsync("/enforcement?status=unsupported")).Content.ReadAsStringAsync();

        Assert.Contains(
            "Meta Marketing API provides no IP exclusion capability; entry cannot be enforced on Meta.",
            html);
    }
}
