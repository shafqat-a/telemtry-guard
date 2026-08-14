using System.Net;
using System.Text.Json;
using TelemetryGuard.Portal.Api;

namespace TelemetryGuard.Tests.Unit.Portal;

/// <summary>P2-03: POST-handler behavior — the D19 review/override request-body
/// contract (manual vs review_screen), the enforcement approve/reject batch call,
/// verbatim rendering of the API's own validation errors, and the mid-session
/// 401-clears-the-cookie contract.</summary>
public sealed class PortalActionPostTests
{
    private static async Task<(string Token, string Html)> GetFormAsync(HttpClient client, string path)
    {
        var resp = await client.GetAsync(path);
        var html = await resp.Content.ReadAsStringAsync();
        return (AntiforgeryHtml.ExtractToken(html), html);
    }

    // ==================================================== review / override =

    [Fact]
    public async Task ReviewOverride_NoSessionId_PostsSourceManual_WithExactBody()
    {
        using var app = new PortalApp();
        app.Handler.OnRouteJson(HttpMethod.Get, "/admin/reports/flagged-sources", new FlaggedSourcesReportDto(
            new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 7), []));
        app.Handler.OnRouteJson(HttpMethod.Post, "/admin/whitelist", new AddWhitelistResponseDto(42), HttpStatusCode.Created);
        using var client = await app.SignedInClientAsync();
        var (token, _) = await GetFormAsync(client, "/review");

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["sourceType"] = "ip",
            ["value"] = "203.0.113.9",
            ["sessionId"] = "",
            ["reason"] = "Marked real customer in portal review",
        });
        var resp = await client.PostAsync("/review?handler=Override", form);

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        var call = Assert.Single(app.Handler.Calls, c => c.Method == HttpMethod.Post && c.PathAndQuery == "/admin/whitelist");
        var body = JsonSerializer.Deserialize<JsonElement>(call.Body!);
        Assert.Equal("manual", body.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("sessionId").ValueKind);
        Assert.Equal("ip", body.GetProperty("type").GetString());
        Assert.Equal("203.0.113.9", body.GetProperty("value").GetString());
    }

    [Fact]
    public async Task ReviewOverride_ValidSessionId_PostsSourceReviewScreen_WithThatSessionId()
    {
        using var app = new PortalApp();
        app.Handler.OnRouteJson(HttpMethod.Get, "/admin/reports/flagged-sources", new FlaggedSourcesReportDto(
            new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 7), []));
        app.Handler.OnRouteJson(HttpMethod.Post, "/admin/whitelist", new AddWhitelistResponseDto(43), HttpStatusCode.Created);
        using var client = await app.SignedInClientAsync();
        var (token, _) = await GetFormAsync(client, "/review");

        const string sessionId = "session_abcdef12";
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["sourceType"] = "device_id",
            ["value"] = "dev-123",
            ["sessionId"] = sessionId,
            ["reason"] = "Marked real customer in portal review",
        });
        var resp = await client.PostAsync("/review?handler=Override", form);
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);

        var call = Assert.Single(app.Handler.Calls, c => c.Method == HttpMethod.Post && c.PathAndQuery == "/admin/whitelist");
        var body = JsonSerializer.Deserialize<JsonElement>(call.Body!);
        Assert.Equal("review_screen", body.GetProperty("source").GetString());
        Assert.Equal(sessionId, body.GetProperty("sessionId").GetString());

        // Follow the redirect to see the confirmation message names the label outcome.
        var follow = await client.GetAsync(resp.Headers.Location);
        var html = await follow.Content.ReadAsStringAsync();
        Assert.Contains("negative training label was recorded", html);
        Assert.Contains(sessionId, html);
    }

    [Fact]
    public async Task ReviewOverride_InvalidSessionId_IsRejected_BeforeAnyApiCall()
    {
        using var app = new PortalApp();
        app.Handler.OnRouteJson(HttpMethod.Get, "/admin/reports/flagged-sources", new FlaggedSourcesReportDto(
            new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 7), []));
        using var client = await app.SignedInClientAsync();
        var (token, _) = await GetFormAsync(client, "/review");

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["sourceType"] = "ip",
            ["value"] = "203.0.113.9",
            ["sessionId"] = "bad", // fails ^[A-Za-z0-9_-]{8,64}$
        });
        var resp = await client.PostAsync("/review?handler=Override", form);

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.DoesNotContain(app.Handler.Calls, c => c.Method == HttpMethod.Post && c.PathAndQuery == "/admin/whitelist");

        var follow = await client.GetAsync(resp.Headers.Location);
        var html = await follow.Content.ReadAsStringAsync();
        Assert.Contains("Session id must match", html);
    }

    [Fact]
    public async Task ReviewOverride_PlacementSourceType_OffersNoOverride_AndIsRejectedServerSideToo()
    {
        using var app = new PortalApp();
        app.Handler.OnRouteJson(HttpMethod.Get, "/admin/reports/flagged-sources", new FlaggedSourcesReportDto(
            new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 7),
            [new FlaggedSourceDto(new DateOnly(2026, 8, 1), "placement", "plc-1", 4, 1, 300)]));
        using var client = await app.SignedInClientAsync();
        var (token, html) = await GetFormAsync(client, "/review");

        // The rendered row has no override form for a placement row.
        Assert.Contains("Not whitelistable", html);

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["sourceType"] = "placement",
            ["value"] = "plc-1",
        });
        var resp = await client.PostAsync("/review?handler=Override", form);
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.DoesNotContain(app.Handler.Calls, c => c.Method == HttpMethod.Post && c.PathAndQuery == "/admin/whitelist");
    }

    // ============================================================ enforcement =

    [Fact]
    public async Task Enforcement_Approve_PostsOnlyTickedIds_AndRendersTheApisNumbersVerbatim()
    {
        using var app = new PortalApp();
        app.Handler.OnRouteJson(HttpMethod.Get, "/admin/enforcement",
            new List<ExclusionQueueEntryDto>
            {
                new(1, "google", "ip", "203.0.113.1", null, "r1", "pending", DateTime.UtcNow, null),
                new(2, "google", "ip", "203.0.113.2", null, "r2", "pending", DateTime.UtcNow, null),
                new(3, "google", "ip", "203.0.113.3", null, "r3", "pending", DateTime.UtcNow, null),
            });
        app.Handler.OnRouteJson(HttpMethod.Post, "/admin/enforcement/approve",
            new EnforcementApproveResponseDto(3, 2));
        using var client = await app.SignedInClientAsync();
        var (token, _) = await GetFormAsync(client, "/enforcement?status=pending");

        // FormUrlEncodedContent only keeps the last value per key, so encode the
        // repeated "ids" pair manually to tick two of the three rows.
        var content = new StringContent(
            $"__RequestVerificationToken={Uri.EscapeDataString(token)}&ids=1&ids=2",
            System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");

        var resp = await client.PostAsync("/enforcement?handler=Approve", content);
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);

        var call = Assert.Single(app.Handler.Calls, c => c.Method == HttpMethod.Post && c.PathAndQuery == "/admin/enforcement/approve");
        var body = JsonSerializer.Deserialize<JsonElement>(call.Body!);
        var ids = body.GetProperty("ids").EnumerateArray().Select(e => e.GetInt64()).ToArray();
        Assert.Equal([1L, 2L], ids);

        var follow = await client.GetAsync(resp.Headers.Location);
        var html = await follow.Content.ReadAsStringAsync();
        Assert.Contains("2 of 3 approved", html);
    }

    [Fact]
    public async Task Enforcement_NonPendingFilter_OmitsActionControls()
    {
        using var app = new PortalApp();
        app.Handler.OnRouteJson(HttpMethod.Get, "/admin/enforcement",
            new List<ExclusionQueueEntryDto>
            {
                new(9, "meta", "ip", "203.0.113.9", null, "r9", "rejected", DateTime.UtcNow, DateTime.UtcNow),
            });
        using var client = await app.SignedInClientAsync();

        var html = await (await client.GetAsync("/enforcement?status=rejected")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("name=\"ids\"", html);
        Assert.DoesNotContain("Approve selected", html);
    }

    // =================================================== 400s and 401 mid-session =

    [Fact]
    public async Task Whitelist_Add_ApiValidationError_RendersVerbatim()
    {
        using var app = new PortalApp();
        app.Handler.OnRoute(HttpMethod.Post, "/admin/whitelist",
            (_, _) => StubAdminApiHandler.ValidationProblem("value", "value must be a valid IP address when type is 'ip'."));
        using var client = await app.SignedInClientAsync();
        var (token, _) = await GetFormAsync(client, "/whitelist");

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["type"] = "ip",
            ["value"] = "not-an-ip",
            ["reason"] = "test",
        });
        var resp = await client.PostAsync("/whitelist?handler=Add", form);
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);

        var follow = await client.GetAsync(resp.Headers.Location);
        var html = await follow.Content.ReadAsStringAsync();
        Assert.Contains("value must be a valid IP address", html);
    }

    [Fact]
    public async Task MidSession_KeyRevoked_401_SignsOutAndRedirectsToSignIn()
    {
        using var app = new PortalApp();
        using var client = await app.SignedInClientAsync();

        // Revoke: the stub now rejects every key, simulating the admin API
        // rejecting a previously-valid key.
        app.Handler.AcceptedApiKey = null;

        var resp = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("/signin", resp.Headers.Location!.ToString());
        Assert.Contains(resp.Headers.GetValues("Set-Cookie"), c => c.StartsWith("tg_portal=", StringComparison.Ordinal));

        // The session really is gone: the very next request is treated as anonymous.
        var again = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
        Assert.Contains("/signin", again.Headers.Location!.ToString());
    }
}
