using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TelemetryGuard.Data.Tenancy;

namespace TelemetryGuard.Tests.Unit.Api;

/// <summary>
/// API-01 pipeline tests over the real Program composition via
/// WebApplicationFactory: liveness, forwarded-header trust, per-tenant rate
/// limiting, health-endpoint exemptions, and RFC 7807 unhandled-exception
/// responses. No backing stores run here — connection strings point at
/// closed ports with fail-fast timeouts.
/// </summary>
public sealed class PipelineTests
{
    private const string ApiKey = "test-api-key";
    private static readonly Guid TenantGuid = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");

    private sealed class FakeResolver : ITenantResolver
    {
        public Task<ResolvedTenant?> ResolveApiKeyAsync(string apiKey, CancellationToken ct)
            => Task.FromResult(apiKey == ApiKey
                ? new ResolvedTenant(TenantGuid, ["admin"], null, null)
                : (ResolvedTenant?)null);

        public Task<ResolvedTenant?> ResolveSiteKeyAsync(string siteKey, CancellationToken ct)
            => Task.FromResult<ResolvedTenant?>(null);
    }

    /// <summary>Stamps a deterministic socket-level RemoteIpAddress before the
    /// pipeline (TestServer leaves it null otherwise), so ForwardedHeaders
    /// trust decisions can be asserted.</summary>
    private sealed class RemoteIpStartupFilter(IPAddress ip) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, nxt) =>
            {
                ctx.Connection.RemoteIpAddress = ip;
                return nxt(ctx);
            });
            next(app);
        };
    }

    private static WebApplicationFactory<Program> CreateFactory(
        Dictionary<string, string?>? settings = null,
        IPAddress? remoteIp = null)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            // UseSetting (not ConfigureAppConfiguration): with minimal hosting the
            // ConfigureAppConfiguration callbacks run only AFTER Program.cs has
            // already read builder.Configuration imperatively (rate-limit sizing),
            // while UseSetting values are present from builder creation.
            var overrides = new Dictionary<string, string?>
            {
                // Closed ports + fail-fast timeouts: /ready must go Unhealthy
                // quickly instead of waiting out real driver timeouts.
                ["ConnectionStrings:Main"] =
                    "Server=localhost,1;Database=TelemetryGuard;User Id=sa;Password=x;" +
                    "TrustServerCertificate=true;Connect Timeout=1;ConnectRetryCount=0",
                ["ConnectionStrings:Redis"] = "localhost:1,connectTimeout=250,abortConnect=false",
                ["Analytics:ClickHouse:ConnectionString"] = "Host=localhost;Port=1;Database=telemetry_guard",
                ["TestHost:EnableDiagnostics"] = "true"
            };
            if (settings is not null)
                foreach (var (k, v) in settings) overrides[k] = v;
            foreach (var (k, v) in overrides)
                b.UseSetting(k, v);
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<ITenantResolver>();
                services.AddSingleton<ITenantResolver>(new FakeResolver());
                if (remoteIp is not null)
                    services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(remoteIp));
            });
        });

    private static HttpRequestMessage Get(string path, params (string Name, string Value)[] headers)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        foreach (var (name, value) in headers) req.Headers.TryAddWithoutValidation(name, value);
        return req;
    }

    [Fact]
    public async Task Healthz_Returns200_WithNoBackingServices()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // liveness predicate excludes all checks
    }

    [Fact]
    public async Task Ready_IsExemptFromTenantResolution_AndReports503WhenStoresAreDown()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/ready"); // no X-Api-Key: must NOT be 401

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Fact]
    public async Task SpoofedXForwardedFor_IsIgnored_WhenTrustedProxyCidrsIsEmpty()
    {
        var socketIp = IPAddress.Parse("203.0.113.9");
        using var factory = CreateFactory(remoteIp: socketIp);
        using var client = factory.CreateClient();

        var resp = await client.SendAsync(Get("/__test/ip",
            ("X-Api-Key", ApiKey), ("X-Forwarded-For", "1.2.3.4")));
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("203.0.113.9", body); // spoofed header did not change RemoteIpAddress
    }

    [Fact]
    public async Task XForwardedFor_IsHonored_WhenSourceIsAConfiguredTrustedProxy()
    {
        var socketIp = IPAddress.Parse("203.0.113.9");
        using var factory = CreateFactory(
            settings: new() { ["ForwardedHeaders:TrustedProxyCidrs:0"] = "203.0.113.0/24" },
            remoteIp: socketIp);
        using var client = factory.CreateClient();

        var resp = await client.SendAsync(Get("/__test/ip",
            ("X-Api-Key", ApiKey), ("X-Forwarded-For", "1.2.3.4")));
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("1.2.3.4", body); // config-driven CIDR made the proxy trusted
    }

    [Fact]
    public async Task TwoProxyChain_ResolvesTheClientIp_WhenForwardLimitCoversBothHops()
    {
        // A platform edge (203.0.113.1) in front of a local nginx (127.0.0.1) that also
        // appends: X-Forwarded-For ends up "<client>, <edge>" with the socket on nginx.
        var socketIp = IPAddress.Parse("127.0.0.1");
        using var factory = CreateFactory(
            settings: new()
            {
                ["ForwardedHeaders:TrustedProxyCidrs:0"] = "127.0.0.1/32",
                ["ForwardedHeaders:TrustedProxyCidrs:1"] = "203.0.113.1/32",
                ["ForwardedHeaders:ForwardLimit"] = "2",
            },
            remoteIp: socketIp);
        using var client = factory.CreateClient();

        var resp = await client.SendAsync(Get("/__test/ip",
            ("X-Api-Key", ApiKey), ("X-Forwarded-For", "198.51.100.7, 203.0.113.1")));
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("198.51.100.7", body); // both trusted hops unwound, real client left
    }

    [Fact]
    public async Task TwoProxyChain_StopsAtTheEdge_WhenForwardLimitIsLeftAtOne()
    {
        // Same chain, default limit: only nginx is unwound, so the edge's own address is
        // what the app sees. This is the misconfiguration that silently collapses every
        // visitor onto one IP — geo, ASN and velocity all become meaningless.
        var socketIp = IPAddress.Parse("127.0.0.1");
        using var factory = CreateFactory(
            settings: new()
            {
                ["ForwardedHeaders:TrustedProxyCidrs:0"] = "127.0.0.1/32",
                ["ForwardedHeaders:TrustedProxyCidrs:1"] = "203.0.113.1/32",
            },
            remoteIp: socketIp);
        using var client = factory.CreateClient();

        var resp = await client.SendAsync(Get("/__test/ip",
            ("X-Api-Key", ApiKey), ("X-Forwarded-For", "198.51.100.7, 203.0.113.1")));

        Assert.Equal("203.0.113.1", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ForwardLimit_DoesNotLetAnUntrustedHopBeUnwound()
    {
        // ForwardLimit 2 but only nginx trusted: the middleware must stop at the first
        // untrusted address instead of walking further up a client-supplied header.
        var socketIp = IPAddress.Parse("127.0.0.1");
        using var factory = CreateFactory(
            settings: new()
            {
                ["ForwardedHeaders:TrustedProxyCidrs:0"] = "127.0.0.1/32",
                ["ForwardedHeaders:ForwardLimit"] = "2",
            },
            remoteIp: socketIp);
        using var client = factory.CreateClient();

        var resp = await client.SendAsync(Get("/__test/ip",
            ("X-Api-Key", ApiKey), ("X-Forwarded-For", "1.2.3.4, 198.51.100.9")));

        Assert.Equal("198.51.100.9", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RateLimiter_Returns429_WhenTenantBucketIsExhausted()
    {
        using var factory = CreateFactory(settings: new()
        {
            ["RateLimiting:TokensPerSecond"] = "1",
            ["RateLimiting:BucketSize"] = "1"
        });
        using var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            var resp = await client.SendAsync(Get("/__test/ip", ("X-Api-Key", ApiKey)));
            statuses.Add(resp.StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, statuses[0]);                       // first token is available
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);          // bucket of 1 exhausted
    }

    [Fact]
    public async Task HealthEndpoints_AreExemptFromRateLimiting()
    {
        using var factory = CreateFactory(settings: new()
        {
            ["RateLimiting:TokensPerSecond"] = "1",
            ["RateLimiting:BucketSize"] = "1"
        });
        using var client = factory.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            var resp = await client.GetAsync("/healthz");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // never 429
        }
    }

    [Fact]
    public async Task UnhandledException_Returns500ProblemJson_WithoutLeakingDetails()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var resp = await client.SendAsync(Get("/__test/throw", ("X-Api-Key", ApiKey)));
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("Deliberate test-host failure", body); // no exception message
        Assert.DoesNotContain("InvalidOperationException", body);    // no exception type
        Assert.DoesNotContain("   at ", body);                       // no stack trace
    }
}
