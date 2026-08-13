using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TelemetryGuard.Data.Tenancy;

namespace TelemetryGuard.Tests.Unit.Api;

/// <summary>
/// SDK-08 bundle-delivery tests over the real Program composition via
/// WebApplicationFactory, against a temp wwwroot/sdk fixture ("Sdk:BundleRoot"
/// override): the D22 cache-header matrix (latest vs pinned vs source maps),
/// byte-identity, 404 behavior (missing file, directory URL, traversal, absent
/// bundle directory), and proof that /sdk/* bypasses tenant resolution and the
/// per-tenant rate limiter. No backing stores run here — connection strings
/// point at closed ports with fail-fast timeouts.
/// </summary>
public sealed class SdkDeliveryTests : IDisposable
{
    private const string LatestCacheControl = "public, max-age=300, stale-while-revalidate=60";
    private const string PinnedCacheControl = "public, max-age=31536000, immutable";

    private readonly string _root;   // parent temp dir — holds a decoy secret the mapping must never expose
    private readonly string _sdkDir; // <root>/sdk — the served directory

    public SdkDeliveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tg-sdk08-" + Guid.NewGuid().ToString("N"));
        _sdkDir = Path.Combine(_root, "sdk");
        Directory.CreateDirectory(_sdkDir);

        // Fixture mirrors SDK-01's build output shape (latest + pinned + maps).
        File.WriteAllText(Path.Combine(_sdkDir, "tg.js"),
            "/* TelemetryGuard SDK v0.1.0 */(()=>{/* latest */})();");
        File.WriteAllText(Path.Combine(_sdkDir, "tg.js.map"),
            """{"version":3,"file":"tg.js","sources":[],"mappings":""}""");
        File.WriteAllText(Path.Combine(_sdkDir, "tg-0.1.0.js"),
            "/* TelemetryGuard SDK v0.1.0 */(()=>{/* pinned */})();");
        File.WriteAllText(Path.Combine(_sdkDir, "tg-0.1.0.js.map"),
            """{"version":3,"file":"tg-0.1.0.js","sources":[],"mappings":""}""");
        File.WriteAllText(Path.Combine(_sdkDir, "tg-1.2.3-beta.1.js"),
            "/* prerelease pinned */");

        // Sits one level ABOVE the served directory: reachable only by traversal.
        File.WriteAllText(Path.Combine(_root, "secret.json"), """{"secret":"never-served"}""");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class NoTenantResolver : ITenantResolver
    {
        // /sdk/* must never consult the resolver; anything that does resolves nothing.
        public Task<ResolvedTenant?> ResolveApiKeyAsync(string apiKey, CancellationToken ct)
            => Task.FromResult<ResolvedTenant?>(null);

        public Task<ResolvedTenant?> ResolveSiteKeyAsync(string siteKey, CancellationToken ct)
            => Task.FromResult<ResolvedTenant?>(null);
    }

