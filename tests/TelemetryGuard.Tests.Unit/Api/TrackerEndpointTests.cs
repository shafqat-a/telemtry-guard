using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using StackExchange.Redis;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Core.Time;
using TelemetryGuard.Data.Models;
using TelemetryGuard.Data.Repositories;
using TelemetryGuard.Data.Tenancy;
using TelemetryGuard.RiskEngine.Velocity;

namespace TelemetryGuard.Tests.Unit.Api;

/// <summary>
/// API-02 tracker endpoint tests over the real Program composition via
/// WebApplicationFactory: happy-path 302 + cookie + click-id passthrough,
/// open-redirect hardening, click-id dedupe flags, grace/click-context Redis
/// writes, Redis-down resilience, cookie reuse, synthetic-bot labeling, and the
/// env-gated &lt;10 ms perf budget. All stores are in-memory fakes; Redis is an
/// NSubstitute IConnectionMultiplexer/IDatabase capture harness.
/// </summary>
public sealed class TrackerEndpointTests
{
    private static readonly Guid TenantGuid = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");
    private static readonly string Tid = TenantGuid.ToString("D");
    private const string SiteKey = "site-1";
    private static readonly Guid ActiveCampaign = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid InactiveCampaign = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string LandingUrl = "https://shop.example/landing?utm_source=ads";
    private static readonly IPAddress RemoteIp = IPAddress.Parse("203.0.113.10");
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);

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

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = FixedNow;
    }

    private sealed class FakeCampaignRepository : ICampaignRepository
    {
        public int RedirectCalls;

        public Task<CampaignRedirect?> GetRedirectAsync(Guid campaignId, CancellationToken ct)
        {
            Interlocked.Increment(ref RedirectCalls);
            CampaignRedirect? result = campaignId == ActiveCampaign
                ? new CampaignRedirect(LandingUrl, 0)
                : campaignId == InactiveCampaign
                    ? new CampaignRedirect(LandingUrl, 1)
                    : null;
            return Task.FromResult(result);
        }

        public Task CreateAsync(CampaignRecord campaign, CancellationToken ct) => throw new NotSupportedException();
        public Task<CampaignRecord?> GetAsync(Guid campaignId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CampaignRecord>> ListAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> UpdateAsync(CampaignRecord campaign, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeTenantRepository : ITenantRepository
    {
        public Task<TenantRecord?> GetCurrentAsync(CancellationToken ct)
            => Task.FromResult<TenantRecord?>(
                new TenantRecord(TenantGuid, "Acme", 0, 45, 0, FixedNow.UtcDateTime));

        public Task<bool> UpdateRetentionDaysAsync(int retentionDays, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> UpdateEnforcementModeAsync(byte enforcementMode, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>Scripts RSK-03's contract: true = first use, false = replay,
    /// null = no click id. Down=true simulates Redis being unreachable.</summary>
    private sealed class FakeVelocityStore : IVelocityStore
    {
        private readonly HashSet<string> _claimed = [];
        public bool Down;

        public Task<bool?> RecordClickAsync(string ip, string? userAgent, string? clickId, CancellationToken ct)
        {
            if (Down)
                throw new RedisConnectionException(ConnectionFailureType.UnableToConnect,
                    CommandFlags.None, "redis down (test)", null, CommandStatus.Unknown);
            if (string.IsNullOrWhiteSpace(ip))
                throw new ArgumentException("IP must not be null or empty.", nameof(ip));
            if (clickId is null)
                return Task.FromResult<bool?>(null);
            lock (_claimed)
                return Task.FromResult<bool?>(_claimed.Add(clickId));
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

    private sealed class CapturingLabelSink : ILabelSink
    {
        public readonly List<LabelEvent> Labels = [];

        public ValueTask WriteAsync(LabelEvent label, CancellationToken ct)
        {
            lock (Labels)
                Labels.Add(label);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>NSubstitute capture harness for the raw Redis calls the handler
    /// issues (click-context hash, TTL, grace sorted set, tenant bookkeeping set,
    /// synthetic NX guard). Down=true throws from GetDatabase().</summary>
    private sealed class RedisHarness
    {
        public readonly IConnectionMultiplexer Mux;
        public readonly List<(string Key, HashEntry[] Entries)> Hashes = [];
        public readonly List<(string Key, TimeSpan? Ttl)> Expires = [];
        public readonly List<(string Key, string Member, double Score)> SortedSetAdds = [];
        public readonly List<(string Key, string Member)> SetAdds = [];
        private readonly HashSet<string> _nxClaimed = [];

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

            db.When(d => d.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<HashEntry[]>()))
              .Do(ci => { lock (Hashes) Hashes.Add((ci.ArgAt<RedisKey>(0).ToString(), ci.ArgAt<HashEntry[]>(1))); });

            db.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>())
              .Returns(true)
              .AndDoes(ci => { lock (Expires) Expires.Add((ci.ArgAt<RedisKey>(0).ToString(), ci.ArgAt<TimeSpan?>(1))); });

            db.SortedSetAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<double>())
              .Returns(true)
              .AndDoes(ci =>
              {
                  lock (SortedSetAdds)
                      SortedSetAdds.Add((ci.ArgAt<RedisKey>(0).ToString(),
                          ci.ArgAt<RedisValue>(1).ToString(), ci.ArgAt<double>(2)));
              });

            db.SetAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>())
              .Returns(true)
              .AndDoes(ci =>
              {
                  lock (SetAdds)
                      SetAdds.Add((ci.ArgAt<RedisKey>(0).ToString(), ci.ArgAt<RedisValue>(1).ToString()));
              });

            db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>())
              .Returns(ci =>
              {
                  lock (_nxClaimed)
                      return _nxClaimed.Add(ci.ArgAt<RedisKey>(0).ToString());
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

    private sealed class TrackerApp : IDisposable
    {
        public readonly WebApplicationFactory<Program> Factory;
        public readonly FakeVelocityStore Velocity = new();
        public readonly CapturingEventSink Sink = new();
        public readonly CapturingLabelSink Labels = new();
        public readonly RedisHarness Redis;
        public readonly FakeClock Clock = new();
        public readonly FakeCampaignRepository Campaigns = new();

        public TrackerApp(Dictionary<string, string?>? settings = null, bool redisDown = false)
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
                if (settings is not null)
                    foreach (var (k, v) in settings) overrides[k] = v;
                foreach (var (k, v) in overrides)
                    b.UseSetting(k, v);
                b.ConfigureTestServices(services =>
                {
                    services.RemoveAll<ITenantResolver>();
                    services.AddSingleton<ITenantResolver>(new FakeResolver());
                    services.RemoveAll<ICampaignRepository>();
                    services.AddSingleton<ICampaignRepository>(Campaigns);
                    services.RemoveAll<ITenantRepository>();
                    services.AddSingleton<ITenantRepository>(new FakeTenantRepository());
                    services.RemoveAll<IVelocityStore>();
                    services.AddSingleton<IVelocityStore>(Velocity);
                    services.RemoveAll<IEventSink>();
                    services.AddSingleton<IEventSink>(Sink);
                    services.RemoveAll<ILabelSink>();
                    services.AddSingleton<ILabelSink>(Labels);
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

    private static string SidFrom(HttpResponseMessage resp)
    {
        Assert.NotNull(resp.Headers.Location);
        var query = QueryHelpers.ParseQuery(resp.Headers.Location!.Query);
        return query["tg_sid"].ToString();
    }

    // --------------------------------------------------------------- tests --

    [Fact]
    public async Task HappyPath_Returns302_WithTgSid_ClickIdPassthrough_AndSessionCookie()
    {
        using var app = new TrackerApp();
        using var client = app.Client();

        var resp = await client.SendAsync(Get($"/c?k={SiteKey}&cid={ActiveCampaign:D}&gclid=abc123"));

        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
        var loc = resp.Headers.Location;
        Assert.NotNull(loc);
        Assert.Equal("https://shop.example/landing", loc!.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(loc.Query);
        Assert.Equal("ads", query["utm_source"].ToString());       // campaign's own params survive
        Assert.Equal("abc123", query["gclid"].ToString());          // click-id passthrough
        var sid = query["tg_sid"].ToString();
        Assert.Matches("^[0-9a-f]{32}$", sid);

        var setCookie = Assert.Single(resp.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith($"tg_sid={sid}", setCookie);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=1800", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.True(resp.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task OpenRedirectAttempt_QueryParamsNeverFeedTheRedirectTarget()
    {
        using var app = new TrackerApp();
        using var client = app.Client();

        var resp = await client.SendAsync(Get(
            $"/c?k={SiteKey}&cid={ActiveCampaign:D}" +
            "&redirect=https%3A%2F%2Fevil.example&url=https%3A%2F%2Fevil.example" +
            "&dest=https%3A%2F%2Fevil.example&next=https%3A%2F%2Fevil.example"));

        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
        var loc = resp.Headers.Location;
        Assert.NotNull(loc);
        Assert.Equal("https://shop.example/landing", loc!.GetLeftPart(UriPartial.Path));
        Assert.DoesNotContain("evil.example", loc.ToString());
    }

    [Theory]
    [InlineData("/c?k=site-1")]                                              // cid missing
    [InlineData("/c?k=site-1&cid=")]                                         // cid empty
    [InlineData("/c?k=site-1&cid=not-a-guid")]                               // cid malformed
    [InlineData("/c?k=site-1&cid=33333333-3333-3333-3333-333333333333")]     // cid unknown
    [InlineData("/c?k=site-1&cid=22222222-2222-2222-2222-222222222222")]     // cid inactive
    public async Task MissingUnknownOrInactiveCampaign_Returns404(string path)
    {
        using var app = new TrackerApp();
        using var client = app.Client();

        var resp = await client.SendAsync(Get(path));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Empty(app.Sink.Events);   // nothing captured for a 404
    }

    [Fact]
    public async Task UnknownSiteKey_IsRejectedByTenantMiddleware_With404()
    {
        using var app = new TrackerApp();
        using var client = app.Client();

        var resp = await client.SendAsync(Get($"/c?k=who-dis&cid={ActiveCampaign:D}"));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Empty(app.Sink.Events);
    }

    [Fact]
    public async Task ReplayedClickId_RecordsInvalidFlag_FirstUseRecordsValid()
    {
        using var app = new TrackerApp();
        using var client = app.Client();

        var first = await client.SendAsync(Get($"/c?k={SiteKey}&cid={ActiveCampaign:D}&gclid=dup-1"));
        var second = await client.SendAsync(Get($"/c?k={SiteKey}&cid={ActiveCampaign:D}&gclid=dup-1"));

        Assert.Equal(HttpStatusCode.Found, first.StatusCode);
        Assert.Equal(HttpStatusCode.Found, second.StatusCode);
        Assert.Equal(2, app.Sink.Events.Count);
        Assert.Equal(false, app.Sink.Events[0].ClickIdInvalid);   // first use: valid
        Assert.Equal(true, app.Sink.Events[1].ClickIdInvalid);    // replay: invalid

        // The click-context hash mirrors the flag as "0"/"1".
        var flags = app.Redis.Hashes
            .Select(h => h.Entries.Single(e => e.Name == "click_id_invalid").Value.ToString())
            .ToArray();
        Assert.Equal(["0", "1"], flags);
    }

    [Fact]
    public async Task MissingClickId_OnPaidTrackerClick_RecordsInvalidFlag()
    {
        using var app = new TrackerApp();
        using var client = app.Client();

        var resp = await client.SendAsync(Get($"/c?k={SiteKey}&cid={ActiveCampaign:D}"));

        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
        var evt = Assert.Single(app.Sink.Events);
        Assert.Equal(true, evt.ClickIdInvalid);
        Assert.Equal("", evt.Gclid);
        var query = QueryHelpers.ParseQuery(resp.Headers.Location!.Query);
        Assert.False(query.ContainsKey("gclid"));   // nothing to pass through

        var hash = Assert.Single(app.Redis.Hashes);
        var fields = hash.Entries.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        Assert.Equal("", fields["click_id_type"]);  // absent recorded as absent, not fabricated
        Assert.Equal("", fields["click_id"]);
        Assert.Equal("1", fields["click_id_invalid"]);
    }

    [Fact]
    public async Task GraceEntry_ClickContextHash_AndTenantBookkeeping_AreWrittenToRedis()
    {
        using var app = new TrackerApp();
        using var client = app.Client();

        var resp = await client.SendAsync(Get($"/c?k={SiteKey}&cid={ActiveCampaign:D}&gclid=g-1"));
        var sid = SidFrom(resp);

        // Click-context hash: t:{tid}:click:{sid}, binding field-name contract.
        var hash = Assert.Single(app.Redis.Hashes);
        Assert.Equal($"t:{Tid}:click:{sid}", hash.Key);
        var fields = hash.Entries.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        Assert.Equal("tracker", fields["kind"]);
        Assert.Equal(FixedNow.ToUnixTimeMilliseconds().ToString(), fields["ts"]);
        Assert.Equal("203.0.113.10", fields["ip"]);
        Assert.Equal("TestUA/1.0", fields["ua"]);
        Assert.False(string.IsNullOrEmpty(fields["header_order"]));
        Assert.Contains("User-Agent", fields["header_order"]);
        Assert.Equal(SiteKey, fields["site_key"]);
        Assert.Equal(ActiveCampaign.ToString("D"), fields["campaign_id"]);
        Assert.Equal("gclid", fields["click_id_type"]);
        Assert.Equal("g-1", fields["click_id"]);
        Assert.Equal("0", fields["click_id_invalid"]);
        Assert.Equal("", fields["referrer"]);       // absent header -> empty string encoding

        // TTL ~ Tracker:SessionTtlSeconds (1800 s).
        var expire = Assert.Single(app.Redis.Expires);
        Assert.Equal($"t:{Tid}:click:{sid}", expire.Key);
        Assert.Equal(TimeSpan.FromSeconds(1800), expire.Ttl);

        // Grace deadline = now + GraceSeconds (10), plus worker tenant discovery.
        var zadd = Assert.Single(app.Redis.SortedSetAdds);
        Assert.Equal($"t:{Tid}:grace", zadd.Key);
        Assert.Equal(sid, zadd.Member);
        Assert.Equal(FixedNow.ToUnixTimeSeconds() + 10, zadd.Score);
        var sadd = Assert.Single(app.Redis.SetAdds);
        Assert.Equal(("grace:tenants", Tid), sadd);
    }

    [Fact]
    public async Task ExactlyOneTrackerClickEvent_ReachesTheSink_FullyPopulated()
    {
        using var app = new TrackerApp();
        using var client = app.Client();

        var resp = await client.SendAsync(Get(
            $"/c?k={SiteKey}&cid={ActiveCampaign:D}&gclid=abc123",
            ("Sec-CH-UA", "\"Chromium\";v=\"120\""),
            ("Sec-CH-UA-Mobile", "?0"),
            ("Sec-CH-UA-Platform", "\"Linux\""),
            ("Referer", "https://www.google.com/")));
        var sid = SidFrom(resp);

        var evt = Assert.Single(app.Sink.Events);
        Assert.Equal(new TenantId(TenantGuid), evt.TenantId);
        Assert.Equal(SiteKey, evt.SiteKey);
        Assert.Equal(sid, evt.SessionId);
        Assert.Equal(EventKind.Tracker, evt.Kind);
        Assert.Equal(ActiveCampaign.ToString("D"), evt.CampaignId);
        Assert.Equal("abc123", evt.Gclid);
        Assert.Equal("", evt.Fbclid);
        Assert.Equal("", evt.Msclkid);
        Assert.Equal("", evt.Ttclid);
        Assert.Equal(false, evt.ClickIdInvalid);
        Assert.Equal("203.0.113.10", evt.Ip);
        Assert.Equal("TestUA/1.0", evt.UserAgent);
        Assert.Equal("\"Chromium\";v=\"120\"", evt.SecChUa);
        Assert.Equal("?0", evt.SecChUaMobile);
        Assert.Equal("\"Linux\"", evt.SecChUaPlatform);
        Assert.Equal("en-US", evt.AcceptLanguage);
        Assert.Equal("https://www.google.com/", evt.Referrer);
        Assert.Contains("User-Agent", evt.HeaderNames);
        Assert.False(evt.HasJsBeacon);                       // tracker path: no JS ran
        Assert.Null(evt.WebdriverFlag);                      // missing signal != zero
        Assert.True(float.IsNaN(evt.StorageAgeSec));
        Assert.Equal((ushort)45, evt.RetentionDays);         // from tenant config (30..180)
        Assert.InRange(evt.RetentionDays, (ushort)30, (ushort)180);
        Assert.Equal(DateTimeKind.Utc, evt.TimestampUtc.Kind);
        Assert.Equal(FixedNow.UtcDateTime, evt.TimestampUtc);
    }

    [Fact]
    public async Task NonGoogleClickId_LandsInContextHash_AndItsDedicatedEventField()
    {
        using var app = new TrackerApp();
        using var client = app.Client();

        var resp = await client.SendAsync(Get($"/c?k={SiteKey}&cid={ActiveCampaign:D}&msclkid=ms-77"));

        var query = QueryHelpers.ParseQuery(resp.Headers.Location!.Query);
        Assert.Equal("ms-77", query["msclkid"].ToString());  // passthrough keeps the original param name
        var evt = Assert.Single(app.Sink.Events);
        Assert.Equal("ms-77", evt.Msclkid);
        Assert.Equal("", evt.Gclid);
        var hash = Assert.Single(app.Redis.Hashes);
        var fields = hash.Entries.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        Assert.Equal("msclkid", fields["click_id_type"]);
        Assert.Equal("ms-77", fields["click_id"]);
    }

    [Fact]
    public async Task RedisDown_StillRedirects302ToTheLandingPage()
    {
        using var app = new TrackerApp(redisDown: true);
        using var client = app.Client();

        var resp = await client.SendAsync(Get($"/c?k={SiteKey}&cid={ActiveCampaign:D}&gclid=abc123"));

        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
        var loc = resp.Headers.Location;
        Assert.NotNull(loc);
        Assert.Equal("https://shop.example/landing", loc!.GetLeftPart(UriPartial.Path));
        Assert.Matches("^[0-9a-f]{32}$", SidFrom(resp));
        // The click is lost to analytics, never to the advertiser (task step 4).
        Assert.Empty(app.Sink.Events);
    }

    [Fact]
    public async Task RepeatClick_WithExistingCookie_ReusesTheSid()
    {
        using var app = new TrackerApp();
        using var client = app.Client();
        const string existingSid = "0123456789abcdef0123456789abcdef";

        var resp = await client.SendAsync(Get(
            $"/c?k={SiteKey}&cid={ActiveCampaign:D}&gclid=abc123",
            ("Cookie", $"tg_sid={existingSid}")));

        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
        Assert.Equal(existingSid, SidFrom(resp));                       // no new sid minted
        var setCookie = Assert.Single(resp.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith($"tg_sid={existingSid}", setCookie);          // cookie value unchanged
        var evt = Assert.Single(app.Sink.Events);
        Assert.Equal(existingSid, evt.SessionId);
    }

    [Fact]
    public async Task MalformedCookieSid_IsReplaced_NotTrusted()
    {
        using var app = new TrackerApp();
        using var client = app.Client();

        var resp = await client.SendAsync(Get(
            $"/c?k={SiteKey}&cid={ActiveCampaign:D}",
            ("Cookie", "tg_sid=DROP TABLE; not-hex")));

        var sid = SidFrom(resp);
        Assert.Matches("^[0-9a-f]{32}$", sid);
        Assert.NotEqual("DROP TABLE", sid);
    }

    [Fact]
    public async Task CampaignLookups_AreCached_NotOnePerRequest()
    {
        using var app = new TrackerApp();
        using var client = app.Client();

        for (var i = 0; i < 5; i++)
            await client.SendAsync(Get($"/c?k={SiteKey}&cid={ActiveCampaign:D}"));

        Assert.Equal(1, app.Campaigns.RedirectCalls);   // 60 s IMemoryCache absorbed the rest
    }

    [Fact]
    public async Task SyntheticHeader_WithFlagEnabled_WritesExactlyOneFraudLabelPerSession()
    {
        using var app = new TrackerApp(settings: new() { ["Synthetic:Enabled"] = "true" });
        using var client = app.Client();
        const string sid = "0123456789abcdef0123456789abcdef";

        // Two clicks in the same session: the SETNX guard allows one label only.
        var first = await client.SendAsync(Get(
            $"/c?k={SiteKey}&cid={ActiveCampaign:D}&gclid=s-1",
            ("X-TG-Synthetic", "run-42"), ("Cookie", $"tg_sid={sid}")));
        var second = await client.SendAsync(Get(
            $"/c?k={SiteKey}&cid={ActiveCampaign:D}&gclid=s-2",
            ("X-TG-Synthetic", "run-42"), ("Cookie", $"tg_sid={sid}")));

        Assert.Equal(HttpStatusCode.Found, first.StatusCode);
        Assert.Equal(HttpStatusCode.Found, second.StatusCode);   // redirect identical either way
        var label = Assert.Single(app.Labels.Labels);
        Assert.Equal(new TenantId(TenantGuid), label.TenantId);
        Assert.Equal(sid, label.SessionId);
        Assert.Equal(LabelValues.Fraud, label.Label);
        Assert.Equal(LabelSources.SyntheticBot, label.LabelSource);
    }

    [Fact]
    public async Task SyntheticHeader_WithFlagDisabled_IsIgnored()
    {
        // The test host runs in the Development environment, whose appsettings
        // ships Synthetic:Enabled=true (API-04 step 10); force it false here to
        // simulate every non-Development config, where the flag stays false.
        using var app = new TrackerApp(settings: new() { ["Synthetic:Enabled"] = "false" });
        using var client = app.Client();

        var resp = await client.SendAsync(Get(
            $"/c?k={SiteKey}&cid={ActiveCampaign:D}&gclid=s-1", ("X-TG-Synthetic", "run-42")));

        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);     // redirect identical either way
        Assert.Empty(app.Labels.Labels);
    }

    /// <summary>
    /// Env-gated perf budget check (mirrors RSK-07's pattern): run only with
    /// RUN_PERF_TESTS=1; asserts p50 of the handler's own Activity duration is
    /// under the 10 ms tracker budget with all stores faked in-memory. CI does
    /// not set the variable, so this reports Skipped there.
    /// </summary>
    [SkippableFact]
    public async Task Perf_HandlerP50_IsUnder10Ms_WithInMemoryFakes()
    {
        Skip.If(Environment.GetEnvironmentVariable("RUN_PERF_TESTS") != "1",
            "Set RUN_PERF_TESTS=1 to run the tracker perf budget test.");

        var durations = new List<double>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "TelemetryGuard.Api.Tracker",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                lock (durations) durations.Add(a.Duration.TotalMilliseconds);
            },
        };
        ActivitySource.AddActivityListener(listener);

        using var app = new TrackerApp(settings: new()
        {
            ["RateLimiting:TokensPerSecond"] = "100000",
            ["RateLimiting:BucketSize"] = "100000",
        });
        using var client = app.Client();

        for (var i = 0; i < 20; i++)   // warm-up: JIT, caches, pipeline
            await client.SendAsync(Get($"/c?k={SiteKey}&cid={ActiveCampaign:D}&gclid=warm-{i}"));
        lock (durations) durations.Clear();

        for (var i = 0; i < 200; i++)
        {
            var resp = await client.SendAsync(Get($"/c?k={SiteKey}&cid={ActiveCampaign:D}&gclid=perf-{i}"));
            Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
        }

        double p50;
        lock (durations)
        {
            Assert.Equal(200, durations.Count);
            var sorted = durations.Order().ToArray();
            p50 = sorted[sorted.Length / 2];
        }
        Assert.True(p50 < 10.0, $"Tracker handler p50 was {p50:F2} ms; budget is < 10 ms.");
    }
}
