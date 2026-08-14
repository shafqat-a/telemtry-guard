using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TelemetryGuard.Integrations.Meta;

namespace TelemetryGuard.Tests.Unit.Integrations;

public class MetaMarketingClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        public int Calls;
        public readonly List<HttpRequestMessage> Requests = new();
        public readonly List<string> CapturedBodies = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Requests.Add(request);
            if (request.Content is not null)
                CapturedBodies.Add(await request.Content.ReadAsStringAsync(ct));
            return await responder(request, Calls);
        }
    }

    /// <summary>Captures every formatted log message (and the exception, if any) so
    /// tests can assert the access_token never appears anywhere in a log line.</summary>
    private sealed class CapturingLogger : ILogger<MetaMarketingClient>
    {
        public readonly List<string> Messages = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            Messages.Add(exception is null ? message : $"{message} {exception}");
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static MetaOptions Options(string token = "super-secret-token") => new()
    {
        BaseUrl = "https://graph.facebook.com/v21.0",
        SystemUserToken = token,
        TimeoutSeconds = 5,
    };

    private static (MetaMarketingClient Client, StubHandler Handler, CapturingLogger Logger) Create(
        Func<HttpRequestMessage, int, Task<HttpResponseMessage>> responder, MetaOptions? opts = null)
    {
        var handler = new StubHandler(responder);
        var logger = new CapturingLogger();
        var client = new MetaMarketingClient(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(opts ?? Options()), logger);
        return (client, handler, logger);
    }

    // ====================================================== FindBlockListAsync ===

    [Fact]
    public async Task FindBlockListAsync_ParsesData_ReturnsExactNameMatch()
    {
        var (client, handler, _) = Create((_, _) => Task.FromResult(Json(
            """{"data":[{"id":"111","name":"other list"},{"id":"222","name":"TelemetryGuard exclusions — t1"}],"paging":{}}""")));

        var result = await client.FindBlockListAsync("biz-1", "TelemetryGuard exclusions — t1", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("222", result!.Id);
        Assert.Equal("TelemetryGuard exclusions — t1", result.Name);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task FindBlockListAsync_NoMatch_NoNextPage_ReturnsNull()
    {
        var (client, handler, _) = Create((_, _) => Task.FromResult(Json(
            """{"data":[{"id":"111","name":"unrelated"}],"paging":{}}""")));

        var result = await client.FindBlockListAsync("biz-1", "nope", CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task FindBlockListAsync_FollowsPagingNext_UntilMatchFound()
    {
        var (client, handler, _) = Create((req, call) =>
        {
            if (call == 1)
                return Task.FromResult(Json(
                    """{"data":[{"id":"111","name":"page-one"}],"paging":{"next":"https://graph.facebook.com/v21.0/biz-1/publisher_block_lists?after=cursor1&access_token=super-secret-token"}}"""));
            return Task.FromResult(Json(
                """{"data":[{"id":"222","name":"target"}],"paging":{}}"""));
        });

        var result = await client.FindBlockListAsync("biz-1", "target", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("222", result!.Id);
        Assert.Equal(2, handler.Calls);
        Assert.Equal("https://graph.facebook.com/v21.0/biz-1/publisher_block_lists?after=cursor1&access_token=super-secret-token",
            handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task FindBlockListAsync_ExceedsTenPageBound_ReturnsNullWithoutInfiniteLoop()
    {
        var (client, handler, _) = Create((_, call) => Task.FromResult(Json(
            $$$"""{"data":[{"id":"{{{call}}}","name":"nope"}],"paging":{"next":"https://graph.facebook.com/v21.0/biz-1/publisher_block_lists?after=c{{{call}}}"}}""")));

        var result = await client.FindBlockListAsync("biz-1", "never-found", CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(10, handler.Calls); // bounded to 10 pages
    }

    // =================================================== CreateBlockListAsync ===

    [Fact]
    public async Task CreateBlockListAsync_PostsNameAndPublisherUrls_ReturnsId()
    {
        var (client, handler, _) = Create((_, _) => Task.FromResult(Json("""{"id":"999"}""")));

        var id = await client.CreateBlockListAsync(
            "biz-1", "My List", new[] { "bad-publisher.example", "worse.example" }, CancellationToken.None);

        Assert.Equal("999", id);
        Assert.Equal(1, handler.Calls);
        var body = Assert.Single(handler.CapturedBodies);
        Assert.Contains("name=My", body); // form-urlencoded space -> '+' or '%20', prefix check is enough
        Assert.Contains("publisher_urls=", body);
        Assert.Contains("bad-publisher.example", Uri.UnescapeDataString(body));
        Assert.Contains("worse.example", Uri.UnescapeDataString(body));
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("https://graph.facebook.com/v21.0/biz-1/block_list_drafts", handler.Requests[0].RequestUri!.ToString());
    }

    // ================================================= AddPublisherUrlsAsync ===

    [Fact]
    public async Task AddPublisherUrlsAsync_PostsToBlockListId_Succeeds()
    {
        var (client, handler, _) = Create((_, _) => Task.FromResult(Json("""{"success":true}""")));

        await client.AddPublisherUrlsAsync("999", new[] { "bad-publisher.example" }, CancellationToken.None);

        Assert.Equal(1, handler.Calls);
        Assert.Equal("https://graph.facebook.com/v21.0/999", handler.Requests[0].RequestUri!.ToString());
        var body = Assert.Single(handler.CapturedBodies);
        Assert.Contains("bad-publisher.example", Uri.UnescapeDataString(body));
    }

    // ======================================================== error mapping ===

    [Fact]
    public async Task DefinitiveGraphError_SurfacesAsMetaApiException_NotRetried()
    {
        var (client, handler, _) = Create((_, _) => Task.FromResult(Json(
            """{"error":{"message":"Invalid OAuth access token.","type":"OAuthException","code":190,"error_subcode":463,"fbtrace_id":"AbCdEfGh"}}""",
            HttpStatusCode.BadRequest)));

        var ex = await Assert.ThrowsAsync<MetaApiException>(
            () => client.FindBlockListAsync("biz-1", "x", CancellationToken.None));

        Assert.Equal("Invalid OAuth access token.", ex.Message);
        Assert.Equal("OAuthException", ex.ErrorType);
        Assert.Equal(190, ex.Code);
        Assert.Equal(463, ex.ErrorSubcode);
        Assert.Equal("AbCdEfGh", ex.FbTraceId);
        Assert.False(ex.Retryable);
        Assert.Equal(1, handler.Calls); // definitive — never retried
    }

    [Fact]
    public async Task RateLimitError_Code17_IsRetryable()
    {
        var (client, handler, _) = Create((_, call) => Task.FromResult(call == 1
            ? Json("""{"error":{"message":"rate limited","type":"OAuthException","code":17}}""", HttpStatusCode.BadRequest)
            : Json("""{"id":"999"}""")));

        var id = await client.CreateBlockListAsync("biz-1", "x", new[] { "a.example" }, CancellationToken.None);

        Assert.Equal("999", id);
        Assert.Equal(2, handler.Calls); // one retry
    }

    [Fact]
    public async Task Http500_RetriesOnceThenThrows_WithRetryableTrue()
    {
        var (client, handler, _) = Create((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("upstream error", Encoding.UTF8, "text/plain"),
            }));

        var ex = await Assert.ThrowsAsync<MetaApiException>(
            () => client.FindBlockListAsync("biz-1", "x", CancellationToken.None));

        Assert.True(ex.Retryable);
        Assert.Equal(2, handler.Calls); // one retry, per spec — no Polly
    }

    [Fact]
    public async Task UnparseableErrorBody_TruncatedTo500Chars_RetryableFromStatus()
    {
        var longBody = new string('x', 900);
        var (client, handler, _) = Create((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(longBody, Encoding.UTF8, "text/plain"),
            }));

        var ex = await Assert.ThrowsAsync<MetaApiException>(
            () => client.FindBlockListAsync("biz-1", "x", CancellationToken.None));

        Assert.Equal(500, ex.Message.Length);
        Assert.False(ex.Retryable); // 400 is definitive, not >= 500
        Assert.Equal(1, handler.Calls);
    }

    // ================================================= access_token never logged ===

    [Fact]
    public async Task AccessToken_NeverAppearsInLogs_OnSuccess()
    {
        var (client, handler, logger) = Create((_, _) => Task.FromResult(Json(
            """{"data":[{"id":"1","name":"x"}],"paging":{}}""")), Options("top-secret-value-12345"));

        await client.FindBlockListAsync("biz-1", "y", CancellationToken.None);

        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain(logger.Messages, m => m.Contains("top-secret-value-12345"));
    }

    [Fact]
    public async Task AccessToken_NeverAppearsInLogs_OnRetryAndFailure()
    {
        var (client, _, logger) = Create((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("boom", Encoding.UTF8, "text/plain"),
            }), Options("top-secret-value-98765"));

        await Assert.ThrowsAsync<MetaApiException>(
            () => client.CreateBlockListAsync("biz-1", "x", new[] { "a.example" }, CancellationToken.None));

        Assert.NotEmpty(logger.Messages); // retry warning was logged
        Assert.DoesNotContain(logger.Messages, m => m.Contains("top-secret-value-98765"));
    }
}
