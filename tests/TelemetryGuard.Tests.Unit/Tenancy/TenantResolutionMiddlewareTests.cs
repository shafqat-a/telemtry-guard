using Microsoft.AspNetCore.Http;
using TelemetryGuard.Api.Tenancy;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Data.Tenancy;

namespace TelemetryGuard.Tests.Unit.Tenancy;

public sealed class TenantResolutionMiddlewareTests
{
    private static readonly Guid TenantA = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = new("22222222-2222-2222-2222-222222222222");

    private static readonly byte[] TransparentGif = Convert.FromBase64String(
        "R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAICRAEAOw==");

    private sealed class FakeResolver : ITenantResolver
    {
        public Dictionary<string, ResolvedTenant> ApiKeys { get; } = new();
        public Dictionary<string, ResolvedTenant> SiteKeys { get; } = new();
        public int ApiKeyCalls { get; private set; }
        public int SiteKeyCalls { get; private set; }

        public Task<ResolvedTenant?> ResolveApiKeyAsync(string apiKey, CancellationToken ct)
        {
            ApiKeyCalls++;
            return Task.FromResult(ApiKeys.GetValueOrDefault(apiKey));
        }

        public Task<ResolvedTenant?> ResolveSiteKeyAsync(string siteKey, CancellationToken ct)
        {
            SiteKeyCalls++;
            return Task.FromResult(SiteKeys.GetValueOrDefault(siteKey));
        }
    }

    private sealed class Harness
    {
        public FakeResolver Resolver { get; } = new();
        public TenantContext Tenant { get; } = new();
        public DefaultHttpContext Context { get; } = new();
        public bool NextInvoked { get; private set; }

        public Harness()
        {
            Context.Response.Body = new MemoryStream();
        }

        public Task RunAsync()
        {
            var middleware = new TenantResolutionMiddleware(_ =>
            {
                NextInvoked = true;
                return Task.CompletedTask;
            });
            return middleware.InvokeAsync(Context, Resolver, Tenant);
        }

        public byte[] ResponseBody => ((MemoryStream)Context.Response.Body).ToArray();
    }

    // 1. Admin route with valid X-Api-Key -> context set, next invoked.
    [Fact]
    public async Task AdminRoute_ValidApiKey_ResolvesTenant_AndInvokesNext()
    {
        var h = new Harness();
        h.Resolver.ApiKeys["good-api-key"] = new ResolvedTenant(TenantA, new[] { "read", "write" }, null, null);
        h.Context.Request.Path = "/admin/whatever";
        h.Context.Request.Headers["X-Api-Key"] = "good-api-key";

        await h.RunAsync();

        Assert.True(h.NextInvoked);
        Assert.True(h.Tenant.IsResolved);
        Assert.Equal(TenantA, h.Tenant.TenantId.Value);
        Assert.Null(h.Tenant.SiteKey);
    }

    // 2. Admin route with no header -> 401 problem+json, next NOT invoked.
    [Fact]
    public async Task AdminRoute_NoHeader_Returns401ProblemJson()
    {
        var h = new Harness();
        h.Context.Request.Path = "/admin/whatever";

        await h.RunAsync();

        Assert.False(h.NextInvoked);
        Assert.False(h.Tenant.IsResolved);
        Assert.Equal(StatusCodes.Status401Unauthorized, h.Context.Response.StatusCode);
        Assert.Equal("application/problem+json", h.Context.Response.ContentType);
    }

    // 3. Admin route with unknown key -> 401, next NOT invoked.
    [Fact]
    public async Task AdminRoute_UnknownApiKey_Returns401()
    {
        var h = new Harness();
        h.Context.Request.Path = "/admin/whatever";
        h.Context.Request.Headers["X-Api-Key"] = "unknown-key";

        await h.RunAsync();

        Assert.False(h.NextInvoked);
        Assert.False(h.Tenant.IsResolved);
        Assert.Equal(StatusCodes.Status401Unauthorized, h.Context.Response.StatusCode);
    }

    // 4. /c with good site key -> context set (tenant + site key), next invoked.
    [Fact]
    public async Task ClickRoute_GoodSiteKey_ResolvesTenant_AndInvokesNext()
    {
        var h = new Harness();
        h.Resolver.SiteKeys["goodkey"] = new ResolvedTenant(TenantA, Array.Empty<string>(), "goodkey", "js");
        h.Context.Request.Path = "/c";
        h.Context.Request.QueryString = new QueryString("?cid=abc123&k=goodkey");

        await h.RunAsync();

        Assert.True(h.NextInvoked);
        Assert.True(h.Tenant.IsResolved);
        Assert.Equal(TenantA, h.Tenant.TenantId.Value);
        Assert.Equal("goodkey", h.Tenant.SiteKey);
    }

