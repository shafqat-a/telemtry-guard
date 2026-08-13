using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using StackExchange.Redis;
using TelemetryGuard.Api.Services;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Data.Tenancy;
using TelemetryGuard.Integrations.Turnstile;
using TelemetryGuard.RiskEngine.Contracts;
using TelemetryGuard.RiskEngine.Pipeline;

namespace TelemetryGuard.Tests.Unit.Api;

/// <summary>
/// API-05 /decide endpoint tests over the real Program composition via
/// WebApplicationFactory: band mapping from CONFIGURED thresholds, unknown-session
/// handling, the Turnstile challenge round-trip (re-score with the challenge
/// outcome, call ordering, fail-closed block), finalize-exactly-once with the SAME
/// ScoringOutcome instance, grace-entry cleanup, request validation (malformed
/// JSON / bad sid / k mismatch / unknown site key), CORS, and the
/// text/plain-vs-application/json equivalence the SDK relies on.
///
/// IScoringPipeline/ITurnstileVerifier/IVerdictFinalizer/ITenantResolver are all
/// hand-rolled fakes; Redis is an NSubstitute IConnectionMultiplexer/IDatabase
/// capture harness (same pattern as API-02/03/04's tests).
/// </summary>
public sealed class DecisionEndpointTests
{
    private static readonly Guid TenantGuid = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");
    private static readonly string Tid = TenantGuid.ToString("D");
    private const string SiteKey = "site-1";
    private const string WidgetSiteKey = "0x4AAAAAAA-test-site-key";
    private const string Sid = "0123456789abcdef0123456789abcdef";

    // ---------------------------------------------------------------- fakes --

    private sealed class FakeResolver : ITenantResolver
    {
        public Task<ResolvedTenant?> ResolveApiKeyAsync(string apiKey, CancellationToken ct)
            => Task.FromResult<ResolvedTenant?>(null);

        public Task<ResolvedTenant?> ResolveSiteKeyAsync(string siteKey, CancellationToken ct)
            => Task.FromResult(siteKey == SiteKey
                ? new ResolvedTenant(TenantGuid, [], SiteKey, "js")
                : (ResolvedTenant?)null);
    }

    /// <summary>Scripts ScoreSessionAsync by the ChallengeOutcome it's called with
    /// (NotChallenged for the first call, Passed/Failed for a re-score) and
    /// captures every call (session id + outcome) in order, so tests can assert
    /// the initial score is never preceded by a challenge-outcome write and the
    /// re-score always follows Turnstile verification.</summary>
    private sealed class FakePipeline : IScoringPipeline
    {
        public readonly List<(string SessionId, ChallengeOutcome Outcome)> Calls = [];
        public Func<string, ChallengeOutcome, ScoringOutcome?> Respond = (_, _) => null;

        public Task<ScoringOutcome?> ScoreSessionAsync(
            string sessionId, ChallengeOutcome outcome = ChallengeOutcome.NotChallenged, CancellationToken ct = default)
        {
            lock (Calls) Calls.Add((sessionId, outcome));
            return Task.FromResult(Respond(sessionId, outcome));
        }
    }

    private sealed class FakeTurnstileVerifier : ITurnstileVerifier
    {
        public bool NextSuccess = true;
        public int Calls;
        public string? LastToken;

        public Task<TurnstileVerifyResult> VerifyAsync(string? token, string? remoteIp, CancellationToken ct)
        {
            Calls++;
            LastToken = token;
            return Task.FromResult(NextSuccess
                ? new TurnstileVerifyResult(true, Array.Empty<string>())
                : new TurnstileVerifyResult(false, new[] { "invalid-input-response" }));
        }
    }

    private sealed class FakeFinalizer : IVerdictFinalizer
    {
        public readonly List<(TenantId TenantId, string SessionId, FinalizeTrigger Trigger, ScoringOutcome? Outcome)> Calls = [];
        public Exception? ThrowOnCall;

        public Task FinalizeAsync(TenantId tenantId, string sessionId, FinalizeTrigger trigger,
            ScoringOutcome? precomputed, CancellationToken ct)
        {
            lock (Calls) Calls.Add((tenantId, sessionId, trigger, precomputed));
            if (ThrowOnCall is not null) throw ThrowOnCall;
            return Task.CompletedTask;
        }
    }

