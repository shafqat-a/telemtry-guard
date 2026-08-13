using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Tests.Integration.Sql;

namespace TelemetryGuard.Tests.Integration.Api;

/// <summary>
/// API-07 /admin/* against the REAL production object graph via
/// WebApplicationFactory&lt;Program&gt;, DAT-08's SqlServerFixture (real SQL Server +
/// real Redis, migrated), and the fixture's seeded "admin"-scope API key: the real
/// SqlTenantResolver resolves X-Api-Key end to end, the real DAT-07
/// WhitelistRepository does the SQL write + Redis cache rebuild, and the real
/// DAT-05/06 ISiteRepository/IVerdictSummaryRepository serve the reports. Only
/// ILabelSink is swapped for a capturing fake — ANA-05's real ClickHouseLabelSink
/// needs a live ClickHouse this suite doesn't stand up — which is exactly what lets
/// this class prove DAT-07's documented "review_screen add -> exactly one negative
/// label" contract holds end to end THROUGH THE HTTP ENDPOINT, not only when the
/// repository is driven directly (see Sql/WhitelistRepositoryTests.cs for that
/// proof against the repository in isolation).
/// </summary>
[Collection("sqlserver")]
[Trait("Category", "Integration")]
public sealed class AdminEndpointTests(SqlServerFixture fx)
{
    private sealed class CapturingLabelSink : ILabelSink
    {
        public readonly List<LabelEvent> Labels = [];

        public ValueTask WriteAsync(LabelEvent label, CancellationToken ct)
        {
            lock (Labels) Labels.Add(label);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AdminApp : IDisposable
    {
        public readonly WebApplicationFactory<Program> Factory;
        public readonly CapturingLabelSink Labels = new();

        public AdminApp(SqlServerFixture fixture)
        {
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                var overrides = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Main"] = fixture.ConnectionString,
                    ["ConnectionStrings:Redis"] = fixture.RedisConnectionString,
                    // Unreachable on purpose: nothing in this suite depends on ClickHouse
                    // actually being reachable (D23 — admin reads never touch it), and the
                    // event/label sinks' hosted services already tolerate this (API-04/05's
                    // own WebApplicationFactory tests use the same bogus host:port).
                    ["Analytics:ClickHouse:ConnectionString"] = "Host=localhost;Port=1;Database=telemetry_guard",
                };
                foreach (var (k, v) in overrides)
                    b.UseSetting(k, v);
                b.ConfigureTestServices(services =>
                {
                    services.RemoveAll<ILabelSink>();
                    services.AddSingleton<ILabelSink>(Labels);
                });
            });
        }

        public HttpClient Client(string apiKey)
        {
            var client = Factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
            return client;
        }