    // 5. /c with unknown site key -> 404, empty body, next NOT invoked.
    [Fact]
    public async Task ClickRoute_UnknownSiteKey_Returns404EmptyBody()
    {
        var h = new Harness();
        h.Context.Request.Path = "/c";
        h.Context.Request.QueryString = new QueryString("?cid=abc123&k=badkey");

        await h.RunAsync();

        Assert.False(h.NextInvoked);
        Assert.False(h.Tenant.IsResolved);
        Assert.Equal(StatusCodes.Status404NotFound, h.Context.Response.StatusCode);
        Assert.Empty(h.ResponseBody);
    }

    // 6. /c with no k param -> 404.
    [Fact]
    public async Task ClickRoute_NoSiteKey_Returns404()
    {
        var h = new Harness();
        h.Context.Request.Path = "/c";
        h.Context.Request.QueryString = new QueryString("?cid=abc123");

        await h.RunAsync();

        Assert.False(h.NextInvoked);
        Assert.Equal(StatusCodes.Status404NotFound, h.Context.Response.StatusCode);
        Assert.Equal(0, h.Resolver.SiteKeyCalls); // no k param, nothing to resolve
    }

    // 7. /p.gif with good site key -> context set, next invoked.
    [Fact]
    public async Task PixelRoute_GoodSiteKey_ResolvesTenant_AndInvokesNext()
    {
        var h = new Harness();
        h.Resolver.SiteKeys["goodkey"] = new ResolvedTenant(TenantA, Array.Empty<string>(), "goodkey", "pixel");
        h.Context.Request.Path = "/p.gif";
        h.Context.Request.QueryString = new QueryString("?k=goodkey");

        await h.RunAsync();

        Assert.True(h.NextInvoked);
        Assert.True(h.Tenant.IsResolved);
        Assert.Equal(TenantA, h.Tenant.TenantId.Value);
        Assert.Equal("goodkey", h.Tenant.SiteKey);
    }

    // 8. /p.gif with unknown key -> success-shaped drop: 200 image/gif, exactly the
    //    43-byte GIF, Cache-Control: no-store, private, next NOT invoked.
    [Fact]
    public async Task PixelRoute_UnknownSiteKey_ReturnsTransparentGif()
    {
        var h = new Harness();
        h.Context.Request.Path = "/p.gif";
        h.Context.Request.QueryString = new QueryString("?k=badkey");

        await h.RunAsync();

        Assert.False(h.NextInvoked);
        Assert.False(h.Tenant.IsResolved);
        Assert.Equal(StatusCodes.Status200OK, h.Context.Response.StatusCode);
        Assert.Equal("image/gif", h.Context.Response.ContentType);
        Assert.Equal("no-store, private", h.Context.Response.Headers.CacheControl);
        Assert.Equal(43, h.ResponseBody.Length);
        Assert.Equal(TransparentGif, h.ResponseBody);
    }

    // 9. /i and /i/init POST pass through with the context unresolved; the middleware
    //    must not touch the resolver (the key lives in the body — API-04 resolves it).
    [Theory]
    [InlineData("/i")]
    [InlineData("/i/init")]
    public async Task BeaconRoutes_PassThrough_Unresolved_NoResolverCalls(string path)
    {
        var h = new Harness();
        h.Context.Request.Path = path;
        h.Context.Request.Method = "POST";

        await h.RunAsync();

        Assert.True(h.NextInvoked);
        Assert.False(h.Tenant.IsResolved);
        Assert.Equal(0, h.Resolver.ApiKeyCalls);
        Assert.Equal(0, h.Resolver.SiteKeyCalls);
    }

    // 10. Header wins: valid X-Api-Key on /c beats a valid ?k= for another tenant.
    [Fact]
    public async Task ClickRoute_ApiKeyHeaderWins_OverSiteKey()
    {
        var h = new Harness();
        h.Resolver.ApiKeys["good-api-key"] = new ResolvedTenant(TenantB, new[] { "read" }, null, null);
        h.Resolver.SiteKeys["goodSiteKey"] = new ResolvedTenant(TenantA, Array.Empty<string>(), "goodSiteKey", "js");
        h.Context.Request.Path = "/c";
        h.Context.Request.QueryString = new QueryString("?cid=abc&k=goodSiteKey");
        h.Context.Request.Headers["X-Api-Key"] = "good-api-key";

        await h.RunAsync();

        Assert.True(h.NextInvoked);
        Assert.True(h.Tenant.IsResolved);
        Assert.Equal(TenantB, h.Tenant.TenantId.Value);
        Assert.Null(h.Tenant.SiteKey); // API-key resolution carries no site key
        Assert.Equal(0, h.Resolver.SiteKeyCalls);
    }

    // 11. /healthz is exempt: next invoked, no resolution attempted.
    [Fact]
    public async Task Healthz_Exempt_NextInvoked_NoResolution()
    {
        var h = new Harness();
        h.Context.Request.Path = "/healthz";

        await h.RunAsync();

        Assert.True(h.NextInvoked);
        Assert.False(h.Tenant.IsResolved);
        Assert.Equal(0, h.Resolver.ApiKeyCalls);
        Assert.Equal(0, h.Resolver.SiteKeyCalls);
    }
}