    /// <summary>NSubstitute capture harness for the grace-entry ZREM the endpoint
    /// issues directly (belt-and-braces alongside the finalizer's own removal).</summary>
    private sealed class RedisHarness
    {
        public readonly IConnectionMultiplexer Mux;
        public readonly List<(string Key, string Member)> SortedSetRemoves = [];

        public RedisHarness()
        {
            Mux = Substitute.For<IConnectionMultiplexer>();
            var db = Substitute.For<IDatabase>();
            Mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

            db.SortedSetRemoveAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>())
              .Returns(true)
              .AndDoes(ci =>
              {
                  lock (SortedSetRemoves)
                      SortedSetRemoves.Add((ci.ArgAt<RedisKey>(0).ToString(), ci.ArgAt<RedisValue>(1).ToString()));
              });
        }
    }

    // ------------------------------------------------------------- harness --

    private static ScoringOutcome Outcome(int score, bool whitelisted = false)
        => new(new ScoreResult(score, Array.Empty<string>(), "test-scorer", 1),
               BandMapper.ToBand(score), whitelisted, 1.0);

    private sealed class DecideApp : IDisposable
    {
        public readonly WebApplicationFactory<Program> Factory;
        public readonly FakePipeline Pipeline = new();
        public readonly FakeTurnstileVerifier Turnstile = new();
        public readonly FakeFinalizer Finalizer = new();
        public readonly RedisHarness Redis = new();

        public DecideApp(Dictionary<string, string?>? settings = null)
        {
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                var overrides = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Main"] =
                        "Server=localhost,1;Database=TelemetryGuard;User Id=sa;Password=x;" +
                        "TrustServerCertificate=true;Connect Timeout=1;ConnectRetryCount=0",
                    ["ConnectionStrings:Redis"] = "localhost:1,connectTimeout=250,abortConnect=false",
                    ["Analytics:ClickHouse:ConnectionString"] = "Host=localhost;Port=1;Database=telemetry_guard",
                    ["Turnstile:SiteKey"] = WidgetSiteKey,
                };
                if (settings is not null)
                    foreach (var (k, v) in settings) overrides[k] = v;
                foreach (var (k, v) in overrides)
                    b.UseSetting(k, v);
                b.ConfigureTestServices(services =>
                {
                    services.RemoveAll<ITenantResolver>();
                    services.AddSingleton<ITenantResolver>(new FakeResolver());
                    services.RemoveAll<IScoringPipeline>();
                    services.AddSingleton<IScoringPipeline>(Pipeline);
                    services.RemoveAll<ITurnstileVerifier>();
                    services.AddSingleton<ITurnstileVerifier>(Turnstile);
                    services.RemoveAll<IVerdictFinalizer>();
                    services.AddSingleton<IVerdictFinalizer>(Finalizer);
                    services.RemoveAll<IConnectionMultiplexer>();
                    services.AddSingleton(Redis.Mux);
                });
            });
        }

        public HttpClient Client() => Factory.CreateClient();
        public void Dispose() => Factory.Dispose();
    }

    private static HttpRequestMessage Post(
        string path, string body, string contentType = "application/json", string? origin = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };
        if (origin is not null) req.Headers.TryAddWithoutValidation("Origin", origin);
        return req;
    }

    private static string Body(string sid, string? token = null, string? k = null)
    {
        var fields = new List<string> { $"\"sid\":\"{sid}\"" };
        if (token is not null) fields.Add($"\"turnstileToken\":\"{token}\"");
        if (k is not null) fields.Add($"\"k\":\"{k}\"");
        return "{" + string.Join(",", fields) + "}";
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage resp)
        => JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    // --------------------------------------------------------------- tests --

    [Theory]
    [InlineData(10, "allow")]
    [InlineData(50, "challenge")]
    [InlineData(90, "block")]
    public async Task ScoreBands_MapToTheCorrectAction(int score, string expectedAction)
    {
        using var app = new DecideApp();
        app.Pipeline.Respond = (_, _) => Outcome(score);
        using var client = app.Client();

        var resp = await client.SendAsync(Post($"/decide?k={SiteKey}", Body(Sid)));
        var raw = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = JsonSerializer.Deserialize<JsonElement>(raw);
        Assert.Equal(expectedAction, json.GetProperty("action").GetString());
        // No response ever exposes scoring internals.
        Assert.DoesNotContain("score", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"band\"", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rule", raw, StringComparison.OrdinalIgnoreCase);

        if (expectedAction == "challenge")
            Assert.Equal(WidgetSiteKey, json.GetProperty("turnstileSiteKey").GetString());
    }

    [Fact]
    public async Task UnknownSession_NoToken_ReturnsChallenge_AndNeverFinalizes()
    {
        using var app = new DecideApp();
        app.Pipeline.Respond = (_, _) => null;
        using var client = app.Client();

        var resp = await client.SendAsync(Post($"/decide?k={SiteKey}", Body(Sid)));
        var json = await ReadJson(resp);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("challenge", json.GetProperty("action").GetString());
        Assert.Equal(WidgetSiteKey, json.GetProperty("turnstileSiteKey").GetString());
        Assert.Empty(app.Finalizer.Calls);
    }

    [Fact]
    public async Task UnknownSession_WithToken_VerifierSuccess_ReturnsAllow_NeverFinalizes()
    {
        using var app = new DecideApp();
        app.Pipeline.Respond = (_, _) => null;
        app.Turnstile.NextSuccess = true;
        using var client = app.Client();

        var resp = await client.SendAsync(Post($"/decide?k={SiteKey}", Body(Sid, token: "tok-1")));
        var json = await ReadJson(resp);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("allow", json.GetProperty("action").GetString());
        Assert.Empty(app.Finalizer.Calls);
    }

    [Fact]
    public async Task UnknownSession_WithToken_VerifierFailure_ReturnsBlock_NeverFinalizes()
    {
        using var app = new DecideApp();
        app.Pipeline.Respond = (_, _) => null;
        app.Turnstile.NextSuccess = false;
        using var client = app.Client();

        var resp = await client.SendAsync(Post($"/decide?k={SiteKey}", Body(Sid, token: "tok-1")));
        var json = await ReadJson(resp);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("block", json.GetProperty("action").GetString());
        Assert.Empty(app.Finalizer.Calls);
    }

    [Fact]
    public async Task ChallengeBand_TokenPresent_VerifierSuccess_Rescore20_ReturnsAllow()
    {
        using var app = new DecideApp();
        var initial = Outcome(50);
        var rescored = Outcome(20);
        app.Pipeline.Respond = (_, outcome) => outcome == ChallengeOutcome.NotChallenged ? initial : rescored;
        app.Turnstile.NextSuccess = true;
        using var client = app.Client();

        var resp = await client.SendAsync(Post($"/decide?k={SiteKey}", Body(Sid, token: "tok-1")));
        var json = await ReadJson(resp);

        Assert.Equal("allow", json.GetProperty("action").GetString());

        // Exactly two pipeline calls: first NotChallenged (default), second Passed —
        // the re-score outcome write is inherent to the ordering (verify happens,
        // THEN the re-score call carries its result), never before the first call.
        Assert.Equal(2, app.Pipeline.Calls.Count);
        Assert.Equal(ChallengeOutcome.NotChallenged, app.Pipeline.Calls[0].Outcome);
        Assert.Equal(ChallengeOutcome.Passed, app.Pipeline.Calls[1].Outcome);

        // Finalized exactly once with the SAME ScoringOutcome instance the
        // re-score produced (no double scoring, no re-wrapping).
        var call = Assert.Single(app.Finalizer.Calls);
        Assert.Same(rescored, call.Outcome);
        Assert.Equal(FinalizeTrigger.Decide, call.Trigger);
        Assert.Equal(Sid, call.SessionId);

        var zrem = Assert.Single(app.Redis.SortedSetRemoves);
        Assert.Equal($"t:{Tid}:grace", zrem.Key);
        Assert.Equal(Sid, zrem.Member);
    }

    [Fact]
    public async Task ChallengeBand_TokenPresent_VerifierSuccess_Rescore85_ReturnsBlock()
    {
        using var app = new DecideApp();
        var initial = Outcome(50);
        var rescored = Outcome(85);
        app.Pipeline.Respond = (_, outcome) => outcome == ChallengeOutcome.NotChallenged ? initial : rescored;
        app.Turnstile.NextSuccess = true;
        using var client = app.Client();

        var resp = await client.SendAsync(Post($"/decide?k={SiteKey}", Body(Sid, token: "tok-1")));
        var json = await ReadJson(resp);

        Assert.Equal("block", json.GetProperty("action").GetString()); // solved Turnstile doesn't wash out T1 evidence
        var call = Assert.Single(app.Finalizer.Calls);
        Assert.Same(rescored, call.Outcome);
    }

    [Fact]
    public async Task ChallengeBand_TokenPresent_VerifierFailure_ReturnsBlock_AndRescoresWithFailedOutcome()
    {
        using var app = new DecideApp();
        var initial = Outcome(50);
        var rescored = Outcome(45); // even a low re-score still blocks: fail-closed on a failed challenge
        app.Pipeline.Respond = (_, outcome) => outcome == ChallengeOutcome.NotChallenged ? initial : rescored;
        app.Turnstile.NextSuccess = false;
        using var client = app.Client();

        var resp = await client.SendAsync(Post($"/decide?k={SiteKey}", Body(Sid, token: "tok-1")));
        var json = await ReadJson(resp);

        Assert.Equal("block", json.GetProperty("action").GetString());
        Assert.Equal(2, app.Pipeline.Calls.Count);
        Assert.Equal(ChallengeOutcome.NotChallenged, app.Pipeline.Calls[0].Outcome);
        Assert.Equal(ChallengeOutcome.Failed, app.Pipeline.Calls[1].Outcome); // verifier result recorded before the re-score returns
        var call = Assert.Single(app.Finalizer.Calls);
        Assert.Same(rescored, call.Outcome);
    }

    [Fact]
    public async Task ChallengeBand_NoToken_DoesNotFinalize()
    {
        using var app = new DecideApp();
        app.Pipeline.Respond = (_, _) => Outcome(50);
        using var client = app.Client();

        var resp = await client.SendAsync(Post($"/decide?k={SiteKey}", Body(Sid)));
        var json = await ReadJson(resp);

        Assert.Equal("challenge", json.GetProperty("action").GetString());
        Assert.Empty(app.Finalizer.Calls);
        Assert.Empty(app.Redis.SortedSetRemoves);
        Assert.Equal(0, app.Turnstile.Calls); // no token -> no verification attempt
    }

    [Fact]
    public async Task AllowAndBlock_FinalizeExactlyOnce_WithTheSameOutcomeInstance()
    {
        using var app = new DecideApp();
        var outcome = Outcome(10);
        app.Pipeline.Respond = (_, _) => outcome;
        using var client = app.Client();

        var resp = await client.SendAsync(Post($"/decide?k={SiteKey}", Body(Sid)));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var call = Assert.Single(app.Finalizer.Calls);
        Assert.Same(outcome, call.Outcome);
        Assert.Equal(new TenantId(TenantGuid), call.TenantId);
        var zrem = Assert.Single(app.Redis.SortedSetRemoves);
        Assert.Equal($"t:{Tid}:grace", zrem.Key);
        Assert.Equal(Sid, zrem.Member);
    }

    [Fact]
    public async Task FinalizerThrows_DecisionStillReturned200()
    {
        using var app = new DecideApp();
        app.Pipeline.Respond = (_, _) => Outcome(10);
        app.Finalizer.ThrowOnCall = new InvalidOperationException("boom");
        using var client = app.Client();

        var resp = await client.SendAsync(Post($"/decide?k={SiteKey}", Body(Sid)));
        var json = await ReadJson(resp);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("allow", json.GetProperty("action").GetString());
        // The endpoint's own belt-and-braces ZREM still runs even though the
        // finalizer blew up before reaching its internal removal.
        Assert.Single(app.Redis.SortedSetRemoves);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"sid\": }")]
    public async Task MalformedJson_Returns400ProblemJson(string body)
    {
        using var app = new DecideApp();
        using var client = app.Client();

        var resp = await client.SendAsync(Post($"/decide?k={SiteKey}", body));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("short")]           // < 8 chars
    [InlineData("has a space in it")]
    [InlineData("")]
    public async Task InvalidSid_Returns400ProblemJson(string badSid)
    {
        using var app = new DecideApp();
        using var client = app.Client();

        var resp = await client.SendAsync(Post($"/decide?k={SiteKey}", Body(badSid)));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task UuidShapedSid_IsValid()
    {
        using var app = new DecideApp();
        app.Pipeline.Respond = (_, _) => Outcome(10);
        using var client = app.Client();
        var uuidSid = Guid.NewGuid().ToString(); // 36 chars incl. dashes — SDK-02's crypto.randomUUID() fallback

        var resp = await client.SendAsync(Post($"/decide?k={SiteKey}", Body(uuidSid)));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task K_FromBodyOnly_ResolvesTheTenant()
    {
        // "k may also appear in the body" — the query string is entirely absent here.
        using var app = new DecideApp();
        app.Pipeline.Respond = (_, _) => Outcome(10);
        using var client = app.Client();

        var resp = await client.SendAsync(Post("/decide", Body(Sid, k: SiteKey)));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task BodyKMismatchesQueryK_Returns400ProblemJson()
    {
        using var app = new DecideApp();
        using var client = app.Client();

        var resp = await client.SendAsync(Post($"/decide?k={SiteKey}", Body(Sid, k: "some-other-key")));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task BodyKMatchingQueryK_IsAccepted()
    {
        using var app = new DecideApp();
        app.Pipeline.Respond = (_, _) => Outcome(10);
        using var client = app.Client();

        var resp = await client.SendAsync(Post($"/decide?k={SiteKey}", Body(Sid, k: SiteKey)));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task UnknownSiteKey_Returns404Problem()
    {
        using var app = new DecideApp();
        using var client = app.Client();

        var resp = await client.SendAsync(Post("/decide?k=who-dis", Body(Sid)));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task TextPlainContentType_BehavesIdenticallyToApplicationJson()
    {
        using var app = new DecideApp();
        app.Pipeline.Respond = (_, _) => Outcome(10);
        using var client = app.Client();

        var jsonResp = await client.SendAsync(Post($"/decide?k={SiteKey}", Body(Sid), "application/json"));
        var textResp = await client.SendAsync(Post($"/decide?k={SiteKey}", Body(Sid), "text/plain"));

        Assert.Equal(HttpStatusCode.OK, jsonResp.StatusCode);
        Assert.Equal(HttpStatusCode.OK, textResp.StatusCode);
        Assert.Equal(await jsonResp.Content.ReadAsStringAsync(), await textResp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Origin_ProducesWildcardCorsHeaders_OnSuccess()
    {
        using var app = new DecideApp();
        app.Pipeline.Respond = (_, _) => Outcome(10);
        using var client = app.Client();

        var resp = await client.SendAsync(
            Post($"/decide?k={SiteKey}", Body(Sid), origin: "https://tenant-site.example"));

        Assert.Equal("*", Assert.Single(resp.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains("Origin", resp.Headers.GetValues("Vary"));
    }

    [Fact]
    public async Task Origin_ProducesWildcardCorsHeaders_OnValidationError()
    {
        using var app = new DecideApp();
        using var client = app.Client();

        var resp = await client.SendAsync(
            Post($"/decide?k={SiteKey}", "not json", origin: "https://tenant-site.example"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("*", Assert.Single(resp.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task Options_ReturnsPreflightHeaders()
    {
        using var app = new DecideApp();
        using var client = app.Client();

        var req = new HttpRequestMessage(HttpMethod.Options, "/decide");
        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Equal("*", Assert.Single(resp.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal("POST", Assert.Single(resp.Headers.GetValues("Access-Control-Allow-Methods")));
        Assert.Equal("Content-Type", Assert.Single(resp.Headers.GetValues("Access-Control-Allow-Headers")));
        Assert.Equal("86400", Assert.Single(resp.Headers.GetValues("Access-Control-Max-Age")));
    }

    [Fact]
    public async Task ScoringBandsOverride_ChangesTheAction()
    {
        // Default AllowMax=30 would allow a score of 10; overriding it to 5
        // must push the same score into the challenge band — proves the
        // endpoint reads Scoring:Bands from configuration, not a hardcoded value.
        using var app = new DecideApp(settings: new() { ["Scoring:Bands:AllowMax"] = "5" });
        app.Pipeline.Respond = (_, _) => Outcome(10); // RSK-07's own Band is still Allow (hardcoded 0..30)
        using var client = app.Client();

        var resp = await client.SendAsync(Post($"/decide?k={SiteKey}", Body(Sid)));
        var json = await ReadJson(resp);

        Assert.Equal("challenge", json.GetProperty("action").GetString());
    }

    [Fact]
    public async Task WhitelistedOutcome_FollowsTheAllowPath()
    {
        using var app = new DecideApp();
        app.Pipeline.Respond = (_, _) => Outcome(0, whitelisted: true);
        using var client = app.Client();

        var resp = await client.SendAsync(Post($"/decide?k={SiteKey}", Body(Sid)));
        var json = await ReadJson(resp);

        Assert.Equal("allow", json.GetProperty("action").GetString());
    }
}
