using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using StackExchange.Redis;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Api.Auth;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Core.Time;
using TelemetryGuard.Data.Models;
using TelemetryGuard.Data.Repositories;
using TelemetryGuard.Data.Tenancy;

namespace TelemetryGuard.Tests.Unit.Api;

/// <summary>
/// API-07 /admin/* tests over the real Program composition via
/// WebApplicationFactory: the AdminScopeFilter backstop (both in isolation and
/// through the full pipeline), whitelist CRUD (validation, the single AddAsync
/// call carrying the exact NewWhitelistEntry, no endpoint-side Redis/ILabelSink
/// use), the summary report's avgScore math and range validation, and the
/// integration-status report's js/http-only classification.
///
/// IWhitelistRepository/ISiteRepository/IVerdictSummaryRepository/ITenantResolver
/// are hand-rolled fakes (same pattern as API-05/06's tests); Redis is an
/// NSubstitute IConnectionMultiplexer/IDatabase capture harness.
/// </summary>
public sealed class AdminEndpointTests
{
    private static readonly Guid TenantGuid = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");
    private const string ApiKey = "admin-test-api-key";
    private static readonly Guid CampaignGuid = Guid.Parse("8b7c1234-0000-0000-0000-0000000000ab");

    // ---------------------------------------------------------------- fakes --

    private sealed class FakeResolver : ITenantResolver
    {
        public Task<ResolvedTenant?> ResolveApiKeyAsync(string apiKey, CancellationToken ct)
            => Task.FromResult(apiKey == ApiKey
                ? new ResolvedTenant(TenantGuid, [], null, null)
                : (ResolvedTenant?)null);

        // Admin routes must never resolve via site key (DAT-04's construction) —
        // this fake mirrors that by never succeeding here.
        public Task<ResolvedTenant?> ResolveSiteKeyAsync(string siteKey, CancellationToken ct)
            => Task.FromResult<ResolvedTenant?>(null);
    }

    private sealed class CapturingLabelSink : ILabelSink
    {
        public readonly List<LabelEvent> Labels = [];

