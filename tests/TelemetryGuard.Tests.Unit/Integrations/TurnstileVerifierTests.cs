using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TelemetryGuard.Integrations.Turnstile;

namespace TelemetryGuard.Tests.Unit.Integrations;

public class TurnstileVerifierTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        public int Calls;
        public List<string> CapturedBodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (request.Content is not null)
                CapturedBodies.Add(await request.Content.ReadAsStringAsync(ct));
            return await responder(request, Calls);
        }
    }

    /// <summary>Handler that never answers within the per-attempt timeout (honors the token).</summary>
    private sealed class DelayingHandler : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static TurnstileVerifier CreateVerifier(HttpMessageHandler handler, TurnstileOptions? opts = null)
    {
        opts ??= new TurnstileOptions { SecretKey = "test-secret" };
        return new TurnstileVerifier(
            new HttpClient(handler), Options.Create(opts), NullLogger<TurnstileVerifier>.Instance);
    }

    [Fact]
    public async Task Success_response_maps_to_successful_result()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Json(
            """{"success":true,"challenge_ts":"2026-08-12T10:00:00.000Z","hostname":"example.com","error-codes":[]}""")));
        var verifier = CreateVerifier(handler);

        var result = await verifier.VerifyAsync("tok-abc", null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.ErrorCodes);
        Assert.Equal("example.com", result.Hostname);
        Assert.Equal(new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero), result.ChallengeTimestamp);
        Assert.False(result.WasUnavailable);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Definitive_failure_surfaces_error_code_verbatim_and_does_not_retry()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Json(
            """{"success":false,"error-codes":["invalid-input-response"]}""")));
        var verifier = CreateVerifier(handler);

        var result = await verifier.VerifyAsync("tok-bad", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(new[] { "invalid-input-response" }, result.ErrorCodes);
        Assert.False(result.WasUnavailable);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Http_500_twice_then_success_retries_to_success_in_three_calls()
    {
        var handler = new StubHandler((_, call) => Task.FromResult(call <= 2
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : Json("""{"success":true,"hostname":"example.com"}""")));
        var verifier = CreateVerifier(handler);

        var result = await verifier.VerifyAsync("tok-abc", null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task Persistent_network_failure_yields_unavailable_after_three_calls()
    {
        var handler = new StubHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused")));
        var verifier = CreateVerifier(handler);

        var result = await verifier.VerifyAsync("tok-abc", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(new[] { TurnstileVerifyResult.UnavailableErrorCode }, result.ErrorCodes);
        Assert.True(result.WasUnavailable);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task Per_attempt_timeout_fires_and_yields_unavailable_instead_of_hanging()
    {
        var handler = new DelayingHandler();
        var verifier = CreateVerifier(handler, new TurnstileOptions
        {
            SecretKey = "test-secret",
            TimeoutSeconds = 1,
            MaxRetries = 0
        });

        var result = await verifier.VerifyAsync("tok-abc", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.WasUnavailable);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Missing_token_short_circuits_without_http_call(string? token)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Json("""{"success":true}""")));
        var verifier = CreateVerifier(handler);

        var result = await verifier.VerifyAsync(token, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(new[] { "missing-input-response" }, result.ErrorCodes);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Empty_secret_key_yields_unavailable_without_http_call()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Json("""{"success":true}""")));
        var verifier = CreateVerifier(handler, new TurnstileOptions { SecretKey = "" });

        var result = await verifier.VerifyAsync("tok-abc", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.WasUnavailable);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Request_body_is_form_urlencoded_with_secret_response_and_remoteip()
    {
        string? contentType = null;
        var handler = new StubHandler((req, _) =>
        {
            contentType = req.Content?.Headers.ContentType?.MediaType;
            return Task.FromResult(Json("""{"success":true}"""));
        });
        var verifier = CreateVerifier(handler);

        await verifier.VerifyAsync("tok-abc", "203.0.113.7", CancellationToken.None);

        Assert.Equal("application/x-www-form-urlencoded", contentType);
        var body = Assert.Single(handler.CapturedBodies);
        Assert.Contains("secret=test-secret", body);
        Assert.Contains("response=tok-abc", body);
        Assert.Contains("remoteip=203.0.113.7", body);
    }

    [Fact]
    public async Task Request_body_omits_remoteip_when_not_supplied()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Json("""{"success":true}""")));
        var verifier = CreateVerifier(handler);

        await verifier.VerifyAsync("tok-abc", null, CancellationToken.None);

        var body = Assert.Single(handler.CapturedBodies);
        Assert.Contains("secret=test-secret", body);
        Assert.Contains("response=tok-abc", body);
        Assert.DoesNotContain("remoteip", body);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_mapping_to_unavailable()
    {
        using var cts = new CancellationTokenSource();
        var handler = new StubHandler((_, _) =>
        {
            cts.Cancel();
            return Task.FromException<HttpResponseMessage>(
                new OperationCanceledException(cts.Token));
        });
        var verifier = CreateVerifier(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => verifier.VerifyAsync("tok-abc", null, cts.Token));
        Assert.Equal(1, handler.Calls);
    }
}
