using System.Net;
using System.Net.Http.Headers;
using TelemetryGuard.Portal.Auth;

namespace TelemetryGuard.Tests.Unit.Portal;

/// <summary>P2-03: sign-in / sign-out / session behavior against a stubbed admin
/// API (no containers, no real HTTP). Covers the acceptance criteria around
/// anonymous redirect, the open-redirect guard, the sign-in throttle, and the
/// X-Api-Key replay contract.</summary>
public sealed class PortalSignInTests
{
    private static async Task<(string Token, IEnumerable<string> Cookies)> GetSignInFormAsync(HttpClient client)
    {
        var resp = await client.GetAsync("/signin");
        var html = await resp.Content.ReadAsStringAsync();
        var cookies = resp.Headers.TryGetValues("Set-Cookie", out var c) ? c : [];
        return (AntiforgeryHtml.ExtractToken(html), cookies);
    }

    private static FormUrlEncodedContent SignInForm(string token, string apiKey, string? returnUrl = null) =>
        new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["ApiKey"] = apiKey,
            ["returnUrl"] = returnUrl ?? "",
        });

    [Fact]
    public async Task AnonymousGet_OfDashboard_RedirectsToSignIn()
    {
        using var app = new PortalApp();
        using var client = app.Client();

        var resp = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("/signin", resp.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("/review")]
    [InlineData("/enforcement")]
    [InlineData("/whitelist")]
    public async Task AnonymousGet_OfProtectedPages_RedirectsToSignIn(string path)
    {
        using var app = new PortalApp();
        using var client = app.Client();

        var resp = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("/signin", resp.Headers.Location!.ToString());
    }

    [Fact]
    public async Task SignIn_ValidKey_SetsSessionCookie_AndRedirectsToLocalReturnUrl()
    {
        using var app = new PortalApp();
        using var client = app.Client();
        var (token, _) = await GetSignInFormAsync(client);

        var resp = await client.PostAsync("/signin", SignInForm(token, StubAdminApiHandler.ValidKey, "/whitelist"));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/whitelist", resp.Headers.Location!.ToString());
        Assert.Contains(resp.Headers.GetValues("Set-Cookie"), c => c.StartsWith("tg_portal=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SignIn_ValidKey_ExternalReturnUrl_FallsBackToRoot()
    {
        using var app = new PortalApp();
        using var client = app.Client();
        var (token, _) = await GetSignInFormAsync(client);

        var resp = await client.PostAsync(
            "/signin", SignInForm(token, StubAdminApiHandler.ValidKey, "https://evil.example.com/steal"));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/", resp.Headers.Location!.ToString());
    }

    [Fact]
    public async Task SignIn_RejectedKey_SetsNoCookie_AndNeverEchoesTheKey()
    {
        using var app = new PortalApp();
        app.Handler.AcceptedApiKey = StubAdminApiHandler.ValidKey;
        using var client = app.Client();
        var (token, _) = await GetSignInFormAsync(client);

        var resp = await client.PostAsync("/signin", SignInForm(token, "totally-wrong-key"));
        var html = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False(resp.Headers.TryGetValues("Set-Cookie", out var cookies)
            && cookies.Any(c => c.StartsWith("tg_portal=", StringComparison.Ordinal)));
        Assert.Contains("rejected by the admin API", html);
        Assert.DoesNotContain("totally-wrong-key", html);
    }

    [Fact]
    public async Task SignIn_EmptyKey_ShowsPrompt_AndNeverCallsTheApi()
    {
        using var app = new PortalApp();
        using var client = app.Client();
        var (token, _) = await GetSignInFormAsync(client);

        var resp = await client.PostAsync("/signin", SignInForm(token, ""));
        var html = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("Enter your admin API key.", html);
        Assert.Empty(app.Handler.Calls);
    }

    [Fact]
    public async Task SignIn_ThrottleTripsAfterConfiguredAttempts_AndStopsCallingTheApi()
    {
        using var app = new PortalApp(); // configured with SignInAttemptsPerIpPer5Min=3
        using var client = app.Client();

        for (var i = 0; i < 3; i++)
        {
            var (token, _) = await GetSignInFormAsync(client);
            await client.PostAsync("/signin", SignInForm(token, "wrong-key"));
        }

        Assert.Equal(3, app.Handler.Calls.Count);

        var (token4, _) = await GetSignInFormAsync(client);
        var resp = await client.PostAsync("/signin", SignInForm(token4, "wrong-key"));
        var html = await resp.Content.ReadAsStringAsync();

        Assert.Contains("Too many attempts", html);
        // The 4th attempt never reached the stub — call count unchanged.
        Assert.Equal(3, app.Handler.Calls.Count);
    }

    [Fact]
    public async Task SignedInSession_ReplaysTheSignedInKey_AsXApiKey()
    {
        using var app = new PortalApp();
        using var client = app.Client();
        var (token, _) = await GetSignInFormAsync(client);
        await client.PostAsync("/signin", SignInForm(token, StubAdminApiHandler.ValidKey, "/"));

        await client.GetAsync("/");

        Assert.Contains(app.Handler.Calls, c => c.PathAndQuery.StartsWith("/admin/campaigns", StringComparison.Ordinal)
            && c.ApiKey == StubAdminApiHandler.ValidKey);
    }

    [Fact]
    public void Hint_NeverExposesMoreThanLastFourCharacters()
    {
        Assert.Equal("…6789", PortalAuth.Hint("valid-admin-key-0123456789"));
        Assert.Equal("****", PortalAuth.Hint("abc"));
    }
}
