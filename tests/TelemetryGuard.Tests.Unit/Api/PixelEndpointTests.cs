using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using StackExchange.Redis;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Api.Endpoints;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Core.Time;
using TelemetryGuard.Data.Models;
using TelemetryGuard.Data.Repositories;
using TelemetryGuard.Data.Tenancy;
using TelemetryGuard.RiskEngine.Velocity;

namespace TelemetryGuard.Tests.Unit.Api;

/// <summary>
/// API-03 pixel endpoint tests over the real Program composition via
/// WebApplicationFactory: byte-exact 43-byte GIF + no-store headers, cookie
/// issuance/reuse (query param and cookie), grace-entry NX semantics (a
/// tracker-created deadline is never reset), click-context non-clobbering
/// (kind=tracker preserved), pixel ClickEvent shape (no click ids, no SDK
/// fields), velocity recording with a null click id, Redis-down resilience,
/// and byte identity between the handler's GIF and DAT-04's anti-probing GIF.
/// All stores are in-memory fakes; Redis is an NSubstitute capture harness.
/// </summary>
public sealed class PixelEndpointTests
{
    private static readonly Guid TenantGuid = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");
    private static readonly string Tid = TenantGuid.ToString("D");
    private const string SiteKey = "site-1";
    private static readonly IPAddress RemoteIp = IPAddress.Parse("203.0.113.10");
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);

    /// <summary>The exact 43 bytes the endpoint must serve (task spec).</summary>
    private static readonly byte[] ExpectedGif =
    {
        0x47,0x49,0x46,0x38,0x39,0x61,
        0x01,0x00,0x01,0x00,0x80,0x00,0x00,
        0x00,0x00,0x00,0xFF,0xFF,0xFF,
        0x21,0xF9,0x04,0x01,0x00,0x00,0x00,0x00,
        0x2C,0x00,0x00,0x00,0x00,0x01,0x00,0x01,0x00,0x00,
        0x02,0x02,0x44,0x01,0x00,
        0x3B
    };

    // ---------------------------------------------------------------- fakes --

    private sealed class FakeResolver : ITenantResolver
    {
        public Task<ResolvedTenant?> ResolveApiKeyAsync(string apiKey, CancellationToken ct)
            => Task.FromResult<ResolvedTenant?>(null);

        public Task<ResolvedTenant?> ResolveSiteKeyAsync(string siteKey, CancellationToken ct)
            => Task.FromResult(siteKey == SiteKey
                ? new ResolvedTenant(TenantGuid, [], SiteKey, "pixel")
                : (ResolvedTenant?)null);
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = FixedNow;
    }

    private sealed class FakeTenantRepository : ITenantRepository
    {
        public Task<TenantRecord?> GetCurrentAsync(CancellationToken ct)
            => Task.FromResult<TenantRecord?>(
                new TenantRecord(TenantGuid, "Acme", 0, 45, 0, FixedNow.UtcDateTime));

        public Task<bool> UpdateRetentionDaysAsync(int retentionDays, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> UpdateEnforcementModeAsync(byte enforcementMode, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>Records RSK-03 calls; the pixel must always pass clickId: null.</summary>
    private sealed class FakeVelocityStore : IVelocityStore
    {
        public readonly List<(string Ip, string? Ua, string? ClickId)> Clicks = [];
        public bool Down;

        public Task<bool?> RecordClickAsync(string ip, string? userAgent, string? clickId, CancellationToken ct)
        {
            if (Down)
                throw new RedisConnectionException(ConnectionFailureType.UnableToConnect,
                    CommandFlags.None, "redis down (test)", null, CommandStatus.Unknown);
            lock (Clicks)
                Clicks.Add((ip, userAgent, clickId));
            return Task.FromResult<bool?>(clickId is null ? null : true);
        }

        public Task RecordSessionAsync(string ip, string? userAgent, string? visitorId, string sessionId,
                                       bool storageAgeZero, CancellationToken ct) => throw new NotSupportedException();

        public Task<VelocitySnapshot> ReadAsync(string ip, string? visitorId, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class CapturingEventSink : IEventSink
    {
        public readonly List<ClickEvent> Events = [];

        public ValueTask WriteBatchAsync(ReadOnlyMemory<ClickEvent> events, CancellationToken ct)
        {
            lock (Events)
                Events.AddRange(events.ToArray());
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// NSubstitute capture harness for the raw Redis calls the pixel handler
    /// issues. Stateful where the handler's semantics depend on it:
    /// - HashGetAsync(key, "kind") answers from preseeded hashes (tracker-first
    ///   scenario) so non-clobbering is observable;
    /// - SortedSetAddAsync honours When.NotExists against preseeded members so
    ///   grace NX semantics are observable.
    /// Down=true throws from GetDatabase().
    /// </summary>
    private sealed class RedisHarness
    {
        public readonly IConnectionMultiplexer Mux;
        public readonly List<(string Key, HashEntry[] Entries)> Hashes = [];
        public readonly List<(string Key, TimeSpan? Ttl)> Expires = [];
        public readonly List<(string Key, string Member, double Score, When When)> SortedSetAdds = [];
        public readonly List<(string Key, string Member)> SetAdds = [];
        public readonly Dictionary<string, string> PreseededKinds = [];      // clickKey -> kind
        public readonly Dictionary<(string Key, string Member), double> SortedSets = [];

        public RedisHarness(bool down = false)
        {
            Mux = Substitute.For<IConnectionMultiplexer>();
            if (down)
            {
                Mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>())
                   .Returns(_ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect,
                       CommandFlags.None, "redis down (test)", null, CommandStatus.Unknown));
                return;
            }

            var db = Substitute.For<IDatabase>();
            Mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

            db.HashGetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>())
              .Returns(ci =>
              {
                  lock (PreseededKinds)
                      return PreseededKinds.TryGetValue(ci.ArgAt<RedisKey>(0).ToString(), out var kind)
                          ? (RedisValue)kind
                          : RedisValue.Null;
              });

            db.When(d => d.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<HashEntry[]>()))
              .Do(ci => { lock (Hashes) Hashes.Add((ci.ArgAt<RedisKey>(0).ToString(), ci.ArgAt<HashEntry[]>(1))); });

            db.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>())
              .Returns(true)
              .AndDoes(ci => { lock (Expires) Expires.Add((ci.ArgAt<RedisKey>(0).ToString(), ci.ArgAt<TimeSpan?>(1))); });

            // 4-arg overload with When — the one the pixel handler must use.
            db.SortedSetAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<double>(), Arg.Any<When>())
              .Returns(ci =>
              {
                  var key = ci.ArgAt<RedisKey>(0).ToString();
                  var member = ci.ArgAt<RedisValue>(1).ToString();
                  var score = ci.ArgAt<double>(2);
                  var when = ci.ArgAt<When>(3);
                  lock (SortedSets)
                  {
                      SortedSetAdds.Add((key, member, score, when));
                      if (when == When.NotExists && SortedSets.ContainsKey((key, member)))
                          return false;                       // NX: existing deadline untouched
                      SortedSets[(key, member)] = score;
                      return true;
                  }
              });

            db.SetAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>())
              .Returns(true)
              .AndDoes(ci =>
              {
                  lock (SetAdds)
                      SetAdds.Add((ci.ArgAt<RedisKey>(0).ToString(), ci.ArgAt<RedisValue>(1).ToString()));
              });
        }
    }

    /// <summary>Stamps a deterministic socket-level RemoteIpAddress before the
    /// pipeline (TestServer leaves it null otherwise).</summary>
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

    // ------------------------------------------------------------- harness --

    private sealed class PixelApp : IDisposable
    {
        public readonly WebApplicationFactory<Program> Factory;
        public readonly FakeVelocityStore Velocity = new();
        public readonly CapturingEventSink Sink = new();
        public readonly RedisHarness Redis;
        public readonly FakeClock Clock = new();

        public PixelApp(bool redisDown = false)
        {
            Redis = new RedisHarness(redisDown);
            Velocity.Down = redisDown;
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                // UseSetting (not ConfigureAppConfiguration): Program.cs reads some
                // values imperatively at builder time (see PipelineTests).
                var overrides = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Main"] =
                        "Server=localhost,1;Database=TelemetryGuard;User Id=sa;Password=x;" +
                        "TrustServerCertificate=true;Connect Timeout=1;ConnectRetryCount=0",
                    ["ConnectionStrings:Redis"] = "localhost:1,connectTimeout=250,abortConnect=false",
                    ["Analytics:ClickHouse:ConnectionString"] = "Host=localhost;Port=1;Database=telemetry_guard",
                };
                foreach (var (k, v) in overrides)
                    b.UseSetting(k, v);
                b.ConfigureTestServices(services =>
                {
                    services.RemoveAll<ITenantResolver>();
                    services.AddSingleton<ITenantResolver>(new FakeResolver());
                    services.RemoveAll<ITenantRepository>();
                    services.AddSingleton<ITenantRepository>(new FakeTenantRepository());
                    services.RemoveAll<IVelocityStore>();
                    services.AddSingleton<IVelocityStore>(Velocity);
                    services.RemoveAll<IEventSink>();
                    services.AddSingleton<IEventSink>(Sink);
                    services.RemoveAll<IConnectionMultiplexer>();
                    services.AddSingleton(Redis.Mux);
                    services.RemoveAll<IClock>();
                    services.AddSingleton<IClock>(Clock);
                    services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(RemoteIp));
                });
            });
        }

        public HttpClient Client() => Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

        public void Dispose() => Factory.Dispose();
    }

    private static HttpRequestMessage Get(string path, params (string Name, string Value)[] headers)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.TryAddWithoutValidation("User-Agent", "TestUA/1.0");
        req.Headers.TryAddWithoutValidation("Accept-Language", "en-US");
        foreach (var (name, value) in headers) req.Headers.TryAddWithoutValidation(name, value);
        return req;
    }

    // --------------------------------------------------------------- tests --

    [Fact]
    public async Task ServesExactly43GifBytes_WithImageGifContentType_AndNoStoreHeaders()
    {
        using var app = new PixelApp();
        using var client = app.Client();

        var resp = await client.SendAsync(Get($"/p.gif?k={SiteKey}"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("image/gif", resp.Content.Headers.ContentType?.MediaType);
        var body = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(43, body.Length);
        Assert.Equal(ExpectedGif, body);                                 // byte-exact
        Assert.Equal((byte)'G', body[0]);                                // "GIF89a" leader...
        Assert.Equal(0x3B, body[^1]);                                    // ...trailer

        Assert.True(resp.Headers.CacheControl?.NoStore);
        Assert.True(resp.Headers.CacheControl?.NoCache);
        Assert.True(resp.Headers.CacheControl?.MustRevalidate);
        Assert.Contains("no-cache", resp.Headers.GetValues("Pragma"));
        Assert.Equal("0", Assert.Single(resp.Content.Headers.GetValues("Expires")));
    }

    [Fact]
    public async Task NoSidSupplied_MintsSid_AndSetsSessionCookie()
    {
        using var app = new PixelApp();
        using var client = app.Client();

        var resp = await client.SendAsync(Get($"/p.gif?k={SiteKey}"));

        var setCookie = Assert.Single(resp.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("tg_sid=", setCookie);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=1800", setCookie, StringComparison.OrdinalIgnoreCase);

        var evt = Assert.Single(app.Sink.Events);
        Assert.Matches("^[0-9a-f]{32}$", evt.SessionId);
        Assert.StartsWith($"tg_sid={evt.SessionId}", setCookie);         // cookie carries the minted sid
    }

    [Fact]
    public async Task QueryParamSid_IsReused_NoNewCookieIssued()
    {
        using var app = new PixelApp();
        using var client = app.Client();
        const string sid = "0123456789abcdef0123456789abcdef";

        var resp = await client.SendAsync(Get($"/p.gif?k={SiteKey}&tg_sid={sid}"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False(resp.Headers.Contains("Set-Cookie"));               // no new cookie
        var evt = Assert.Single(app.Sink.Events);
        Assert.Equal(sid, evt.SessionId);
    }

    [Fact]
    public async Task CookieSid_IsReused_NoNewCookieIssued()
    {
        using var app = new PixelApp();
        using var client = app.Client();
        const string sid = "fedcba9876543210fedcba9876543210";

        var resp = await client.SendAsync(Get($"/p.gif?k={SiteKey}", ("Cookie", $"tg_sid={sid}")));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False(resp.Headers.Contains("Set-Cookie"));
        var evt = Assert.Single(app.Sink.Events);
        Assert.Equal(sid, evt.SessionId);
    }

    [Fact]
    public async Task MalformedSid_IsReplaced_NotTrusted()
    {
        using var app = new PixelApp();
        using var client = app.Client();

        var resp = await client.SendAsync(Get(
            $"/p.gif?k={SiteKey}&tg_sid=DROPTABLE", ("Cookie", "tg_sid=not-hex-either")));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var evt = Assert.Single(app.Sink.Events);
        Assert.Matches("^[0-9a-f]{32}$", evt.SessionId);
        Assert.NotEqual("DROPTABLE", evt.SessionId);
        Assert.Contains("Set-Cookie", resp.Headers.Select(h => h.Key)); // fresh sid -> fresh cookie
    }

    [Fact]
    public async Task PixelClickEvent_ReachesSink_WithNoClickIds_AndNoSdkFields()
    {
        using var app = new PixelApp();
        using var client = app.Client();

        await client.SendAsync(Get(
            $"/p.gif?k={SiteKey}&gclid=should-be-ignored",   // click ids are tracker-only (API-02)
            ("Sec-CH-UA", "\"Chromium\";v=\"120\""),
            ("Sec-CH-UA-Mobile", "?0"),
            ("Sec-CH-UA-Platform", "\"Linux\""),
            ("Referer", "https://shop.example/landing")));

        var evt = Assert.Single(app.Sink.Events);
        Assert.Equal(new TenantId(TenantGuid), evt.TenantId);
        Assert.Equal(SiteKey, evt.SiteKey);
        Assert.Equal(EventKind.Pixel, evt.Kind);

        // No click-id fields — even when a stray gclid rides the query string.
        Assert.Equal("", evt.CampaignId);
        Assert.Equal("", evt.Gclid);
        Assert.Equal("", evt.Fbclid);
        Assert.Equal("", evt.Msclkid);
        Assert.Equal("", evt.Ttclid);
        Assert.Null(evt.ClickIdInvalid);                     // absence here proves nothing

        // HTTP layer captured.
        Assert.Equal("203.0.113.10", evt.Ip);
        Assert.Equal("TestUA/1.0", evt.UserAgent);
        Assert.Equal("\"Chromium\";v=\"120\"", evt.SecChUa);
        Assert.Equal("?0", evt.SecChUaMobile);
        Assert.Equal("\"Linux\"", evt.SecChUaPlatform);
        Assert.Equal("en-US", evt.AcceptLanguage);
        Assert.Equal("https://shop.example/landing", evt.Referrer);
        Assert.Contains("User-Agent", evt.HeaderNames);

        // SDK/behavioral fields stay absent — NaN/null semantics (spec §7, D22).
        Assert.False(evt.HasJsBeacon);
        Assert.Null(evt.BeaconIntegrityOk);
        Assert.Null(evt.FingerprintVisitorId);
        Assert.Null(evt.WebdriverFlag);
        Assert.Null(evt.HeadlessBrowser);
        Assert.Null(evt.HoneypotTouched);
        Assert.True(float.IsNaN(evt.StorageAgeSec));
        Assert.True(float.IsNaN(evt.MouseEventCount));
        Assert.True(float.IsNaN(evt.TimeOnPageSec));

        Assert.Equal((ushort)45, evt.RetentionDays);         // from cached tenant config
        Assert.Equal(DateTimeKind.Utc, evt.TimestampUtc.Kind);
        Assert.Equal(FixedNow.UtcDateTime, evt.TimestampUtc);
    }

    [Fact]
    public async Task VelocityCounters_AreIncremented_WithNullClickId()
    {
        using var app = new PixelApp();
        using var client = app.Client();

        await client.SendAsync(Get($"/p.gif?k={SiteKey}&gclid=never-read"));

        var call = Assert.Single(app.Velocity.Clicks);
        Assert.Equal("203.0.113.10", call.Ip);
        Assert.Equal("TestUA/1.0", call.Ua);
        Assert.Null(call.ClickId);                           // pixels never pass a click id
    }

    [Fact]
    public async Task VelocityStoreThrowing_StillServesTheGif_Status200()
    {
        // NOTE: the handler resolves IVelocityStore optionally via
        // ctx.RequestServices.GetService (task step 3), but the full Program
        // composition always registers it (API-02's /c injects it as a
        // parameter, so removing the registration breaks minimal-API route
        // building app-wide). The absent-store path is therefore covered by
        // the GetService call itself; here we prove a *failing* store never
        // breaks the pixel response.
        using var app = new PixelApp();
        app.Velocity.Down = true;
        using var client = app.Client();

        var resp = await client.SendAsync(Get($"/p.gif?k={SiteKey}"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(ExpectedGif, await resp.Content.ReadAsByteArrayAsync());
        Assert.Empty(app.Sink.Events);                       // capture lost to analytics, not the page
    }

    [Fact]
    public async Task GraceEntry_ClickContextHash_AndTenantBookkeeping_AreWrittenToRedis()
    {
        using var app = new PixelApp();
        using var client = app.Client();
        const string sid = "0123456789abcdef0123456789abcdef";

        await client.SendAsync(Get($"/p.gif?k={SiteKey}&tg_sid={sid}",
            ("Referer", "https://shop.example/landing")));

        // Click-context hash: t:{tid}:click:{sid}, binding field-name contract,
        // kind=pixel, no click-id fields.
        var hash = Assert.Single(app.Redis.Hashes);
        Assert.Equal($"t:{Tid}:click:{sid}", hash.Key);
        var fields = hash.Entries.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        Assert.Equal("pixel", fields["kind"]);
        Assert.Equal(FixedNow.ToUnixTimeMilliseconds().ToString(), fields["ts"]);
        Assert.Equal("203.0.113.10", fields["ip"]);
        Assert.Equal("TestUA/1.0", fields["ua"]);
        Assert.Equal("en-US", fields["accept_language"]);
        Assert.Equal("https://shop.example/landing", fields["referrer"]);
        Assert.Contains("User-Agent", fields["header_order"]);
        Assert.Equal(SiteKey, fields["site_key"]);
        Assert.False(fields.ContainsKey("click_id"));
        Assert.False(fields.ContainsKey("click_id_type"));
        Assert.False(fields.ContainsKey("click_id_invalid"));
        Assert.False(fields.ContainsKey("campaign_id"));

        // TTL ~ Tracker:SessionTtlSeconds (1800 s).
        var expire = Assert.Single(app.Redis.Expires);
        Assert.Equal($"t:{Tid}:click:{sid}", expire.Key);
        Assert.Equal(TimeSpan.FromSeconds(1800), expire.Ttl);

        // Grace deadline = now + GraceSeconds (10), NX, plus tenant discovery.
        var zadd = Assert.Single(app.Redis.SortedSetAdds);
        Assert.Equal($"t:{Tid}:grace", zadd.Key);
        Assert.Equal(sid, zadd.Member);
        Assert.Equal(FixedNow.ToUnixTimeSeconds() + 10, zadd.Score);
        Assert.Equal(When.NotExists, zadd.When);
        var sadd = Assert.Single(app.Redis.SetAdds);
        Assert.Equal(("grace:tenants", Tid), sadd);
    }

    [Fact]
    public async Task PreseededGraceEntry_KeepsItsDeadline_NxSemantics()
    {
        using var app = new PixelApp();
        using var client = app.Client();
        const string sid = "0123456789abcdef0123456789abcdef";

        // A tracker hit already registered the grace entry with an earlier deadline.
        var trackerDeadline = FixedNow.ToUnixTimeSeconds() - 5 + 10;
        app.Redis.SortedSets[($"t:{Tid}:grace", sid)] = trackerDeadline;

        await client.SendAsync(Get($"/p.gif?k={SiteKey}&tg_sid={sid}"));

        // The handler attempted NX and the stored score is unchanged.
        var zadd = Assert.Single(app.Redis.SortedSetAdds);
        Assert.Equal(When.NotExists, zadd.When);
        Assert.Equal(trackerDeadline, app.Redis.SortedSets[($"t:{Tid}:grace", sid)]);
    }

    [Fact]
    public async Task PreseededTrackerContext_IsNeverClobberedWithPixel()
    {
        using var app = new PixelApp();
        using var client = app.Client();
        const string sid = "0123456789abcdef0123456789abcdef";

        // The tracker hit came first: hash exists with kind=tracker.
        app.Redis.PreseededKinds[$"t:{Tid}:click:{sid}"] = "tracker";

        var resp = await client.SendAsync(Get($"/p.gif?k={SiteKey}&tg_sid={sid}"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Empty(app.Redis.Hashes);                      // no HSET at all: kind=tracker preserved
        Assert.Empty(app.Redis.Expires);                     // and no TTL reset on the tracker's hash
        Assert.Single(app.Sink.Events);                      // the pixel event still flows to the sink
        Assert.Single(app.Redis.SortedSetAdds);              // and the grace NX attempt still happens
    }

    [Fact]
    public async Task RedisDown_StillServesTheGif_Status200()
    {
        using var app = new PixelApp(redisDown: true);
        using var client = app.Client();

        var resp = await client.SendAsync(Get($"/p.gif?k={SiteKey}"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("image/gif", resp.Content.Headers.ContentType?.MediaType);
        Assert.Equal(ExpectedGif, await resp.Content.ReadAsByteArrayAsync());
        Assert.True(resp.Headers.CacheControl?.NoStore);
        // The view is lost to analytics, never to the page.
        Assert.Empty(app.Sink.Events);
    }

    [Fact]
    public async Task UnknownSiteKey_MiddlewareServesTheIdenticalGif_AndNothingIsWritten()
    {
        using var app = new PixelApp();
        using var client = app.Client();

        var resp = await client.SendAsync(Get("/p.gif?k=who-dis"));

        // Anti-probing: success-shaped drop from DAT-04's middleware with the
        // SAME 43 bytes — known/unknown keys are indistinguishable to a prober.
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("image/gif", resp.Content.Headers.ContentType?.MediaType);
        var body = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(ExpectedGif, body);
        Assert.Contains("no-store", resp.Headers.CacheControl?.ToString());

        // The handler never ran and nothing was captured.
        Assert.Empty(app.Sink.Events);
        Assert.Empty(app.Redis.Hashes);
        Assert.Empty(app.Redis.SortedSetAdds);
        Assert.Empty(app.Velocity.Clicks);
        Assert.False(resp.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public void HandlerGif_IsByteIdentical_ToTheTaskSpecArray()
    {
        // The static array the endpoint serves IS the spec'd constant; the
        // middleware equivalence is proven end-to-end in the test above.
        Assert.Equal(ExpectedGif, PixelEndpoints.Gif);
    }
}
