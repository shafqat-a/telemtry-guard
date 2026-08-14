using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TelemetryGuard.Portal;
using TelemetryGuard.Portal.Api;

namespace TelemetryGuard.Tests.Unit.Portal;

/// <summary>
/// P2-03 portal test support: a stub replacement for the admin API's HTTP transport
/// (StubAdminApiHandler), and a WebApplicationFactory wrapper (PortalApp) that wires
/// it in exactly the way the task spec calls for — re-registering the typed
/// IAdminApiClient/PortalApiClient client so the later ConfigureTestServices action
/// wins and the stub handler replaces the real transport.
/// </summary>
public sealed class StubAdminApiHandler : HttpMessageHandler
{
    public const string ValidKey = "valid-admin-key-0123456789";

    public sealed record Call(HttpMethod Method, string PathAndQuery, string? ApiKey, string? Body);

    public readonly List<Call> Calls = [];

    /// <summary>The one API key this stub accepts. Tests can null it out (nothing
    /// accepted -> every call 401s, simulating a revoked key mid-session) or point
    /// it at a different value.</summary>
    public string? AcceptedApiKey { get; set; } = ValidKey;

    /// <summary>When true, every SendAsync throws HttpRequestException — simulates
    /// the admin API being unreachable.</summary>
    public bool SimulateUnreachable { get; set; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Per-path response overrides, keyed by (Method, AbsolutePath). When
    /// absent, a route falls back to a generic 200-with-empty-body response as long
    /// as the API key is accepted, matching the shape callers expect for that path.</summary>
    private readonly Dictionary<(HttpMethod, string), Func<HttpRequestMessage, string?, HttpResponseMessage>> _routes = new();

    public void OnRoute(HttpMethod method, string path, Func<HttpRequestMessage, string?, HttpResponseMessage> respond)
        => _routes[(method, path)] = respond;

    public void OnRouteJson<T>(HttpMethod method, string path, T value, HttpStatusCode status = HttpStatusCode.OK)
        => OnRoute(method, path, (_, _) => JsonResponse(status, value));

    public static HttpResponseMessage JsonResponse<T>(HttpStatusCode status, T value) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json"),
    };

    public static HttpResponseMessage ValidationProblem(string field, string message) => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new { title = "One or more validation errors occurred.", status = 400, errors = new Dictionary<string, string[]> { [field] = [message] } }),
            Encoding.UTF8, "application/problem+json"),
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (SimulateUnreachable)
            throw new HttpRequestException("stub: admin API unreachable");

        var apiKey = request.Headers.TryGetValues("X-Api-Key", out var values) ? values.FirstOrDefault() : null;
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Calls.Add(new Call(request.Method, request.RequestUri!.PathAndQuery, apiKey, body));

        if (AcceptedApiKey is null || !string.Equals(apiKey, AcceptedApiKey, StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"title":"Unauthorized"}""", Encoding.UTF8, "application/problem+json"),
            };
        }

        if (_routes.TryGetValue((request.Method, request.RequestUri.AbsolutePath), out var handler))
            return handler(request, body);

        // Default: an empty-but-well-shaped 200 keeps unrelated calls from 500ing a
        // page under test that doesn't care about that particular panel.
        return request.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : JsonResponse(HttpStatusCode.OK, new { });
    }
}

/// <summary>WebApplicationFactory&lt;PortalEntryPoint&gt; wrapper wiring the stub
/// transport in place of the real one, per the task's testing section.</summary>
public sealed class PortalApp : IDisposable
{
    public readonly WebApplicationFactory<PortalEntryPoint> Factory;
    public readonly StubAdminApiHandler Handler = new();

    public PortalApp()
    {
        RegisterRealisticDefaults(Handler);

        Factory = new WebApplicationFactory<PortalEntryPoint>().WithWebHostBuilder(b =>
        {
            b.UseSolutionRelativeContentRoot("TelemetryGuard.Portal");
            b.UseSetting("Portal:ApiBaseUrl", "http://admin.test");
            b.UseSetting("Portal:SignInAttemptsPerIpPer5Min", "3");
            b.ConfigureTestServices(services =>
            {
                services.AddHttpClient<IAdminApiClient, PortalApiClient>(c => c.BaseAddress = new Uri("http://admin.test"))
                    .ConfigurePrimaryHttpMessageHandler(() => Handler);
            });
        });
    }

    /// <summary>Cookies are handled automatically; redirects are NOT followed so
    /// tests can assert on 302 Location / Set-Cookie directly.</summary>
    public HttpClient Client() => Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>Convenience for tests that only care about post-sign-in behavior:
    /// completes the GET /signin -> POST /signin round trip (antiforgery token +
    /// cookie) and returns a client already carrying the tg_portal session cookie.</summary>
    public async Task<HttpClient> SignedInClientAsync(string apiKey = StubAdminApiHandler.ValidKey)
    {
        var client = Client();
        var getResp = await client.GetAsync("/signin");
        var token = AntiforgeryHtml.ExtractToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["ApiKey"] = apiKey,
            ["returnUrl"] = "",
        });
        var postResp = await client.PostAsync("/signin", form);
        if (postResp.StatusCode != HttpStatusCode.Redirect)
            throw new InvalidOperationException($"Sign-in did not redirect (status {postResp.StatusCode}) — check the stub's AcceptedApiKey.");

        return client;
    }

    public void Dispose() => Factory.Dispose();

    /// <summary>Every panel-backing route gets an empty-but-well-shaped default so a
    /// test that only cares about one panel doesn't 500 the whole page via a null
    /// collection from the generic {} fallback (System.Text.Json leaves a missing
    /// non-nullable reference-type record property at its type default — null — not
    /// an empty collection). Tests override individual routes with OnRouteJson/OnRoute
    /// as needed.</summary>
    private static void RegisterRealisticDefaults(StubAdminApiHandler handler)
    {
        handler.OnRouteJson(HttpMethod.Get, "/admin/campaigns", new CampaignListDto([]));
        handler.OnRouteJson(HttpMethod.Get, "/admin/reports/summary",
            new SummaryReportDto(default, default, Guid.Empty, []));
        handler.OnRouteJson(HttpMethod.Get, "/admin/sites/integration-status",
            new IntegrationStatusReportDto([]));
        handler.OnRouteJson(HttpMethod.Get, "/admin/reports/flagged-sources",
            new FlaggedSourcesReportDto(default, default, []));
        handler.OnRouteJson(HttpMethod.Get, "/admin/whitelist", new List<WhitelistEntryDto>());
        handler.OnRouteJson(HttpMethod.Get, "/admin/enforcement", new List<ExclusionQueueEntryDto>());
        handler.OnRouteJson(HttpMethod.Post, "/admin/whitelist", new AddWhitelistResponseDto(1), HttpStatusCode.Created);
        handler.OnRouteJson(HttpMethod.Post, "/admin/enforcement/approve", new EnforcementApproveResponseDto(0, 0));
        handler.OnRouteJson(HttpMethod.Post, "/admin/enforcement/reject", new EnforcementRejectResponseDto(0, 0));
    }
}

public static class AntiforgeryHtml
{
    private static readonly Regex TokenPattern = new(
        """name="__RequestVerificationToken"[^>]*value="([^"]+)""",
        RegexOptions.Compiled);

    public static string ExtractToken(string html)
    {
        var match = TokenPattern.Match(html);
        if (!match.Success)
            throw new InvalidOperationException("No __RequestVerificationToken found in the rendered HTML.");
        return match.Groups[1].Value;
    }
}
