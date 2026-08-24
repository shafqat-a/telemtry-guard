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
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Api.Services;
using TelemetryGuard.Api.Options;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Core.Time;
using TelemetryGuard.Data.Models;
using TelemetryGuard.Data.Repositories;
using TelemetryGuard.Data.Tenancy;
using TelemetryGuard.RiskEngine.Velocity;

namespace TelemetryGuard.Tests.Unit.Api;

/// <summary>
/// API-04 beacon ingestion tests over the real Program composition via
/// WebApplicationFactory: /i/init contract (nonce + signed storage ts, ACAO *,
/// NO Set-Cookie), both content types and both sid shapes on POST /i, the
/// 64 KB cap, seq-0 acceptance + replay/gap handling across sequential POSTs,
/// nonce round-trip through a real /i/init call, site-liveness key, sink
/// cadence (1st / fp-bearing / every 10th), velocity capture semantics, and
/// the X-TG-Synthetic × Synthetic:Enabled matrix. Redis is an in-memory
/// NSubstitute IDatabase fake; sinks/repos are capturing fakes.
/// </summary>
public sealed class BeaconEndpointTests
{
    private static readonly Guid TenantGuid = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");
    private static readonly string Tid = TenantGuid.ToString("D");
    private const string SiteKey = "site-1";
    private const string HexSid = "9f8e7d6c5b4a39281706f5e4d3c2b1a0";       // tracker shape
    private const string UuidSid = "e58ed763-928c-4155-bee9-fdbaaadc15f3";  // randomUUID shape
    private const string Secret = "test-secret-0123456789abcdef-0123456789";
    private static readonly IPAddress RemoteIp = IPAddress.Parse("203.0.113.10");
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly long NowMs = FixedNow.ToUnixTimeMilliseconds();

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

    private sealed class FakeTenantRepository : ITenantRepository
    {
        public Task<TenantRecord?> GetCurrentAsync(CancellationToken ct)
            => Task.FromResult<TenantRecord?>(
                new TenantRecord(TenantGuid, "Acme", 0, 45, 0, FixedNow.UtcDateTime));

        public Task<bool> UpdateRetentionDaysAsync(int retentionDays, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> UpdateEnforcementModeAsync(byte enforcementMode, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class CapturingVelocityStore : IVelocityStore
    {
        public readonly List<(string Ip, string? Ua, string? VisitorId, string SessionId, bool StorageAgeZero)> Sessions = [];

        public Task RecordSessionAsync(string ip, string? userAgent, string? visitorId, string sessionId,
                                       bool storageAgeZero, CancellationToken ct)
        {
            lock (Sessions)
                Sessions.Add((ip, userAgent, visitorId, sessionId, storageAgeZero));
            return Task.CompletedTask;
        }

        public Task<bool?> RecordClickAsync(string ip, string? userAgent, string? clickId, CancellationToken ct)
            => throw new NotSupportedException();

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

    /// <summary>In-memory Redis over NSubstitute: strings (with NX + TTL) and
    /// hashes — everything the beacon pair touches.</summary>
    private sealed class RedisHarness
    {
        public readonly IConnectionMultiplexer Mux;
        public readonly Dictionary<string, string> Strings = [];
        public readonly Dictionary<string, TimeSpan?> StringTtls = [];
        public readonly Dictionary<string, Dictionary<string, string>> Hashes = [];
        public readonly Dictionary<string, Dictionary<string, double>> SortedSets = [];
        public readonly Dictionary<string, HashSet<string>> Sets = [];
        public readonly List<(string Key, TimeSpan? Ttl)> Expires = [];
        private readonly object _gate = new();

        public RedisHarness()
        {
            Mux = Substitute.For<IConnectionMultiplexer>();
            var db = Substitute.For<IDatabase>();
            Mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

            // Both overloads production code compiles against: the 3-arg call
            // (key, value, ttl) binds the keepTtl overload; the 4-arg call with
            // When.NotExists binds the (expiry, when) overload.
            db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>())
              .Returns(ci => SetString(
                  ci.ArgAt<RedisKey>(0).ToString(), ci.ArgAt<RedisValue>(1).ToString(),
                  ci.ArgAt<TimeSpan?>(2), ci.ArgAt<When>(3)));
            db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
                    Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
              .Returns(ci => SetString(
                  ci.ArgAt<RedisKey>(0).ToString(), ci.ArgAt<RedisValue>(1).ToString(),
                  ci.ArgAt<TimeSpan?>(2), ci.ArgAt<When>(4)));

            db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
              .Returns(ci =>
              {
                  lock (_gate)
                      return Strings.TryGetValue(ci.ArgAt<RedisKey>(0).ToString(), out var v)
                          ? (RedisValue)v
                          : RedisValue.Null;
              });

            db.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
              .Returns(ci =>
              {
                  lock (_gate)
                      return Hashes.TryGetValue(ci.ArgAt<RedisKey>(0).ToString(), out var h)
                          ? h.Select(kv => new HashEntry(kv.Key, kv.Value)).ToArray()
                          : Array.Empty<HashEntry>();
              });

            db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
              .Returns(ci =>
              {
                  lock (_gate)
                      return Hashes.ContainsKey(ci.ArgAt<RedisKey>(0).ToString())
                          || Strings.ContainsKey(ci.ArgAt<RedisKey>(0).ToString());
              });

            db.When(d => d.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<HashEntry[]>()))
              .Do(ci =>
              {
                  var key = ci.ArgAt<RedisKey>(0).ToString();
                  lock (_gate)
                  {
                      if (!Hashes.TryGetValue(key, out var h))
                          Hashes[key] = h = [];
                      foreach (var entry in ci.ArgAt<HashEntry[]>(1))
                          h[entry.Name.ToString()] = entry.Value.ToString();
                  }
              });

            db.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>())
              .Returns(true)
              .AndDoes(ci =>
              {
                  lock (_gate)
                      Expires.Add((ci.ArgAt<RedisKey>(0).ToString(), ci.ArgAt<TimeSpan?>(1)));
              });

            db.SortedSetAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<double>(),
                    Arg.Any<When>(), Arg.Any<CommandFlags>())
              .Returns(ci => AddSorted(
                  ci.ArgAt<RedisKey>(0).ToString(), ci.ArgAt<RedisValue>(1).ToString(),
                  ci.ArgAt<double>(2), ci.ArgAt<When>(3)));

            db.SetAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
              .Returns(ci =>
              {
                  lock (_gate)
                  {
                      var key = ci.ArgAt<RedisKey>(0).ToString();
                      if (!Sets.TryGetValue(key, out var set)) Sets[key] = set = [];
                      return set.Add(ci.ArgAt<RedisValue>(1).ToString());
                  }
              });
        }

