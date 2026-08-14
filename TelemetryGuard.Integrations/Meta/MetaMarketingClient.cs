using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TelemetryGuard.Integrations.Meta;

/// <summary>
/// Typed-HttpClient implementation of <see cref="IMetaMarketingClient"/> against the
/// Meta Marketing API (spec D15) — no official .NET SDK exists, so this is a thin,
/// deliberately small wrapper. Registered via
/// <see cref="MetaServiceCollectionExtensions.AddMetaMarketingClient"/>.
///
/// All paths are built from <see cref="MetaOptions.BaseUrl"/> + <see cref="Edges"/> —
/// the single place to fix if a pinned Graph API version renames an edge. The
/// system-user token travels as an <c>access_token</c> query parameter on GET calls
/// and as a form field on POST calls; it is NEVER written to a log — every log
/// statement below logs the edge path only, never the built URL/form body.
/// </summary>
/// <remarks>
/// MANUAL SMOKE PROCEDURE (never automated — no live Meta calls in CI or any test):
/// against a Meta test Business (Business Settings → System Users → generate a
/// token scoped to a sandbox Business Manager), 1) fill in a real SystemUserToken
/// in a local, gitignored appsettings override; 2) set Meta:DryRun=false; 3) seed
/// one dbo.Tenants row with that test Business's id in MetaBusinessId, and one
/// approved dbo.ExclusionQueue row (Platform='meta', SourceType='placement',
/// Value=a publisher domain); 4) run the API host and watch the
/// MetaExclusionSyncService logs plus the test Business's Publisher Block Lists
/// (Business Settings → Brand Safety) in Meta Business Manager. Never point this
/// at a production Business.
/// </remarks>
public sealed class MetaMarketingClient(
    HttpClient http, IOptions<MetaOptions> options, ILogger<MetaMarketingClient> logger)
    : IMetaMarketingClient
{
    /// <summary>Graph edge names, isolated here per the spec instruction: Meta renames
    /// edges between versions — verify these against the pinned version's changelog and
    /// adjust ONLY these constants, never a call-site string.</summary>
    private static class Edges
    {
        public const string PublisherBlockLists = "publisher_block_lists";
        public const string BlockListDrafts = "block_list_drafts";
    }

    private const int MaxFindPages = 10;
    private const int MaxAttempts = 2; // one retry after 500 ms, per spec — no Polly
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<MetaBlockList?> FindBlockListAsync(string businessId, string name, CancellationToken ct)
    {
        var path = $"{businessId}/{Edges.PublisherBlockLists}";
        var url = $"{options.Value.BaseUrl}/{path}?fields=id,name&access_token={Uri.EscapeDataString(options.Value.SystemUserToken)}";

        for (var page = 1; page <= MaxFindPages; page++)
        {
            using var response = await SendWithRetryAsync(
                () => new HttpRequestMessage(HttpMethod.Get, url), path, ct).ConfigureAwait(false);
            var payload = await ReadJsonAsync<ListResponse>(response, ct).ConfigureAwait(false);

            var match = payload.Data?.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.Ordinal));
            if (match is not null) return new MetaBlockList(match.Id, match.Name);

            if (string.IsNullOrEmpty(payload.Paging?.Next)) return null;
            url = payload.Paging.Next; // Graph pagination URLs are already absolute (incl. cursor + token)
        }

        logger.LogWarning(
            "MetaMarketingClient: FindBlockListAsync exceeded the {MaxPages}-page bound for {Path} without a match",
            MaxFindPages, path);
        return null;
    }

    public async Task<string> CreateBlockListAsync(
        string businessId, string name, IReadOnlyList<string> publisherUrls, CancellationToken ct)
    {
        var path = $"{businessId}/{Edges.BlockListDrafts}";
        var url = $"{options.Value.BaseUrl}/{path}";
        var form = new Dictionary<string, string>
        {
            ["name"] = name,
            ["publisher_urls"] = JsonSerializer.Serialize(publisherUrls),
        };

        using var response = await SendWithRetryAsync(
            () => BuildPostRequest(url, form), path, ct).ConfigureAwait(false);
        var payload = await ReadJsonAsync<CreateResponse>(response, ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(payload.Id))
        {
            throw new MetaApiException(
                "Meta Graph API create response was missing an id.", null, null, null, null, retryable: false);
        }

        return payload.Id;
    }

    public async Task AddPublisherUrlsAsync(
        string blockListId, IReadOnlyList<string> publisherUrls, CancellationToken ct)
    {
        var url = $"{options.Value.BaseUrl}/{blockListId}";
        var form = new Dictionary<string, string> { ["publisher_urls"] = JsonSerializer.Serialize(publisherUrls) };

        // 2xx (typically {"success":true}) is all we need — deliberately not parsed;
        // growing a richer response contract here is exactly the "general request
        // builder" scope creep D15 forbids.
        using var response = await SendWithRetryAsync(
            () => BuildPostRequest(url, form), blockListId, ct).ConfigureAwait(false);
    }

    private HttpRequestMessage BuildPostRequest(string url, Dictionary<string, string> form)
    {
        form["access_token"] = options.Value.SystemUserToken; // body, never the URL — see class doc
        return new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
    }

    /// <summary>Sends the request built by <paramref name="requestFactory"/>, retrying
    /// exactly once after <see cref="RetryDelay"/> when the failure is
    /// <see cref="MetaApiException.Retryable"/> (transient HTTP 5xx/timeout, or Graph
    /// rate limiting). <paramref name="logPath"/> is the edge path ONLY — never the
    /// built URL/query/body, which may carry the access_token.</summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory, string logPath, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            using var request = requestFactory();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // caller cancelled — propagate, don't swallow into a retry
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                if (attempt < MaxAttempts)
                {
                    logger.LogWarning(ex, "MetaMarketingClient: {Path} attempt {Attempt} failed transiently; retrying", logPath, attempt);
                    await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
                    continue;
                }
                throw new MetaApiException(
                    $"Meta Graph API request to {logPath} timed out or failed: {ex.Message}",
                    null, null, null, null, retryable: true);
            }

            if (response.IsSuccessStatusCode) return response;

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var error = ParseError(body, (int)response.StatusCode);
            response.Dispose();

            if (error.Retryable && attempt < MaxAttempts)
            {
                logger.LogWarning(
                    "MetaMarketingClient: {Path} attempt {Attempt} returned a retryable error ({Code}); retrying",
                    logPath, attempt, (int)response.StatusCode);
                await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
                continue;
            }

            throw error;
        }

        throw new MetaApiException($"Meta Graph API request to {logPath} exhausted its retries.", null, null, null, null, retryable: true);
    }

    private static MetaApiException ParseError(string body, int statusCode)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<ErrorEnvelope>(body, JsonOptions);
            if (envelope?.Error is { } err)
            {
                var retryable = statusCode >= 500
                    || err.Code == 17
                    || (err.Code == 4 && err.ErrorSubcode == 2446079);
                return new MetaApiException(
                    err.Message ?? "Meta Graph API returned an error.",
                    err.Type, err.Code, err.ErrorSubcode, err.FbtraceId, retryable);
            }
        }
        catch (JsonException)
        {
            // fall through — unparseable body handled below
        }

        var truncated = body.Length > 500 ? body[..500] : body;
        return new MetaApiException(truncated, null, null, null, null, retryable: statusCode >= 500);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var payload = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct).ConfigureAwait(false);
        if (payload is null)
        {
            throw new MetaApiException(
                "Meta Graph API returned an empty or invalid response body.", null, null, null, null, retryable: false);
        }
        return payload;
    }

    // ---- minimal typed response records (spec D15: nothing beyond what's parsed) ----

    private sealed record ListResponse(
        [property: JsonPropertyName("data")] List<BlockListItem>? Data,
        [property: JsonPropertyName("paging")] PagingInfo? Paging);

    private sealed record BlockListItem(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name);

    private sealed record PagingInfo([property: JsonPropertyName("next")] string? Next);

    private sealed record CreateResponse([property: JsonPropertyName("id")] string Id);

    private sealed record ErrorEnvelope([property: JsonPropertyName("error")] GraphError? Error);

    private sealed record GraphError(
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("code")] int? Code,
        [property: JsonPropertyName("error_subcode")] int? ErrorSubcode,
        [property: JsonPropertyName("fbtrace_id")] string? FbtraceId);
}