        public void Dispose() => Factory.Dispose();
    }

    private static HttpRequestMessage Post(string path, string body) => new(HttpMethod.Post, path)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static async Task<JsonElement> ReadJson(HttpResponseMessage resp)
        => JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    private static string FreshValue(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..30];

    /// <summary>A syntactically valid IP in the given /24 (the endpoint's own
    /// IPAddress.TryParse validation rejects the GUID-suffixed values
    /// <see cref="FreshValue"/> produces) — last octet randomized for
    /// uniqueness across the tests in this class.</summary>
    private static string FreshIp(string subnet) => $"{subnet}.{Random.Shared.Next(1, 254)}";

    [Fact]
    public async Task NoApiKey_Returns401ProblemJson_BeforeReachingTheHandler()
    {
        using var app = new AdminApp(fx);
        using var client = app.Factory.CreateClient();

        var resp = await client.GetAsync("/admin/whitelist");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task PostWhitelist_ReviewScreen_RealRepository_EmitsExactlyOneLabelEvent_AndRebuildsRedisCache()
    {
        using var app = new AdminApp(fx);
        using var client = app.Client(SqlServerFixture.ApiKeyA);

        var value = FreshIp("203.0.113");
        var sessionId = $"sess{Guid.NewGuid():N}"[..32];

        var resp = await client.SendAsync(Post(
            "/admin/whitelist",
            $$"""{"type":"ip","value":"{{value}}","source":"review_screen","sessionId":"{{sessionId}}"}"""));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        // Exactly ONE LabelEvent total: DAT-07's real WhitelistRepository emitted
        // it as AddAsync's own side effect — the admin endpoint above it emitted
        // none (it never touches ILabelSink for whitelist operations).
        var label = Assert.Single(app.Labels.Labels);
        Assert.Equal(LabelValues.Legit, label.Label);
        Assert.Equal(LabelSources.ReviewScreen, label.LabelSource);
        Assert.Equal(sessionId, label.SessionId);
        Assert.Equal(new TenantId(SqlServerFixture.TenantA), label.TenantId);

        // The repository's own Redis cache rebuild ran too — proof the endpoint
        // issued no Redis command of its own to accomplish this.
        var db = fx.Redis.GetDatabase();
        var key = SqlServerFixture.WhitelistKey(SqlServerFixture.TenantA, "ip");
        Assert.True(await db.SetContainsAsync(key, value));
    }

    [Fact]
    public async Task PostWhitelist_Manual_RealRepository_EmitsNoLabel()
    {
        using var app = new AdminApp(fx);
        using var client = app.Client(SqlServerFixture.ApiKeyA);
        var value = FreshIp("198.51.100");

        var resp = await client.SendAsync(Post(
            "/admin/whitelist", $$"""{"type":"ip","value":"{{value}}","source":"manual"}"""));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Empty(app.Labels.Labels);
    }

    [Fact]
    public async Task DeleteWhitelist_RealRepository_ExistingThenUnknownId()
    {
        using var app = new AdminApp(fx);
        using var client = app.Client(SqlServerFixture.ApiKeyA);
        var value = FreshValue("dev");

        var addResp = await client.SendAsync(Post(
            "/admin/whitelist", $$"""{"type":"device_id","value":"{{value}}","source":"manual"}"""));
        var added = await ReadJson(addResp);
        var id = added.GetProperty("id").GetInt64();

        var delResp = await client.DeleteAsync($"/admin/whitelist/{id}");
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);

        // Second delete of the same (now-gone) id: 404, not 204 again.
        var delAgain = await client.DeleteAsync($"/admin/whitelist/{id}");
        Assert.Equal(HttpStatusCode.NotFound, delAgain.StatusCode);
        Assert.Equal("application/problem+json", delAgain.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GetSummary_RealRepository_ReturnsSeededRow_WithAvgScoreMath()
    {
        using var app = new AdminApp(fx);
        using var client = app.Client(SqlServerFixture.ApiKeyA);

        // A fresh campaign (not the shared CampaignA1) so this test's summary row
        // never collides with another test's increments on the same (tenant,
        // campaign, day) key.
        var campaignId = Guid.NewGuid();
        await using (var conn = await fx.OpenAsync(SqlServerFixture.TenantA))
        {
            await conn.ExecuteAsync(
                "INSERT INTO dbo.Campaigns (TenantId, CampaignId, Platform, LandingUrl) " +
                "VALUES (@TenantId, @CampaignId, 'google', N'https://a.example.com/lp')",
                new { TenantId = SqlServerFixture.TenantA, CampaignId = campaignId });

            await conn.ExecuteAsync(
                """
                INSERT INTO dbo.VerdictDailySummaries
                    (TenantId, CampaignId, [Date], Allowed, Challenged, Blocked, ScoreSum, Events)
                VALUES (@TenantId, @CampaignId, CAST(SYSUTCDATETIME() AS date), @Allowed, @Challenged, @Blocked, @ScoreSum, @Events);
                """,
                new
                {
                    TenantId = SqlServerFixture.TenantA, CampaignId = campaignId,
                    Allowed = 5, Challenged = 2, Blocked = 1, ScoreSum = 240L, Events = 8,
                });
        }

        var today = DateTime.UtcNow.Date.ToString("yyyy-MM-dd");
        var resp = await client.GetAsync(
            $"/admin/reports/summary?campaignId={campaignId:D}&from={today}&to={today}");
        var json = await ReadJson(resp);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var row = Assert.Single(json.GetProperty("rows").EnumerateArray());
        Assert.Equal(8, row.GetProperty("events").GetInt32());
        Assert.Equal(5, row.GetProperty("allowed").GetInt32());
        Assert.Equal(2, row.GetProperty("challenged").GetInt32());
        Assert.Equal(1, row.GetProperty("blocked").GetInt32());
        Assert.Equal(30.0, row.GetProperty("avgScore").GetDouble()); // 240/8
    }

    [Fact]
    public async Task GetSummary_UnknownCampaign_ReturnsEmptyRows_NotAnError()
    {
        using var app = new AdminApp(fx);
        using var client = app.Client(SqlServerFixture.ApiKeyA);

        var resp = await client.GetAsync(
            $"/admin/reports/summary?campaignId={Guid.NewGuid():D}&from=2026-01-01&to=2026-01-02");
        var json = await ReadJson(resp);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Empty(json.GetProperty("rows").EnumerateArray());
    }

    [Fact]
    public async Task IntegrationStatus_RealRepository_ReflectsSeededSiteAndLastBeacon()
    {
        using var app = new AdminApp(fx);
        using var client = app.Client(SqlServerFixture.ApiKeyA);

        var tid = SqlServerFixture.TenantA.ToString("D");
        var db = fx.Redis.GetDatabase();
        await db.StringSetAsync(
            $"t:{tid}:site:{SqlServerFixture.SiteKeyA}:lastbeacon",
            DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeSeconds());

        var resp = await client.GetAsync("/admin/sites/integration-status");
        var json = await ReadJson(resp);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var site = json.GetProperty("sites").EnumerateArray()
            .First(s => s.GetProperty("siteKey").GetString() == SqlServerFixture.SiteKeyA);
        Assert.Equal("js", site.GetProperty("effectiveLevel").GetString());
        Assert.NotEqual(JsonValueKind.Null, site.GetProperty("lastBeaconAt").ValueKind);
    }
}
