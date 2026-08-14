using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TelemetryGuard.Data.Tenancy;

namespace TelemetryGuard.Tests.Unit.Api;

/// <summary>
/// INT-05: IEdgeSignalReader over the REAL Program composition (ForwardedHeaders
/// included) via the "/__test/edge-signals" diagnostics echo. Covers the config gate
/// (Edge:Provider), the spoof-defense peer check (direct/original peer must be a
/// Cloudflare address), the X-Original-For fallback path ForwardedHeaders leaves
/// behind once it swaps RemoteIpAddress, partial/absent headers, and malformed
/// numeric headers never throwing. No test contacts Cloudflare — the Cloudflare-range
/// merge is driven purely by Edge:Provider=Cloudflare against the SAME embedded
/// cloudflare-ips.txt the runtime ships.
/// </summary>
public sealed class EdgeSignalsTests
{
    private const string ApiKey = "test-api-key";
    private static readonly Guid TenantGuid = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");

    // Inside the embedded Cloudflare v4 range 104.16.0.0/13.
    private static readonly IPAddress CloudflarePeer = IPAddress.Parse("104.16.1.1");

    // A public address that is NOT in any embedded Cloudflare range.
    private static readonly IPAddress NonCloudflarePeer = IPAddress.Parse("8.8.8.8");

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
    /// pipeline runs (TestServer leaves it null otherwise) — this is the DIRECT
    /// (pre-ForwardedHeaders) peer for every test below.</summary>
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
        Dictionary<string, string?>? settings, IPAddress directPeer)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            var overrides = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Main"] =
                    "Server=localhost,1;Database=TelemetryGuard;User Id=sa;Password=x;" +
                    "TrustServerCertificate=true;Connect Timeout=1;ConnectRetryCount=0",
                ["ConnectionStrings:Redis"] = "localhost:1,connectTimeout=250,abortConnect=false",
                ["Analytics:ClickHouse:ConnectionString"] = "Host=localhost;Port=1;Database=telemetry_guard",
                ["TestHost:EnableDiagnostics"] = "true",
            };
            if (settings is not null)
                foreach (var (k, v) in settings) overrides[k] = v;
            foreach (var (k, v) in overrides)
                b.UseSetting(k, v);
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<ITenantResolver>();
                services.AddSingleton<ITenantResolver>(new FakeResolver());
                services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(directPeer));
            });
        });

    private static HttpRequestMessage Get(string path, params (string Name, string Value)[] headers)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey);
        foreach (var (name, value) in headers) req.Headers.TryAddWithoutValidation(name, value);
        return req;
    }

    private sealed record EdgeSignalsDto(string? Ja3, string? Ja4, uint? Asn, int? BotScore);

    private static async Task<EdgeSignalsDto> ReadAsync(HttpResponseMessage resp)
    {
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<EdgeSignalsDto>(
            body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    // --------------------------------------------------------------- tests --

    [Fact]
    public async Task ProviderOff_IgnoresHeaders_EvenFromACloudflarePeer()
    {
        using var factory = CreateFactory(settings: null, directPeer: CloudflarePeer);
        using var client = factory.CreateClient();

        var resp = await client.SendAsync(Get("/__test/edge-signals",
            ("X-TG-JA3", "cd08e31494f9531f560d64c695473da9"),
            ("X-TG-JA4", "t13d1516h2_8daaf6152771_02713d6af862"),
            ("X-TG-ASN", "13335"),
            ("X-TG-Bot-Score", "10")));

        var s = await ReadAsync(resp);
        Assert.Null(s.Ja3);
        Assert.Null(s.Ja4);
        Assert.Null(s.Asn);
        Assert.Null(s.BotScore);
    }

    [Fact]
    public async Task ProviderOn_CloudflarePeer_ReadsAllFourHeaders()
    {
        using var factory = CreateFactory(
            settings: new() { ["Edge:Provider"] = "Cloudflare" }, directPeer: CloudflarePeer);
        using var client = factory.CreateClient();

        var resp = await client.SendAsync(Get("/__test/edge-signals",
            ("X-TG-JA3", "cd08e31494f9531f560d64c695473da9"),
            ("X-TG-JA4", "t13d1516h2_8daaf6152771_02713d6af862"),
            ("X-TG-ASN", "13335"),
            ("X-TG-Bot-Score", "10")));

        var s = await ReadAsync(resp);
        Assert.Equal("cd08e31494f9531f560d64c695473da9", s.Ja3);
        Assert.Equal("t13d1516h2_8daaf6152771_02713d6af862", s.Ja4);
        Assert.Equal(13335u, s.Asn);
        Assert.Equal(10, s.BotScore);
    }

    [Fact]
    public async Task ProviderOn_NonCloudflarePeer_ReadsNothing_SpoofRejected()
    {
        using var factory = CreateFactory(
            settings: new() { ["Edge:Provider"] = "Cloudflare" }, directPeer: NonCloudflarePeer);
        using var client = factory.CreateClient();

        var resp = await client.SendAsync(Get("/__test/edge-signals",
            ("X-TG-JA3", "cd08e31494f9531f560d64c695473da9"),
            ("X-TG-JA4", "t13d1516h2_8daaf6152771_02713d6af862"),
            ("X-TG-ASN", "13335"),
            ("X-TG-Bot-Score", "10")));

        var s = await ReadAsync(resp);
        Assert.Null(s.Ja3);
        Assert.Null(s.Ja4);
        Assert.Null(s.Asn);
        Assert.Null(s.BotScore);
    }

    [Fact]
    public async Task ProviderOn_CloudflarePeer_PartialHeaders_AsnOnly_OthersStayNull()
    {
        using var factory = CreateFactory(
            settings: new() { ["Edge:Provider"] = "Cloudflare" }, directPeer: CloudflarePeer);
        using var client = factory.CreateClient();

        var resp = await client.SendAsync(Get("/__test/edge-signals", ("X-TG-ASN", "13335")));

        var s = await ReadAsync(resp);
        Assert.Null(s.Ja3);
        Assert.Null(s.Ja4);
        Assert.Equal(13335u, s.Asn);
        Assert.Null(s.BotScore);
    }

    [Fact]
    public async Task ProviderOn_CloudflarePeer_HeadersAbsent_EverythingNull_NoCrash()
    {
        using var factory = CreateFactory(
            settings: new() { ["Edge:Provider"] = "Cloudflare" }, directPeer: CloudflarePeer);
        using var client = factory.CreateClient();

        var resp = await client.SendAsync(Get("/__test/edge-signals"));

        var s = await ReadAsync(resp);
        Assert.Null(s.Ja3);
        Assert.Null(s.Ja4);
        Assert.Null(s.Asn);
        Assert.Null(s.BotScore);
    }

    [Theory]
    [InlineData("not-a-number", "also-not-a-number")]
    [InlineData("-1", "1.5")]        // ASN is unsigned; bot score header holds a non-integer
    [InlineData("", "")]
    public async Task ProviderOn_CloudflarePeer_MalformedNumericHeaders_ParseToNull_NoException(
        string asnValue, string botScoreValue)
    {
        using var factory = CreateFactory(
            settings: new() { ["Edge:Provider"] = "Cloudflare" }, directPeer: CloudflarePeer);
        using var client = factory.CreateClient();

        var resp = await client.SendAsync(Get("/__test/edge-signals",
            ("X-TG-ASN", asnValue), ("X-TG-Bot-Score", botScoreValue)));

        var s = await ReadAsync(resp); // 200, not 500 — no exception escaped the handler
        Assert.Null(s.Asn);
        Assert.Null(s.BotScore);
    }

    /// <summary>The defense-in-depth case that matters most: a Cloudflare direct peer
    /// forwards a DIFFERENT (non-Cloudflare) client IP via X-Forwarded-For. Once
    /// ForwardedHeadersMiddleware honors that trusted hop, RemoteIpAddress becomes the
    /// forwarded (non-CF) client address — so the reader must key its spoof check off
    /// the ORIGINAL peer (preserved by ASP.NET Core in X-Original-For), not the
    /// post-forwarding RemoteIpAddress, or every real Cloudflare-fronted request would
    /// be rejected.</summary>
    [Fact]
    public async Task ProviderOn_AfterForwardedHeadersSwappedRemoteIp_StillAuthorizesViaOriginalPeer()
    {
        using var factory = CreateFactory(
            settings: new() { ["Edge:Provider"] = "Cloudflare" }, directPeer: CloudflarePeer);
        using var client = factory.CreateClient();

        var resp = await client.SendAsync(Get("/__test/edge-signals",
            ("X-Forwarded-For", "203.0.113.50"), // end-user's real IP, NOT a Cloudflare address
            ("X-TG-JA3", "cd08e31494f9531f560d64c695473da9"),
            ("X-TG-ASN", "13335")));

        // Sanity: ForwardedHeaders actually swapped RemoteIpAddress to the forwarded value.
        var ipResp = await client.SendAsync(Get("/__test/ip", ("X-Forwarded-For", "203.0.113.50")));
        Assert.Equal("203.0.113.50", await ipResp.Content.ReadAsStringAsync());

        var s = await ReadAsync(resp);
        Assert.Equal("cd08e31494f9531f560d64c695473da9", s.Ja3); // honored: original peer WAS Cloudflare
        Assert.Equal(13335u, s.Asn);
    }

    /// <summary>Same forwarding scenario, but the ORIGINAL (direct) peer is NOT
    /// Cloudflare — ForwardedHeaders trusts nothing (Edge:Provider merges only
    /// Cloudflare ranges), so RemoteIpAddress stays the direct peer and the spoof
    /// check correctly rejects.</summary>
    [Fact]
    public async Task ProviderOn_NonCloudflareOriginalPeer_ForwardedForIgnored_HeadersRejected()
    {
        using var factory = CreateFactory(
            settings: new() { ["Edge:Provider"] = "Cloudflare" }, directPeer: NonCloudflarePeer);
        using var client = factory.CreateClient();

        var resp = await client.SendAsync(Get("/__test/edge-signals",
            ("X-Forwarded-For", "203.0.113.50"),
            ("X-TG-JA3", "cd08e31494f9531f560d64c695473da9")));

        var s = await ReadAsync(resp);
        Assert.Null(s.Ja3);

        // The untrusted forwarded value never reached RemoteIpAddress either.
        var ipResp = await client.SendAsync(Get("/__test/ip", ("X-Forwarded-For", "203.0.113.50")));
        Assert.Equal(NonCloudflarePeer.ToString(), await ipResp.Content.ReadAsStringAsync());
    }
}