        private bool AddSorted(string key, string member, double score, When when)
        {
            lock (_gate)
            {
                if (!SortedSets.TryGetValue(key, out var set)) SortedSets[key] = set = [];
                if (when == When.NotExists && set.ContainsKey(member)) return false;
                var added = !set.ContainsKey(member);
                set[member] = score;
                return added;
            }
        }

        private bool SetString(string key, string value, TimeSpan? ttl, When when)
        {
            lock (_gate)
            {
                if (when == When.NotExists && Strings.ContainsKey(key))
                    return false;
                Strings[key] = value;
                StringTtls[key] = ttl;
                return true;
            }
        }

        public Dictionary<string, string> SessionHash(string sid)
        {
            lock (_gate)
                return Hashes.TryGetValue($"t:{Tid}:sess:{sid}", out var h)
                    ? new Dictionary<string, string>(h)
                    : [];
        }
    }

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

    private sealed class BeaconApp : IDisposable
    {
        public readonly WebApplicationFactory<Program> Factory;
        public readonly RedisHarness Redis = new();
        public readonly CapturingVelocityStore Velocity = new();
        public readonly CapturingEventSink Sink = new();
        public readonly CapturingLabelSink Labels = new();
        public readonly FakeClock Clock = new();

        public BeaconApp(Dictionary<string, string?>? settings = null, IPAddress? remoteIp = null)
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
                    ["Beacon:HmacSecret"] = Secret,
                    ["RateLimiting:TokensPerSecond"] = "10000",
                    ["RateLimiting:BucketSize"] = "10000",
                };
                if (settings is not null)
                    foreach (var (k, v) in settings) overrides[k] = v;
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
                    services.RemoveAll<ILabelSink>();
                    services.AddSingleton<ILabelSink>(Labels);
                    services.RemoveAll<IConnectionMultiplexer>();
                    services.AddSingleton(Redis.Mux);
                    services.RemoveAll<IClock>();
                    services.AddSingleton<IClock>(Clock);
                    services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(remoteIp ?? RemoteIp));
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

    private static string Envelope(
        long seq, string eventsJson, string nonce = "", string sid = HexSid,
        string k = SiteKey, long? sentAt = null)
        => $"{{\"k\":\"{k}\",\"sid\":\"{sid}\",\"seq\":{seq},\"nonce\":\"{nonce}\"," +
           $"\"sent_at\":{sentAt ?? NowMs},\"events\":{eventsJson}}}";

    /// <summary>The shipped SDK envelope shape (3afde1a): session_id is the persistent
    /// session, sid == visit_id is the per-document-load identity the server scopes the
    /// aggregate hash, nonce and seq counter on.</summary>
    private static string VisitEnvelope(
        long seq, string eventsJson, string sessionId, string visitId, string nonce = "")
        => $"{{\"k\":\"{SiteKey}\",\"session_id\":\"{sessionId}\",\"sid\":\"{visitId}\"," +
           $"\"visit_id\":\"{visitId}\",\"seq\":{seq},\"nonce\":\"{nonce}\"," +
           $"\"sent_at\":{NowMs},\"events\":{eventsJson}}}";

    private static HttpRequestMessage Post(
        string body, string contentType = "text/plain", string k = SiteKey,
        params (string Name, string Value)[] headers)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/i?k={Uri.EscapeDataString(k)}")
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };
        req.Headers.TryAddWithoutValidation("User-Agent", "TestUA/1.0");
        foreach (var (name, value) in headers) req.Headers.TryAddWithoutValidation(name, value);
        return req;
    }

    private static string FpEvents(long ts, string sig, string vid = "v-1")
        => "[{\"e\":\"fp\",\"t\":700,\"vid\":\"" + vid + "\",\"conf\":0.9,\"scr\":[1920,1080,24,2]," +
           "\"tz\":\"UTC\",\"langs\":[\"en-US\"],\"canvasBlocked\":false,\"touch\":false,\"mob\":false," +
           "\"wd\":false,\"botd\":{\"bot\":false},\"storage\":{\"ck\":{\"present\":true,\"ts\":" + ts +
           ",\"sig\":\"" + sig + "\"},\"ls\":{\"present\":false},\"fresh\":true,\"cookiesDisabled\":false}}]";

    // ------------------------------------------------------------- /i/init --

    [Fact]
    public async Task Init_ReturnsNonceAndSignedStorageTs_WithWildcardCors_AndNoCookies()
    {
        using var app = new BeaconApp();
        using var client = app.Client();

        var resp = await client.GetAsync($"/i/init?k={SiteKey}&sid={HexSid}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("*", Assert.Single(resp.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.False(resp.Headers.Contains("Set-Cookie"));                    // credentials:'omit' — pointless AND forbidden
        Assert.False(resp.Headers.Contains("Access-Control-Allow-Credentials"));

        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        var nonce = body.GetProperty("nonce").GetString()!;
        var storageTs = body.GetProperty("storageTs").GetInt64();
        var storageSig = body.GetProperty("storageSig").GetString()!;

        Assert.Matches("^[0-9a-f]{32}$", nonce);
        Assert.Equal(NowMs, storageTs);
        Assert.Matches("^[0-9a-f]{64}$", storageSig);
        Assert.Equal(SessionAggregator.ComputeStorageSig(Secret, Tid, storageTs), storageSig);

        // Nonce persisted under t:{tid}:nonce:{sid} with the configured TTL.
        Assert.Equal(nonce, app.Redis.Strings[$"t:{Tid}:nonce:{HexSid}"]);
        Assert.Equal(TimeSpan.FromSeconds(1800), app.Redis.StringTtls[$"t:{Tid}:nonce:{HexSid}"]); // == SessionTtlSeconds by default
    }

    [Theory]
    [InlineData(HexSid)]
    [InlineData(UuidSid)]
    public async Task Init_AcceptsBothSidShapes(string sid)
    {
        using var app = new BeaconApp();
        using var client = app.Client();

        var resp = await client.GetAsync($"/i/init?k={SiteKey}&sid={sid}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Theory]
    [InlineData("ab")]                                       // too short
    [InlineData("abc%25def12345")]                           // contains %
    [InlineData("abcd.efgh.1234")]                           // contains .
    public async Task Init_MalformedSid_IsDroppedWith204(string sid)
    {
        using var app = new BeaconApp();
        using var client = app.Client();

        var resp = await client.GetAsync($"/i/init?k={SiteKey}&sid={sid}");

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Empty(app.Redis.Strings);   // no nonce minted
    }

    [Fact]
    public async Task Init_UnknownSiteKey_GetsSuccessShaped204()
    {
        using var app = new BeaconApp();
        using var client = app.Client();

        var resp = await client.GetAsync($"/i/init?k=who-dis&sid={HexSid}");

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Empty(app.Redis.Strings);
    }

    // ------------------------------------------------------------------ /i --

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/json")]
    public async Task Post_EitherContentType_AggregatesIntoTheSessionHash(string contentType)
    {
        using var app = new BeaconApp();
        using var client = app.Client();

        var resp = await client.SendAsync(Post(Envelope(0,
            """[{"e":"pv","t":12},{"e":"pm","t":300,"s":[[0,0,0],[100,3,4],[300,6,8]]}]"""),
            contentType));

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        var hash = app.Redis.SessionHash(HexSid);
        Assert.Equal("1", hash["n_pv"]);
        Assert.Equal("2", hash["mm_n"]);
        Assert.Equal(150, double.Parse(hash["mm_mean_ms"]));
        Assert.Equal(5000, double.Parse(hash["mm_m2"]));
        Assert.Equal(10, double.Parse(hash["mm_path_len"]));
        Assert.Equal("0", hash["last_seq"]);
        Assert.Equal("2", hash["n_events_total"]);

        // TTL ~ Beacon:SessionTtlSeconds on the session hash.
        Assert.Contains(($"t:{Tid}:sess:{HexSid}", (TimeSpan?)TimeSpan.FromSeconds(1800)), app.Redis.Expires);

        // Site liveness for D22 / API-07.
        Assert.Equal(FixedNow.ToUnixTimeSeconds().ToString(), app.Redis.Strings[$"t:{Tid}:site:{SiteKey}:lastbeacon"]);
        Assert.Equal(TimeSpan.FromSeconds(604800), app.Redis.StringTtls[$"t:{Tid}:site:{SiteKey}:lastbeacon"]);
    }

    [Fact]
    public async Task Post_UuidSidShape_IsAccepted()
    {
        using var app = new BeaconApp();
        using var client = app.Client();

        var resp = await client.SendAsync(Post(Envelope(0, """[{"e":"pv","t":1}]""", sid: UuidSid)));

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Equal("1", app.Redis.SessionHash(UuidSid)["n_pv"]);
    }

    [Fact]
    public async Task Post_BodyOverCap_413_UnderCap_Accepted()
    {
        using var app = new BeaconApp();
        using var client = app.Client();

        var big = Envelope(0, "[]")[..^1] + ",\"pad\":\"" + new string('x', 70_000) + "\"}";
        var over = await client.SendAsync(Post(big));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
        Assert.Empty(app.Redis.SessionHash(HexSid));   // nothing aggregated

        var medium = Envelope(0, "[]")[..^1] + ",\"pad\":\"" + new string('x', 59_000) + "\"}";
        var under = await client.SendAsync(Post(medium));
        Assert.Equal(HttpStatusCode.NoContent, under.StatusCode);
        Assert.Equal("1", app.Redis.SessionHash(HexSid)["n_beacons"]);
    }

    [Fact]
    public async Task Post_SeqZeroAccepted_ReplayDropped_GapCounted_AcrossSequentialPosts()
    {
        using var app = new BeaconApp();
        using var client = app.Client();

        // seq 0 on a fresh session MUST be accepted (last_seq absent => -1).
        var first = await client.SendAsync(Post(Envelope(0, """[{"e":"pv","t":1}]""")));
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal("1", app.Redis.SessionHash(HexSid)["n_pv"]);

        // Replay of seq 0: events dropped, replay counted, still 204.
        var replay = await client.SendAsync(Post(Envelope(0, """[{"e":"pv","t":2}]""")));
        Assert.Equal(HttpStatusCode.NoContent, replay.StatusCode);
        var afterReplay = app.Redis.SessionHash(HexSid);
        Assert.Equal("1", afterReplay["n_pv"]);
        Assert.Equal("1", afterReplay["seq_replays"]);

        // Gap 0 -> 3: two missing envelopes recorded.
        var gap = await client.SendAsync(Post(Envelope(3, """[{"e":"pv","t":3}]""")));
        Assert.Equal(HttpStatusCode.NoContent, gap.StatusCode);
        var afterGap = app.Redis.SessionHash(HexSid);
        Assert.Equal("2", afterGap["n_pv"]);
        Assert.Equal("2", afterGap["seq_gaps"]);
        Assert.Equal("3", afterGap["last_seq"]);
    }

    [Fact]
    public async Task Post_AcceptedBatches_RegisterAndRefreshBeaconQuietPeriod_ReplayDoesNot()
    {
        using var app = new BeaconApp(new() { ["Beacon:FinalizeQuietSeconds"] = "30" });
        using var client = app.Client();
        var graceKey = $"t:{Tid}:grace";

        await client.SendAsync(Post(Envelope(0, "[]")));
        Assert.Equal(FixedNow.AddSeconds(30).ToUnixTimeSeconds(),
            app.Redis.SortedSets[graceKey][HexSid]);
        Assert.Contains(Tid, app.Redis.Sets["grace:tenants"]);
        var context = app.Redis.Hashes[$"t:{Tid}:click:{HexSid}"];
        Assert.Equal("beacon", context["kind"]);
        Assert.Equal(RemoteIp.ToString(), context["ip"]);
        Assert.Equal(SiteKey, context["site_key"]);

        app.Clock.UtcNow = FixedNow.AddSeconds(12);
        await client.SendAsync(Post(Envelope(1, "[]", sentAt: app.Clock.UtcNow.ToUnixTimeMilliseconds())));
        Assert.Equal(FixedNow.AddSeconds(42).ToUnixTimeSeconds(),
            app.Redis.SortedSets[graceKey][HexSid]);

        app.Clock.UtcNow = FixedNow.AddSeconds(20);
        await client.SendAsync(Post(Envelope(1, "[]", sentAt: app.Clock.UtcNow.ToUnixTimeMilliseconds())));
        Assert.Equal(FixedNow.AddSeconds(42).ToUnixTimeSeconds(),
            app.Redis.SortedSets[graceKey][HexSid]);
    }

    /// <summary>The page-2 regression: one browser session, two document loads. The
    /// SDK restarts seq at 0 and fetches a fresh nonce on every load, so nonce and
    /// last_seq MUST be scoped per visit — otherwise every multi-page visit reads as a
    /// replayed, tampered beacon (integrity_fails → the beacon_integrity_failed T1
    /// floor of 85) and its events are dropped.</summary>
    [Fact]
    public async Task Post_TwoVisitsOfOneSession_EachStartAtSeqZero_NeitherIsAReplay()
    {
        using var app = new BeaconApp();
        using var client = app.Client();
        const string sessionId = "9f8e7d6c5b4a39281706f5e4d3c2b1a0";
        const string visit1 = "11111111-2222-4333-8444-555555555555";
        const string visit2 = "66666666-7777-4888-9999-aaaaaaaaaaaa";
        const string pv = """[{"e":"pv","t":1}]""";

        async Task<string> InitAsync(string visitId)
        {
            var init = await client.GetAsync($"/i/init?k={SiteKey}&sid={visitId}");
            return JsonSerializer.Deserialize<JsonElement>(await init.Content.ReadAsStringAsync())
                .GetProperty("nonce").GetString()!;
        }

        // Visit 1 (landing page): init, then seq 0 and 1.
        var nonce1 = await InitAsync(visit1);
        await client.SendAsync(Post(VisitEnvelope(0, pv, sessionId, visit1, nonce1)));
        await client.SendAsync(Post(VisitEnvelope(1, pv, sessionId, visit1, nonce1)));

        // Visit 2 (next page in the same tab): a NEW init overwrites nothing of visit 1,
        // and seq restarts at 0 without being a replay.
        var nonce2 = await InitAsync(visit2);
        Assert.NotEqual(nonce1, nonce2);
        var second = await client.SendAsync(Post(VisitEnvelope(0, pv, sessionId, visit2, nonce2)));
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        // Visit 1's late pagehide flush (racing visit 2's init) still verifies.
        await client.SendAsync(Post(VisitEnvelope(2, pv, sessionId, visit1, nonce1)));

        var h1 = app.Redis.SessionHash(visit1);
        var h2 = app.Redis.SessionHash(visit2);
        Assert.Equal("3", h1["n_pv"]);
        Assert.Equal("2", h1["last_seq"]);
        Assert.Equal("1", h2["n_pv"]);
        Assert.Equal("0", h2["last_seq"]);
        foreach (var h in new[] { h1, h2 })
        {
            Assert.Equal("1", h["nonce_ok"]);
            Assert.DoesNotContain("seq_replays", h.Keys);
            Assert.DoesNotContain("integrity_fails", h.Keys);
        }

        // The nonce lives at least as long as the visit's aggregate hash.
        Assert.True(app.Redis.StringTtls[$"t:{Tid}:nonce:{visit1}"]
            >= TimeSpan.FromSeconds(new BeaconOptions().SessionTtlSeconds));

        // Journey linkage survives: both visits carry the canonical session id.
        Assert.Equal(sessionId, app.Redis.Hashes[$"t:{Tid}:click:{visit1}"]["session_id"]);
        Assert.Equal(sessionId, app.Redis.Hashes[$"t:{Tid}:click:{visit2}"]["session_id"]);
    }

    [Fact]
    public async Task Post_NonceRoundTrip_ThroughARealInitCall()
    {
        using var app = new BeaconApp();
        using var client = app.Client();

        var init = await client.GetAsync($"/i/init?k={SiteKey}&sid={HexSid}");
        var nonce = JsonSerializer.Deserialize<JsonElement>(await init.Content.ReadAsStringAsync())
            .GetProperty("nonce").GetString()!;

        await client.SendAsync(Post(Envelope(0, """[{"e":"pv","t":1}]""", nonce: nonce)));
        var hash = app.Redis.SessionHash(HexSid);
        Assert.Equal("1", hash["nonce_ok"]);
        Assert.DoesNotContain("integrity_fails", hash.Keys);

        // Wrong nonce on the next envelope: recorded, never rejected.
        await client.SendAsync(Post(Envelope(1, """[{"e":"pv","t":2}]""",
            nonce: "ffffffffffffffffffffffffffffffff")));
        hash = app.Redis.SessionHash(HexSid);
        Assert.Equal("0", hash["nonce_ok"]);
        Assert.Equal("1", hash["integrity_fails"]);
        Assert.Equal("2", hash["n_pv"]);   // events still aggregated
    }

    [Fact]
    public async Task Post_SinkCadence_FirstEveryTenth_AndFpBearing()
    {
        using var app = new BeaconApp();
        using var client = app.Client();

        for (var seq = 0; seq < 10; seq++)
            await client.SendAsync(Post(Envelope(seq, """[{"e":"pv","t":1}]""")));

        // 10 accepted beacons without fp/fs => snapshots at #1 and #10 only.
        Assert.Equal(2, app.Sink.Events.Count);
        Assert.All(app.Sink.Events, e =>
        {
            Assert.Equal(EventKind.Beacon, e.Kind);
            Assert.Equal(new TenantId(TenantGuid), e.TenantId);
            Assert.Equal(SiteKey, e.SiteKey);
            Assert.Equal(HexSid, e.SessionId);
            Assert.True(e.HasJsBeacon);
            Assert.Equal("203.0.113.10", e.Ip);
            Assert.Equal((ushort)45, e.RetentionDays);   // tenant config, cached (D20)
        });
        Assert.True(float.IsNaN(app.Sink.Events[0].StorageAgeSec));   // missing != zero

        // An fp-bearing batch triggers an immediate snapshot (#11).
        var sig = SessionAggregator.ComputeStorageSig(Secret, Tid, NowMs);
        await client.SendAsync(Post(Envelope(10, FpEvents(NowMs, sig))));
        Assert.Equal(3, app.Sink.Events.Count);
        var fpEvent = app.Sink.Events[^1];
        Assert.Equal("v-1", fpEvent.FingerprintVisitorId);
        Assert.Equal(0f, fpEvent.StorageAgeSec);
        Assert.Equal(1920f, fpEvent.ScreenWidth);
        Assert.Equal(false, fpEvent.WebdriverFlag);
        Assert.Equal(10f, fpEvent.PagesViewed);
    }

    /// <summary>INT-05: no Cloudflare fronting -> the beacon's own snapshot
    /// ClickEvent carries null TlsJa3/TlsJa4/CfAsn (missing ≠ zero), while the
    /// organic-beacon HTTP context is retained for later scoring.</summary>
    [Fact]
    public async Task EdgeSignals_Absent_SnapshotClickEventStaysNull_NoClickContextHashWritten()
    {
        using var app = new BeaconApp();
        using var client = app.Client();

        await client.SendAsync(Post(Envelope(0, """[{"e":"pv","t":1}]""")));

        var evt = Assert.Single(app.Sink.Events);
        Assert.Null(evt.TlsJa3);
        Assert.Null(evt.TlsJa4);
        Assert.Null(evt.CfAsn);
        Assert.Equal("", app.Redis.Hashes[$"t:{Tid}:click:{HexSid}"]["tls_fp"]);
    }

    /// <summary>INT-05: Cloudflare-fronted + a Cloudflare-range direct peer -> the
    /// snapshot ClickEvent for THIS beacon POST carries TlsJa3/TlsJa4/CfAsn (analytics
    /// completeness for the beacon path) and the fingerprint is retained for scoring.</summary>
    [Fact]
    public async Task EdgeSignals_Present_WhenCloudflareFronted_PopulatesTheSnapshotClickEvent()
    {
        using var app = new BeaconApp(
            settings: new() { ["Edge:Provider"] = "Cloudflare" },
            remoteIp: IPAddress.Parse("104.16.1.1"));
        using var client = app.Client();

        await client.SendAsync(Post(Envelope(0, """[{"e":"pv","t":1}]"""),
            headers: [("X-TG-JA3", "cd08e31494f9531f560d64c695473da9"), ("X-TG-ASN", "13335")]));

        var evt = Assert.Single(app.Sink.Events);
        Assert.Equal("cd08e31494f9531f560d64c695473da9", evt.TlsJa3);
        Assert.Null(evt.TlsJa4);
        Assert.Equal(13335u, evt.CfAsn);
        Assert.Equal("cd08e31494f9531f560d64c695473da9",
            app.Redis.Hashes[$"t:{Tid}:click:{HexSid}"]["tls_fp"]);
    }

    [Fact]
    public async Task Post_VelocityCapture_OncePerAcceptedBatch_StorageAgeZeroOnlyWhenVerified()
    {
        using var app = new BeaconApp();
        using var client = app.Client();

        // 1: no fp yet -> visitorId null, storageAgeZero false.
        await client.SendAsync(Post(Envelope(0, """[{"e":"pv","t":1}]""")));
        // 2: fp with VERIFIED zero-age storage pair -> storageAgeZero true.
        var sig = SessionAggregator.ComputeStorageSig(Secret, Tid, NowMs);
        await client.SendAsync(Post(Envelope(1, FpEvents(NowMs, sig))));
        // 3: replay -> dropped batch, NO velocity capture.
        await client.SendAsync(Post(Envelope(1, """[{"e":"pv","t":9}]""")));

        Assert.Equal(2, app.Velocity.Sessions.Count);
        var (ip1, ua1, vid1, sid1, zero1) = app.Velocity.Sessions[0];
        Assert.Equal(("203.0.113.10", "TestUA/1.0", null, HexSid, false), (ip1, ua1, vid1, sid1, zero1));
        var (_, _, vid2, _, zero2) = app.Velocity.Sessions[1];
        Assert.Equal("v-1", vid2);
        Assert.True(zero2);
    }

    [Fact]
    public async Task Post_TamperedStorageSig_NeverCountsAsZeroAge()
    {
        using var app = new BeaconApp();
        using var client = app.Client();

        await client.SendAsync(Post(Envelope(0, FpEvents(NowMs,
            "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef"))));

        var (_, _, _, _, storageAgeZero) = Assert.Single(app.Velocity.Sessions);
        Assert.False(storageAgeZero);
        var hash = app.Redis.SessionHash(HexSid);
        Assert.Equal("0", hash["storage_sig_ok"]);
        Assert.DoesNotContain("storage_age_sec", hash.Keys);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"k":"site-1","sid":"ab","seq":0,"nonce":"","sent_at":1,"events":[]}""")]     // bad sid
    [InlineData("""{"k":"other-site","sid":"9f8e7d6c5b4a39281706f5e4d3c2b1a0","seq":0,"nonce":"","sent_at":1,"events":[]}""")] // body k != query k
    [InlineData("""{"k":"site-1","sid":"9f8e7d6c5b4a39281706f5e4d3c2b1a0","seq":-1,"nonce":"","sent_at":1,"events":[]}""")]    // negative seq
    [InlineData("""{"k":"site-1","sid":"9f8e7d6c5b4a39281706f5e4d3c2b1a0","seq":0,"nonce":"","sent_at":1,"events":"nope"}""")] // events not an array
    public async Task Post_InvalidEnvelope_SilentDrop204_NothingWritten(string body)
    {
        using var app = new BeaconApp();
        using var client = app.Client();

        var resp = await client.SendAsync(Post(body));

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Empty(app.Redis.Hashes);
        Assert.Empty(app.Sink.Events);
        Assert.Empty(app.Velocity.Sessions);
    }

    [Fact]
    public async Task Post_SiteKeyFromEnvelopeOnly_IsAccepted_AsTheShippedSdkSendsIt()
    {
        // SDK-02 is the canonical wire contract and puts `k` INSIDE the envelope for
        // POST /i — only /i/init and /decide take it as a query param. The server used to
        // require the query form, so every real beacon was silently 204'd: bundle loads,
        // /i/init succeeds, POST /i returns 204, and nothing is ever recorded.
        using var app = new BeaconApp();
        using var client = app.Client();

        var req = new HttpRequestMessage(HttpMethod.Post, "/i")   // no ?k=
        {
            Content = new StringContent(
                Envelope(0, """[{"e":"pv","t":1}]"""), Encoding.UTF8, "text/plain"),
        };
        req.Headers.TryAddWithoutValidation("User-Agent", "TestUA/1.0");

        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.NotEmpty(app.Redis.Hashes);   // session aggregate written
        Assert.NotEmpty(app.Sink.Events);    // first beacon always snapshots
    }

    [Fact]
    public async Task Post_UnknownSiteKeyInEnvelopeOnly_StaysASuccessShapedDrop()
    {
        using var app = new BeaconApp();
        using var client = app.Client();

        var req = new HttpRequestMessage(HttpMethod.Post, "/i")
        {
            Content = new StringContent(
                Envelope(0, """[{"e":"pv","t":1}]""", k: "who-dis"), Encoding.UTF8, "text/plain"),
        };

        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Empty(app.Redis.Hashes);
        Assert.Empty(app.Sink.Events);
    }

    [Fact]
    public async Task Post_QuerySiteKeyStillWins_AndAMismatchedEnvelopeKeyIsStillDropped()
    {
        // The query form must keep working: it is what lets the tenant middleware (and
        // the per-tenant rate-limit partition) resolve a beacon before the handler runs.
        using var app = new BeaconApp();
        using var client = app.Client();

        var accepted = await client.SendAsync(Post(Envelope(0, """[{"e":"pv","t":1}]""")));
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.NotEmpty(app.Redis.Hashes);

        using var app2 = new BeaconApp();
        using var client2 = app2.Client();
        var mismatched = await client2.SendAsync(Post(
            Envelope(0, """[{"e":"pv","t":1}]""", k: "tg_sk_someoneelse")));   // query != body
        Assert.Equal(HttpStatusCode.NoContent, mismatched.StatusCode);
        Assert.Empty(app2.Redis.Hashes);
    }

    [Fact]
    public async Task Post_UnknownSiteKey_SuccessShaped204_IndistinguishableFromAccepted()
    {
        using var app = new BeaconApp();
        using var client = app.Client();

        var resp = await client.SendAsync(Post(
            Envelope(0, """[{"e":"pv","t":1}]""", k: "who-dis"), k: "who-dis"));

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Empty(app.Redis.Hashes);
        Assert.Empty(app.Sink.Events);
    }

    // ---------------------------------------------------- synthetic labels --

    [Fact]
    public async Task Synthetic_FlagOnAndHeader_WritesExactlyOneFraudLabelPerSession()
    {
        using var app = new BeaconApp(settings: new() { ["Synthetic:Enabled"] = "true" });
        using var client = app.Client();

        await client.SendAsync(Post(Envelope(0, """[{"e":"pv","t":1}]"""),
            "text/plain", SiteKey, ("X-TG-Synthetic", "run-42")));
        await client.SendAsync(Post(Envelope(1, """[{"e":"pv","t":2}]"""),
            "text/plain", SiteKey, ("X-TG-Synthetic", "run-42")));

        var label = Assert.Single(app.Labels.Labels);   // SETNX guard: once per session
        Assert.Equal(new TenantId(TenantGuid), label.TenantId);
        Assert.Equal(HexSid, label.SessionId);
        Assert.Equal(LabelValues.Fraud, label.Label);
        Assert.Equal(LabelSources.SyntheticBot, label.LabelSource);
    }

    [Fact]
    public async Task Synthetic_FlagOnWithoutHeader_NoLabel()
    {
        using var app = new BeaconApp(settings: new() { ["Synthetic:Enabled"] = "true" });
        using var client = app.Client();

        await client.SendAsync(Post(Envelope(0, """[{"e":"pv","t":1}]""")));

        Assert.Empty(app.Labels.Labels);
    }

    [Fact]
    public async Task Synthetic_FlagOff_HeaderIsIgnored()
    {
        // The test host runs in the Development environment, whose appsettings
        // ships Synthetic:Enabled=true (API-04 step 10); force it false here to
        // simulate every non-Development config, where the flag stays false.
        using var app = new BeaconApp(settings: new() { ["Synthetic:Enabled"] = "false" });
        using var client = app.Client();

        var resp = await client.SendAsync(Post(Envelope(0, """[{"e":"pv","t":1}]"""),
            "text/plain", SiteKey, ("X-TG-Synthetic", "run-42")));

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);   // identical either way
        Assert.Empty(app.Labels.Labels);
    }
}