    private WebApplicationFactory<Program> CreateFactory(
        string? bundleRoot = null,
        Dictionary<string, string?>? settings = null)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            // UseSetting (not ConfigureAppConfiguration): with minimal hosting the
            // ConfigureAppConfiguration callbacks run only AFTER Program.cs has
            // already read builder.Configuration imperatively, while UseSetting
            // values are present from builder creation (same as PipelineTests).
            var overrides = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Main"] =
                    "Server=localhost,1;Database=TelemetryGuard;User Id=sa;Password=x;" +
                    "TrustServerCertificate=true;Connect Timeout=1;ConnectRetryCount=0",
                ["ConnectionStrings:Redis"] = "localhost:1,connectTimeout=250,abortConnect=false",
                ["Analytics:ClickHouse:ConnectionString"] = "Host=localhost;Port=1;Database=telemetry_guard",
                ["Sdk:BundleRoot"] = bundleRoot ?? _sdkDir
            };
            if (settings is not null)
                foreach (var (k, v) in settings) overrides[k] = v;
            foreach (var (k, v) in overrides)
                b.UseSetting(k, v);
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<ITenantResolver>();
                services.AddSingleton<ITenantResolver>(new NoTenantResolver());
            });
        });

    private static string? HeaderValue(HttpResponseMessage resp, string name)
        => resp.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;

    // ---------------------------------------------------------------- header matrix

    [Fact]
    public async Task LatestBundle_HasShortTtl_CrossOriginHeaders_AndExactBytes()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/sdk/tg.js");
        var body = await resp.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(LatestCacheControl, HeaderValue(resp, "Cache-Control"));
        Assert.Equal("text/javascript; charset=utf-8", resp.Content.Headers.ContentType?.ToString());
        Assert.Equal("*", HeaderValue(resp, "Access-Control-Allow-Origin"));
        Assert.Equal("cross-origin", HeaderValue(resp, "Cross-Origin-Resource-Policy"));
        Assert.Equal("nosniff", HeaderValue(resp, "X-Content-Type-Options"));
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(_sdkDir, "tg.js")), body); // byte-identical
    }

    [Theory]
    [InlineData("tg-0.1.0.js")]
    [InlineData("tg-1.2.3-beta.1.js")] // prerelease suffix is still version-pinned
    public async Task PinnedBundle_IsImmutable_WithCrossOriginHeaders(string file)
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/sdk/" + file);
        var body = await resp.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(PinnedCacheControl, HeaderValue(resp, "Cache-Control"));
        Assert.Equal("text/javascript; charset=utf-8", resp.Content.Headers.ContentType?.ToString());
        Assert.Equal("*", HeaderValue(resp, "Access-Control-Allow-Origin"));
        Assert.Equal("cross-origin", HeaderValue(resp, "Cross-Origin-Resource-Policy"));
        Assert.Equal("nosniff", HeaderValue(resp, "X-Content-Type-Options"));
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(_sdkDir, file)), body);
    }

    [Theory]
    [InlineData("tg.js.map", LatestCacheControl)]      // map follows its bundle's policy
    [InlineData("tg-0.1.0.js.map", PinnedCacheControl)]
    public async Task SourceMaps_AreJson_AndFollowTheirBundlesCachePolicy(string file, string expectedCacheControl)
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/sdk/" + file);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(expectedCacheControl, HeaderValue(resp, "Cache-Control"));
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.ToString());
        Assert.Equal("nosniff", HeaderValue(resp, "X-Content-Type-Options"));
    }

    // ---------------------------------------------------------------- 404 surface

    [Theory]
    [InlineData("/sdk")]                        // bare prefix — no directory response
    [InlineData("/sdk/")]                       // directory URL — no browsing
    [InlineData("/sdk/nonexistent.js")]         // missing file
    [InlineData("/sdk/..%2fsecret.json")]       // traversal to the fixture's parent dir
    [InlineData("/sdk/..%2f..%2fappsettings.json")] // deeper traversal shape
    public async Task MissingDirectoryAndTraversalShapes_Return404(string path)
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync(path);
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.DoesNotContain("never-served", body); // nothing outside sdk/ is reachable
    }

    [Fact]
    public async Task AbsentBundleDirectory_StartupSucceeds_AndSdkRoutes404()
    {
        // Fresh clone: the SDK has never been built, wwwroot/sdk does not exist.
        using var factory = CreateFactory(
            bundleRoot: Path.Combine(_root, "does-not-exist"));
        using var client = factory.CreateClient();

        var sdk = await client.GetAsync("/sdk/tg.js");
        var health = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.NotFound, sdk.StatusCode); // honest 404, no exception
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);    // host started fine
    }

    // ------------------------------------------------- middleware-order guarantees

    [Fact]
    public async Task SdkRoutes_BypassTenantResolution_AndPerTenantRateLimiting()
    {
        // Bucket of 1 token: any request that touched the rate limiter after the
        // first would 429; any request that touched tenant resolution would 401
        // (no X-Api-Key is sent and the resolver resolves nothing). The /sdk
        // branch is mapped BEFORE both middlewares (API-01's order table), so
        // every request must succeed.
        using var factory = CreateFactory(settings: new()
        {
            ["RateLimiting:TokensPerSecond"] = "1",
            ["RateLimiting:BucketSize"] = "1"
        });
        using var client = factory.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            var resp = await client.GetAsync("/sdk/tg.js");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // never 401, never 429
        }
    }

    [Fact]
    public async Task HeadRequest_ServesHeadersOnly_ForCurlDashI()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var resp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/sdk/tg.js"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(LatestCacheControl, HeaderValue(resp, "Cache-Control"));
        Assert.Equal("text/javascript; charset=utf-8", resp.Content.Headers.ContentType?.ToString());
    }
}