        public ValueTask WriteAsync(LabelEvent label, CancellationToken ct)
        {
            lock (Labels) Labels.Add(label);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Captures every AddAsync/Remove*/List call. AddAsync optionally
    /// emits a LabelEvent through the injected sink exactly the way DAT-07's real
    /// WhitelistRepository does (Source=='review_screen' && SessionId != null) —
    /// this is what proves the ENDPOINT contributes zero label writes on top of
    /// the repository's own one; DAT-07's own suite
    /// (WhitelistRepositoryTests.AddAsync_review_screen_emits_negative_label)
    /// separately proves the real repository's side of that contract against a
    /// real SQL Server + real ILabelSink.</summary>
    private sealed class FakeWhitelistRepository(ILabelSink? labelSink = null) : IWhitelistRepository
    {
        public readonly List<NewWhitelistEntry> AddCalls = [];
        public readonly List<long> RemoveByIdCalls = [];
        public readonly List<(string? SourceType, int Offset, int Limit)> ListCalls = [];
        public readonly Dictionary<long, WhitelistEntry> Entries = [];
        public IReadOnlyList<WhitelistEntry> ListResult = [];
        private long _nextId = 1;

        public async Task<long> AddAsync(NewWhitelistEntry entry, CancellationToken ct)
        {
            AddCalls.Add(entry);
            var id = _nextId++;
            Entries[id] = new WhitelistEntry(
                id, TenantGuid, entry.SourceType, entry.Value, entry.Reason,
                entry.Source, entry.CreatedBy, DateTime.UtcNow, entry.ExpiresUtc);

            if (entry.Source == "review_screen" && !string.IsNullOrEmpty(entry.SessionId) && labelSink is not null)
            {
                await labelSink.WriteAsync(new LabelEvent(
                    new TenantId(TenantGuid), entry.SessionId, LabelValues.Legit,
                    LabelSources.ReviewScreen, DateTime.UtcNow), ct);
            }

            return id;
        }

        public Task<bool> RemoveAsync(string sourceType, string value, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<WhitelistEntry?> GetByIdAsync(long id, CancellationToken ct)
            => Task.FromResult(Entries.TryGetValue(id, out var e) ? e : null);

        public Task<bool> RemoveByIdAsync(long id, CancellationToken ct)
        {
            RemoveByIdCalls.Add(id);
            return Task.FromResult(Entries.Remove(id));
        }

        public Task<IReadOnlyList<WhitelistEntry>> ListAsync(
            string? sourceType, int offset, int limit, CancellationToken ct)
        {
            ListCalls.Add((sourceType, offset, limit));
            return Task.FromResult(ListResult);
        }

        public Task<IReadOnlyDictionary<string, bool>> AreWhitelistedAsync(
            string sourceType, IReadOnlyCollection<string> values, CancellationToken ct)
            => throw new NotSupportedException();

        public Task RebuildCacheAsync(string sourceType, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class FakeSiteRepository : ISiteRepository
    {
        public IReadOnlyList<SiteRecord> Sites = [];

        public Task CreateAsync(SiteRecord site, CancellationToken ct) => throw new NotSupportedException();
        public Task<SiteRecord?> GetBySiteKeyAsync(string siteKey, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SiteRecord>> ListAsync(CancellationToken ct) => Task.FromResult(Sites);
        public Task<bool> UpdateAsync(string siteKey, string domain, string integrationMode, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string siteKey, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeVerdictSummaryRepository : IVerdictSummaryRepository
    {
        public (Guid CampaignId, DateOnly From, DateOnly To)? LastCall;
        public IReadOnlyList<VerdictDailySummaryRow> Rows = [];

        public (DateOnly From, DateOnly To, int Limit)? LastFlaggedSourcesCall;
        public IReadOnlyList<FlaggedSourceDailyRow> FlaggedSourceRows = [];

        public Task UpsertDailySummaryAsync(VerdictDailySummaryRow row, CancellationToken ct) => throw new NotSupportedException();
        public Task UpsertFlaggedSourceAsync(FlaggedSourceDailyRow row, CancellationToken ct) => throw new NotSupportedException();
        public Task IncrementDailySummaryAsync(VerdictDailySummaryRow delta, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<VerdictDailySummaryRow>> GetDailySummariesAsync(
            Guid campaignId, DateOnly from, DateOnly to, CancellationToken ct)
        {
            LastCall = (campaignId, from, to);
            return Task.FromResult(Rows);
        }

        public Task<IReadOnlyList<FlaggedSourceDailyRow>> GetTopFlaggedSourcesAsync(
            DateOnly from, DateOnly to, int limit, CancellationToken ct)
        {
            LastFlaggedSourcesCall = (from, to, limit);
            return Task.FromResult(FlaggedSourceRows);
        }
    }

    /// <summary>P2-01: fake for IPublisherSummaryRepository, backing GET
    /// /admin/reports/publishers and GET /admin/reports/sites.</summary>
    private sealed class FakePublisherSummaryRepository : IPublisherSummaryRepository
    {
        public (DateOnly From, DateOnly To, int Limit)? LastTopPlacementsCall;
        public IReadOnlyList<PlacementRangeTotalsRow> PlacementRows = [];

        public (DateOnly From, DateOnly To)? LastSiteDailyCall;
        public IReadOnlyList<SiteDailySummaryRow> SiteRows = [];

        public Task UpsertPlacementDailyAsync(PublisherDailySummaryRow row, CancellationToken ct) => throw new NotSupportedException();
        public Task UpsertSiteDailyAsync(SiteDailySummaryRow row, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<PlacementRangeTotalsRow>> GetTopPlacementsAsync(
            DateOnly from, DateOnly to, int limit, CancellationToken ct)
        {
            LastTopPlacementsCall = (from, to, limit);
            return Task.FromResult(PlacementRows);
        }

        public Task<IReadOnlyList<SiteDailySummaryRow>> GetSiteDailyAsync(
            DateOnly from, DateOnly to, CancellationToken ct)
        {
            LastSiteDailyCall = (from, to);
            return Task.FromResult(SiteRows);
        }
    }

    /// <summary>P2-03: fake for DAT-05's ICampaignRepository, backing GET /admin/campaigns.</summary>
    private sealed class FakeCampaignRepository : ICampaignRepository
    {
        public int ListCallCount;
        public IReadOnlyList<CampaignRecord> Rows = [];

        public Task CreateAsync(CampaignRecord campaign, CancellationToken ct) => throw new NotSupportedException();
        public Task<CampaignRecord?> GetAsync(Guid campaignId, CancellationToken ct) => throw new NotSupportedException();
        public Task<CampaignRedirect?> GetRedirectAsync(Guid campaignId, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<CampaignRecord>> ListAsync(CancellationToken ct)
        {
            ListCallCount++;
            return Task.FromResult(Rows);
        }

        public Task<bool> UpdateAsync(CampaignRecord campaign, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
    }

    /// <summary>NSubstitute Redis harness: seeds t:{tid}:site:{key}:lastbeacon
    /// string values and counts GetDatabase() calls so tests can prove the
    /// whitelist handlers never touch Redis at all.</summary>
    private sealed class RedisHarness
    {
        public readonly IConnectionMultiplexer Mux;
        public readonly IDatabase Db;
        public readonly Dictionary<string, string> Strings = [];

        public RedisHarness()
        {
            Mux = Substitute.For<IConnectionMultiplexer>();
            Db = Substitute.For<IDatabase>();
            Mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(Db);

            Db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
              .Returns(ci => Strings.TryGetValue(ci.ArgAt<RedisKey>(0).ToString(), out var v)
                  ? (RedisValue)v
                  : RedisValue.Null);
        }

        public void SeedLastBeacon(string tid, string siteKey, DateTimeOffset at)
            => Strings[$"t:{tid}:site:{siteKey}:lastbeacon"] = at.ToUnixTimeSeconds().ToString();
    }

    // ------------------------------------------------------------- harness --

    private sealed class AdminApp : IDisposable
    {
        public readonly WebApplicationFactory<Program> Factory;
        public readonly CapturingLabelSink Labels = new();
        public readonly FakeSiteRepository Sites = new();
        public readonly FakeVerdictSummaryRepository Summaries = new();
        public readonly FakeCampaignRepository Campaigns = new();
        public readonly FakePublisherSummaryRepository Publishers = new();
        public readonly RedisHarness Redis = new();
        public readonly FakeClock Clock = new();
        public readonly FakeWhitelistRepository Whitelist;

        public AdminApp(bool wireLabelSinkIntoRepository = true)
        {
            Whitelist = new FakeWhitelistRepository(wireLabelSinkIntoRepository ? Labels : null);

            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
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
                    services.RemoveAll<IWhitelistRepository>();
                    services.AddSingleton<IWhitelistRepository>(Whitelist);
                    services.RemoveAll<ISiteRepository>();
                    services.AddSingleton<ISiteRepository>(Sites);
                    services.RemoveAll<IVerdictSummaryRepository>();
                    services.AddSingleton<IVerdictSummaryRepository>(Summaries);
                    services.RemoveAll<ICampaignRepository>();
                    services.AddSingleton<ICampaignRepository>(Campaigns);
                    services.RemoveAll<IPublisherSummaryRepository>();
                    services.AddSingleton<IPublisherSummaryRepository>(Publishers);
                    services.RemoveAll<ILabelSink>();
                    services.AddSingleton<ILabelSink>(Labels);
                    services.RemoveAll<IConnectionMultiplexer>();
                    services.AddSingleton(Redis.Mux);
                    services.RemoveAll<IClock>();
                    services.AddSingleton<IClock>(Clock);
                });
            });
        }

        public HttpClient Client()
        {
            var client = Factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
            return client;
        }

        public HttpClient AnonymousClient() => Factory.CreateClient();

        public void Dispose() => Factory.Dispose();
    }

    private static HttpRequestMessage Post(string path, string body) => new(HttpMethod.Post, path)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static async Task<JsonElement> ReadJson(HttpResponseMessage resp)
        => JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    // ========================================================= auth / scope =

    [Fact]
    public async Task NoApiKey_SiteKeyOnly_Returns401ProblemJson_FromMiddleware()
    {
        using var app = new AdminApp();
        using var client = app.AnonymousClient();

        var resp = await client.GetAsync("/admin/whitelist?k=some-site-key");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
        Assert.Empty(app.Whitelist.ListCalls); // never reached the handler
    }

    [Fact]
    public async Task ValidApiKey_PassesTheFilter_AndReachesTheHandler()
    {
        using var app = new AdminApp();
        using var client = app.Client();

        var resp = await client.GetAsync("/admin/whitelist");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Single(app.Whitelist.ListCalls);
    }

    [Fact]
    public async Task UnknownApiKey_Returns401ProblemJson()
    {
        using var app = new AdminApp();
        using var client = app.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "not-a-real-key");

        var resp = await client.GetAsync("/admin/whitelist");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
    }

    // --------------------------------------- AdminScopeFilter, in isolation --

    private sealed class FakeTenantContext(bool isResolved, string? siteKey) : ITenantContext
    {
        public TenantId TenantId { get; } = new(TenantGuid);
        public string? SiteKey { get; } = siteKey;
        public bool IsResolved { get; } = isResolved;
    }

    private static async Task<IResult> InvokeFilterAsync(ITenantContext tenant)
    {
        var services = new ServiceCollection();
        services.AddSingleton(tenant);
        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        var efc = EndpointFilterInvocationContext.Create(httpContext);

        var filter = new AdminScopeFilter();
        var result = await filter.InvokeAsync(efc, _ => ValueTask.FromResult<object?>(Results.Ok("passed")));
        return Assert.IsAssignableFrom<IResult>(result);
    }

    [Fact]
    public async Task Filter_UnresolvedContext_Returns403Problem()
    {
        var result = await InvokeFilterAsync(new FakeTenantContext(isResolved: false, siteKey: null));

        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, statusResult.StatusCode);
    }

    [Fact]
    public async Task Filter_SiteKeyResolvedContext_Returns403Problem()
    {
        // Belt-and-braces: should never happen via DAT-04's middleware on
        // /admin/*, but the filter refuses it anyway if it ever did.
        var result = await InvokeFilterAsync(new FakeTenantContext(isResolved: true, siteKey: "site-1"));

        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, statusResult.StatusCode);
    }

    [Fact]
    public async Task Filter_ApiKeyResolvedContext_PassesThrough()
    {
        var result = await InvokeFilterAsync(new FakeTenantContext(isResolved: true, siteKey: null));

        // Not a 403 — the "passed" sentinel from `next` came back untouched.
        Assert.False(result is IStatusCodeHttpResult { StatusCode: StatusCodes.Status403Forbidden });
    }

    // ============================================================ whitelist =

    [Fact]
    public async Task PostWhitelist_Manual_Returns201_WithLocationHeader_AndCorrectEntry()
    {
        using var app = new AdminApp();
        using var client = app.Client();

        var body = """{"type":"ip","value":"203.0.113.7","reason":"confirmed real customer","source":"manual"}""";
        var resp = await client.SendAsync(Post("/admin/whitelist", body));
        var json = await ReadJson(resp);

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.NotNull(resp.Headers.Location);
        Assert.Contains("/admin/whitelist/", resp.Headers.Location!.ToString());
        Assert.True(json.GetProperty("id").GetInt64() > 0);

        var call = Assert.Single(app.Whitelist.AddCalls);
        Assert.Equal("ip", call.SourceType);
        Assert.Equal("203.0.113.7", call.Value);
        Assert.Equal("manual", call.Source);
        Assert.Null(call.SessionId);

        // Endpoint issued no Redis command and no ILabelSink call of its own —
        // the fake repository (Labels not wired in) proves it structurally, and
        // this asserts it at runtime too.
        Assert.Empty(app.Labels.Labels);
        Mux_DidNotTouchRedis(app.Redis.Mux);
    }

    [Fact]
    public async Task PostWhitelist_ReviewScreen_WithSessionId_Returns201_AndExactlyOneLabelEventTotal()
    {
        using var app = new AdminApp(wireLabelSinkIntoRepository: true);
        using var client = app.Client();
        var sessionId = "9f8e7d6c5b4a39281706f5e4d3c2b1a0";

        var body = $$"""{"type":"ip","value":"203.0.113.8","source":"review_screen","sessionId":"{{sessionId}}"}""";
        var resp = await client.SendAsync(Post("/admin/whitelist", body));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var call = Assert.Single(app.Whitelist.AddCalls);
        Assert.Equal("review_screen", call.Source);
        Assert.Equal(sessionId, call.SessionId);

        // Exactly ONE LabelEvent total: the repository (simulating DAT-07's
        // documented AddAsync side effect) emitted it; the endpoint emitted none.
        var label = Assert.Single(app.Labels.Labels);
        Assert.Equal(LabelValues.Legit, label.Label);
        Assert.Equal(LabelSources.ReviewScreen, label.LabelSource);
        Assert.Equal(sessionId, label.SessionId);
    }

    [Fact]
    public async Task PostWhitelist_ReviewScreen_WithoutSessionId_Returns400_AndWritesNothing()
    {
        using var app = new AdminApp();
        using var client = app.Client();

        var body = """{"type":"ip","value":"203.0.113.9","source":"review_screen"}""";
        var resp = await client.SendAsync(Post("/admin/whitelist", body));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
        Assert.Empty(app.Whitelist.AddCalls);
        Assert.Empty(app.Labels.Labels);
    }

    [Theory]
    [InlineData("cidr")]
    [InlineData("ua")]
    [InlineData("")]
    public async Task PostWhitelist_InvalidType_Returns400ValidationProblem(string type)
    {
        using var app = new AdminApp();
        using var client = app.Client();

        var body = $$"""{"type":"{{type}}","value":"1.2.3.4","source":"manual"}""";
        var resp = await client.SendAsync(Post("/admin/whitelist", body));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
        Assert.Empty(app.Whitelist.AddCalls);
    }

    [Fact]
    public async Task PostWhitelist_EmptyValue_Returns400ValidationProblem()
    {
        using var app = new AdminApp();
        using var client = app.Client();

        var body = """{"type":"device_id","value":"","source":"manual"}""";
        var resp = await client.SendAsync(Post("/admin/whitelist", body));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Empty(app.Whitelist.AddCalls);
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("999.999.999.999")]
    public async Task PostWhitelist_BadIpForIpType_Returns400ValidationProblem(string badValue)
    {
        using var app = new AdminApp();
        using var client = app.Client();

        var body = $$"""{"type":"ip","value":"{{badValue}}","source":"manual"}""";
        var resp = await client.SendAsync(Post("/admin/whitelist", body));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Empty(app.Whitelist.AddCalls);
    }

    [Fact]
    public async Task PostWhitelist_InvalidSource_Returns400ValidationProblem()
    {
        using var app = new AdminApp();
        using var client = app.Client();

        var body = """{"type":"ip","value":"1.2.3.4","source":"auto"}""";
        var resp = await client.SendAsync(Post("/admin/whitelist", body));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Empty(app.Whitelist.AddCalls);
    }

    [Fact]
    public async Task GetWhitelist_ReturnsEntries_WithCamelCaseFields()
    {
        using var app = new AdminApp();
        app.Whitelist.ListResult = new List<WhitelistEntry>
        {
            new(1, TenantGuid, "ip", "203.0.113.7", "real customer", "manual", "tester",
                new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), null),
        };
        using var client = app.Client();

        var resp = await client.GetAsync("/admin/whitelist?type=ip&offset=0&limit=10");
        var raw = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var call = Assert.Single(app.Whitelist.ListCalls);
        Assert.Equal("ip", call.SourceType);
        Assert.Equal(0, call.Offset);
        Assert.Equal(10, call.Limit);

        Assert.Contains("\"id\":1", raw);
        Assert.Contains("\"type\":\"ip\"", raw);
        Assert.Contains("\"value\":\"203.0.113.7\"", raw);
        Assert.Contains("\"createdBy\":\"tester\"", raw);
    }

    [Fact]
    public async Task GetWhitelist_LimitIsClampedTo200()
    {
        using var app = new AdminApp();
        using var client = app.Client();

        var resp = await client.GetAsync("/admin/whitelist?limit=5000");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var call = Assert.Single(app.Whitelist.ListCalls);
        Assert.Equal(200, call.Limit);
    }

    [Fact]
    public async Task DeleteWhitelist_ExistingId_Returns204()
    {
        using var app = new AdminApp();
        var id = await app.Whitelist.AddAsync(
            new NewWhitelistEntry("ip", "203.0.113.7", null, "manual", null, null), CancellationToken.None);
        app.Whitelist.AddCalls.Clear();
        using var client = app.Client();

        var resp = await client.DeleteAsync($"/admin/whitelist/{id}");

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Single(app.Whitelist.RemoveByIdCalls);
        Assert.Equal(id, app.Whitelist.RemoveByIdCalls[0]);
    }

    [Fact]
    public async Task DeleteWhitelist_UnknownId_Returns404Problem_AndNeverCallsRemove()
    {
        using var app = new AdminApp();
        using var client = app.Client();

        var resp = await client.DeleteAsync("/admin/whitelist/999999");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
        Assert.Empty(app.Whitelist.RemoveByIdCalls);
    }

    // =============================================================== summary =

    [Fact]
    public async Task GetSummary_ReturnsRows_WithAvgScoreMath()
    {
        using var app = new AdminApp();
        app.Summaries.Rows = new List<VerdictDailySummaryRow>
        {
            new(TenantGuid, CampaignGuid, new DateOnly(2026, 8, 1), 90, 20, 10, 3288, 120), // 3288/120 = 27.4
        };
        using var client = app.Client();

        var resp = await client.GetAsync(
            $"/admin/reports/summary?campaignId={CampaignGuid:D}&from=2026-08-01&to=2026-08-12");
        var json = await ReadJson(resp);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("2026-08-01", json.GetProperty("from").GetString());
        Assert.Equal("2026-08-12", json.GetProperty("to").GetString());
        Assert.Equal(CampaignGuid, json.GetProperty("campaignId").GetGuid());

        var row = json.GetProperty("rows")[0];
        Assert.Equal("2026-08-01", row.GetProperty("date").GetString());
        Assert.Equal(120, row.GetProperty("events").GetInt32());
        Assert.Equal(90, row.GetProperty("allowed").GetInt32());
        Assert.Equal(20, row.GetProperty("challenged").GetInt32());
        Assert.Equal(10, row.GetProperty("blocked").GetInt32());
        Assert.Equal(27.4, row.GetProperty("avgScore").GetDouble());

        Assert.Equal((CampaignGuid, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 12)), app.Summaries.LastCall);
    }

    [Fact]
    public async Task GetSummary_ZeroEvents_AvgScoreIsNull()
    {
        using var app = new AdminApp();
        app.Summaries.Rows = new List<VerdictDailySummaryRow>
        {
            new(TenantGuid, CampaignGuid, new DateOnly(2026, 8, 1), 0, 0, 0, 0, 0),
        };
        using var client = app.Client();

        var resp = await client.GetAsync(
            $"/admin/reports/summary?campaignId={CampaignGuid:D}&from=2026-08-01&to=2026-08-01");
        var json = await ReadJson(resp);

        var row = json.GetProperty("rows")[0];
        Assert.Equal(JsonValueKind.Null, row.GetProperty("avgScore").ValueKind);
    }

    [Fact]
    public async Task GetSummary_NoCampaignSentinel_IsAllowed()
    {
        using var app = new AdminApp();
        using var client = app.Client();

        var resp = await client.GetAsync(
            $"/admin/reports/summary?campaignId={Guid.Empty:D}&from=2026-08-01&to=2026-08-01");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task GetSummary_MissingCampaignId_Returns400()
    {
        using var app = new AdminApp();
        using var client = app.Client();

        var resp = await client.GetAsync("/admin/reports/summary?from=2026-08-01&to=2026-08-12");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GetSummary_InvalidCampaignId_Returns400()
    {
        using var app = new AdminApp();
        using var client = app.Client();

        var resp = await client.GetAsync("/admin/reports/summary?campaignId=not-a-guid&from=2026-08-01&to=2026-08-12");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetSummary_FromAfterTo_Returns400()
    {
        using var app = new AdminApp();
        using var client = app.Client();

        var resp = await client.GetAsync(
            $"/admin/reports/summary?campaignId={CampaignGuid:D}&from=2026-08-12&to=2026-08-01");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetSummary_RangeExceeds366Days_Returns400()
    {
        using var app = new AdminApp();
        using var client = app.Client();

        var resp = await client.GetAsync(
            $"/admin/reports/summary?campaignId={CampaignGuid:D}&from=2025-01-01&to=2026-06-01");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetSummary_366DaySpan_IsAllowed()
    {
        using var app = new AdminApp();
        using var client = app.Client();

        // 2027-07-01 -> 2028-07-01 crosses the 2028-02-29 leap day, so the
        // DayNumber difference is exactly 366 — the inclusive boundary.
        var resp = await client.GetAsync(
            $"/admin/reports/summary?campaignId={CampaignGuid:D}&from=2027-07-01&to=2028-07-01");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ==================================================== integration status =

    [Fact]
    public async Task IntegrationStatus_RecentBeacon_IsJs_OldOrAbsentBeacon_IsHttpOnly()
    {
        using var app = new AdminApp();
        var tid = TenantGuid.ToString("D");
        app.Sites.Sites = new List<SiteRecord>
        {
            new(TenantGuid, "site_abc123", "landing-a.example", "js", DateTime.UtcNow),
            new(TenantGuid, "site_old456", "landing-b.example", "pixel", DateTime.UtcNow),
            new(TenantGuid, "site_none789", "landing-c.example", "pixel", DateTime.UtcNow),
        };
        app.Redis.SeedLastBeacon(tid, "site_abc123", app.Clock.UtcNow.AddHours(-1));   // recent -> js
        app.Redis.SeedLastBeacon(tid, "site_old456", app.Clock.UtcNow.AddDays(-3));    // stale -> http-only
        // site_none789: no key at all -> http-only, lastBeaconAt null
        using var client = app.Client();

        var resp = await client.GetAsync("/admin/sites/integration-status");
        var json = await ReadJson(resp);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var sites = json.GetProperty("sites");
        Assert.Equal(3, sites.GetArrayLength());

        var abc = FindBySiteKey(sites, "site_abc123");
        Assert.Equal("landing-a.example", abc.GetProperty("domain").GetString());
        Assert.Equal("js", abc.GetProperty("configuredMode").GetString());
        Assert.Equal("js", abc.GetProperty("effectiveLevel").GetString());
        Assert.NotEqual(JsonValueKind.Null, abc.GetProperty("lastBeaconAt").ValueKind);

        var old = FindBySiteKey(sites, "site_old456");
        Assert.Equal("pixel", old.GetProperty("configuredMode").GetString());
        Assert.Equal("http-only", old.GetProperty("effectiveLevel").GetString());
        Assert.NotEqual(JsonValueKind.Null, old.GetProperty("lastBeaconAt").ValueKind);

        var none = FindBySiteKey(sites, "site_none789");
        Assert.Equal("http-only", none.GetProperty("effectiveLevel").GetString());
        Assert.Equal(JsonValueKind.Null, none.GetProperty("lastBeaconAt").ValueKind);
    }

    private static JsonElement FindBySiteKey(JsonElement sites, string siteKey)
    {
        foreach (var site in sites.EnumerateArray())
        {
            if (site.GetProperty("siteKey").GetString() == siteKey)
                return site;
        }
        throw new InvalidOperationException($"site {siteKey} not found in response");
    }

    // =============================================================== campaigns =
    // P2-03: GET /admin/campaigns.

    [Fact]
    public async Task GetCampaigns_ReturnsShape_WithCamelCaseFields_AndNoLandingUrlOrGeoTargets()
    {
        using var app = new AdminApp();
        app.Campaigns.Rows = new List<CampaignRecord>
        {
            new(TenantGuid, CampaignGuid, "google", "ext-123",
                "https://example.com/landing", null, 0, DateTime.UtcNow),
        };
        using var client = app.Client();

        var resp = await client.GetAsync("/admin/campaigns");
        var raw = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, app.Campaigns.ListCallCount);
        Assert.Contains("\"campaignId\"", raw);
        Assert.Contains("\"platform\":\"google\"", raw);
        Assert.Contains("\"externalCampaignId\":\"ext-123\"", raw);
        Assert.Contains("\"status\":0", raw);
        Assert.DoesNotContain("landingUrl", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("geoTargets", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetCampaigns_NoApiKey_Returns401ProblemJson_FromMiddleware()
    {
        using var app = new AdminApp();
        using var client = app.AnonymousClient();

        var resp = await client.GetAsync("/admin/campaigns");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0, app.Campaigns.ListCallCount); // never reached the handler
    }

    // ===================================================== flagged sources =
    // P2-03: GET /admin/reports/flagged-sources.

    [Fact]
    public async Task GetFlaggedSources_ReturnsScoreSum_AndNoAverageField()
    {
        using var app = new AdminApp();
        app.Summaries.FlaggedSourceRows = new List<FlaggedSourceDailyRow>
        {
            new(TenantGuid, new DateOnly(2026, 8, 1), "ip", "203.0.113.9", 5, 2, 410),
        };
        using var client = app.Client();

        var resp = await client.GetAsync("/admin/reports/flagged-sources?from=2026-08-01&to=2026-08-07");
        var raw = await resp.Content.ReadAsStringAsync();
        var json = JsonSerializer.Deserialize<JsonElement>(raw);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("2026-08-01", json.GetProperty("from").GetString());
        Assert.Equal("2026-08-07", json.GetProperty("to").GetString());

        var row = json.GetProperty("sources")[0];
        Assert.Equal("ip", row.GetProperty("sourceType").GetString());
        Assert.Equal("203.0.113.9", row.GetProperty("value").GetString());
        Assert.Equal(5, row.GetProperty("flaggedCount").GetInt32());
        Assert.Equal(2, row.GetProperty("blockedCount").GetInt32());
        Assert.Equal(410, row.GetProperty("scoreSum").GetInt64());
        Assert.False(row.TryGetProperty("avgScore", out _), "no average field must be computed for flagged sources");
        Assert.DoesNotContain("avgScore", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0, 1)]      // below the DAT-06 minimum -> clamped up to 1
    [InlineData(5000, 1000)] // above the DAT-06 maximum -> clamped down to 1000
    public async Task GetFlaggedSources_LimitIsClamped(int requested, int expected)
    {
        using var app = new AdminApp();
        using var client = app.Client();

        var resp = await client.GetAsync(
            $"/admin/reports/flagged-sources?from=2026-08-01&to=2026-08-07&limit={requested}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.NotNull(app.Summaries.LastFlaggedSourcesCall);
        Assert.Equal(expected, app.Summaries.LastFlaggedSourcesCall!.Value.Limit);
    }

    [Fact]
    public async Task GetFlaggedSources_MissingFrom_Returns400ValidationProblem()
    {
        using var app = new AdminApp();
        using var client = app.Client();

        var resp = await client.GetAsync("/admin/reports/flagged-sources?to=2026-08-07");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GetFlaggedSources_MissingTo_Returns400ValidationProblem()
    {
        using var app = new AdminApp();
        using var client = app.Client();

        var resp = await client.GetAsync("/admin/reports/flagged-sources?from=2026-08-01");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetFlaggedSources_FromAfterTo_Returns400()
    {
        using var app = new AdminApp();
        using var client = app.Client();

        var resp = await client.GetAsync("/admin/reports/flagged-sources?from=2026-08-07&to=2026-08-01");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetFlaggedSources_RangeExceeds366Days_Returns400()
    {
        using var app = new AdminApp();
        using var client = app.Client();

        var resp = await client.GetAsync(
            "/admin/reports/flagged-sources?from=2025-01-01&to=2026-06-01");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetFlaggedSources_NoApiKey_Returns401ProblemJson()
    {
        using var app = new AdminApp();
        using var client = app.AnonymousClient();

        var resp = await client.GetAsync("/admin/reports/flagged-sources?from=2026-08-01&to=2026-08-07");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ===================================================== P2-01: publishers =
    // GET /admin/reports/publishers. AdminScopeFilter is route-agnostic (it only
    // inspects ITenantContext, never the endpoint) — its 403 backstop is proven
    // once, in isolation, by Filter_SiteKeyResolvedContext_Returns403Problem
    // above; that single proof covers every route in the group, these included.

    [Fact]
    public async Task GetPublisherReport_ComputesFlaggedRatioAndAvgScore()
    {
        using var app = new AdminApp();
        app.Publishers.PlacementRows = new List<PlacementRangeTotalsRow>
        {
            new("ad-net.example", 100, 45, 15, 25, 4200, 8,
                new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 10)),
        };
        using var client = app.Client();

        var resp = await client.GetAsync("/admin/reports/publishers?from=2026-08-01&to=2026-08-10");
        var json = await ReadJson(resp);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var row = json.GetProperty("rows")[0];
        Assert.Equal("ad-net.example", row.GetProperty("placement").GetString());
        Assert.Equal(100, row.GetProperty("events").GetInt32());
        Assert.Equal(40, row.GetProperty("flagged").GetInt32());       // 15 + 25
        Assert.Equal(0.4, row.GetProperty("flaggedRatio").GetDouble());
        Assert.Equal(42.0, row.GetProperty("avgScore").GetDouble());   // 4200 / 100
        Assert.Equal(8, row.GetProperty("noJsBeaconCount").GetInt32());
        Assert.False(row.GetProperty("lowVolume").GetBoolean());      // events >= 30
        Assert.Equal("2026-08-01", row.GetProperty("firstDay").GetString());
        Assert.Equal("2026-08-10", row.GetProperty("lastDay").GetString());

        Assert.Equal((new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 10), 50), app.Publishers.LastTopPlacementsCall);
    }

    [Fact]
    public async Task GetPublisherReport_LowVolume_IsFlaggedButRatioStillReturned()
    {
        using var app = new AdminApp();
        app.Publishers.PlacementRows = new List<PlacementRangeTotalsRow>
        {
            new("small-pub.example", 10, 5, 3, 2, 300, 0,
                new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 1)),
        };
        using var client = app.Client();

        var resp = await client.GetAsync("/admin/reports/publishers?from=2026-08-01&to=2026-08-01");
        var json = await ReadJson(resp);

        var row = json.GetProperty("rows")[0];
        Assert.True(row.GetProperty("lowVolume").GetBoolean()); // events (10) < 30
        Assert.Equal(0.5, row.GetProperty("flaggedRatio").GetDouble()); // still computed, not hidden
    }

    [Fact]
    public async Task GetPublisherReport_MissingFrom_Returns400ValidationProblem()
    {
        using var app = new AdminApp();
        using var client = app.Client();

        var resp = await client.GetAsync("/admin/reports/publishers?to=2026-08-10");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GetPublisherReport_MalformedTo_Returns400ValidationProblem()
    {
        using var app = new AdminApp();
        using var client = app.Client();

        var resp = await client.GetAsync("/admin/reports/publishers?from=2026-08-01&to=not-a-date");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetPublisherReport_FromAfterTo_Returns400()
    {
        using var app = new AdminApp();
        using var client = app.Client();

        var resp = await client.GetAsync("/admin/reports/publishers?from=2026-08-10&to=2026-08-01");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetPublisherReport_RangeExceeds366Days_Returns400()
    {
        using var app = new AdminApp();
        using var client = app.Client();

        var resp = await client.GetAsync("/admin/reports/publishers?from=2025-01-01&to=2026-06-01");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(9999, 200)]
    public async Task GetPublisherReport_LimitIsClamped(int requested, int expected)
    {
        using var app = new AdminApp();
        using var client = app.Client();

        var resp = await client.GetAsync($"/admin/reports/publishers?from=2026-08-01&to=2026-08-01&limit={requested}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.NotNull(app.Publishers.LastTopPlacementsCall);
        Assert.Equal(expected, app.Publishers.LastTopPlacementsCall!.Value.Limit);
    }

    [Fact]
    public async Task GetPublisherReport_NoApiKey_Returns401ProblemJson()
    {
        using var app = new AdminApp();
        using var client = app.AnonymousClient();

        var resp = await client.GetAsync("/admin/reports/publishers?from=2026-08-01&to=2026-08-01");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // =========================================================== P2-01: sites =
    // GET /admin/reports/sites.

    [Fact]
    public async Task GetSiteReport_ReturnsRows_WithAvgScoreMath()
    {
        using var app = new AdminApp();
        app.Publishers.SiteRows = new List<SiteDailySummaryRow>
        {
            new(TenantGuid, new DateOnly(2026, 8, 1), "site-1", 50, 10, 6, 2, 2, 300, 1),
        };
        using var client = app.Client();

        var resp = await client.GetAsync("/admin/reports/sites?from=2026-08-01&to=2026-08-01");
        var json = await ReadJson(resp);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var row = json.GetProperty("rows")[0];
        Assert.Equal("2026-08-01", row.GetProperty("date").GetString());
        Assert.Equal("site-1", row.GetProperty("siteKey").GetString());
        Assert.Equal(50, row.GetProperty("totalEvents").GetInt32());
        Assert.Equal(10, row.GetProperty("events").GetInt32());
        Assert.Equal(6, row.GetProperty("allowed").GetInt32());
        Assert.Equal(2, row.GetProperty("challenged").GetInt32());
        Assert.Equal(2, row.GetProperty("blocked").GetInt32());
        Assert.Equal(30.0, row.GetProperty("avgScore").GetDouble()); // 300 / 10
        Assert.Equal(1, row.GetProperty("noJsBeaconCount").GetInt32());

        Assert.Equal((new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 1)), app.Publishers.LastSiteDailyCall);
    }

    [Fact]
    public async Task GetSiteReport_ZeroScoredEvents_AvgScoreIsNull_ButTotalEventsStillReflectsTraffic()
    {
        using var app = new AdminApp();
        app.Publishers.SiteRows = new List<SiteDailySummaryRow>
        {
            new(TenantGuid, new DateOnly(2026, 8, 1), "site-2", 20, 0, 0, 0, 0, 0, 0),
        };
        using var client = app.Client();

        var resp = await client.GetAsync("/admin/reports/sites?from=2026-08-01&to=2026-08-01");
        var json = await ReadJson(resp);

        var row = json.GetProperty("rows")[0];
        Assert.Equal(20, row.GetProperty("totalEvents").GetInt32()); // pixel/tracker-only traffic
        Assert.Equal(0, row.GetProperty("events").GetInt32());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("avgScore").ValueKind); // never 0
    }

    [Fact]
    public async Task GetSiteReport_MissingTo_Returns400ValidationProblem()
    {
        using var app = new AdminApp();
        using var client = app.Client();

        var resp = await client.GetAsync("/admin/reports/sites?from=2026-08-01");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GetSiteReport_FromAfterTo_Returns400()
    {
        using var app = new AdminApp();
        using var client = app.Client();

        var resp = await client.GetAsync("/admin/reports/sites?from=2026-08-10&to=2026-08-01");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetSiteReport_RangeExceeds366Days_Returns400()
    {
        using var app = new AdminApp();
        using var client = app.Client();

        var resp = await client.GetAsync("/admin/reports/sites?from=2025-01-01&to=2026-06-01");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetSiteReport_NoApiKey_Returns401ProblemJson()
    {
        using var app = new AdminApp();
        using var client = app.AnonymousClient();

        var resp = await client.GetAsync("/admin/reports/sites?from=2026-08-01&to=2026-08-01");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ------------------------------------------------------------- helpers --

    private static void Mux_DidNotTouchRedis(IConnectionMultiplexer mux)
        => mux.DidNotReceive().GetDatabase(Arg.Any<int>(), Arg.Any<object?>());
}
