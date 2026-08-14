using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TelemetryGuard.Core.Time;
using TelemetryGuard.Data.Repositories;
using TelemetryGuard.Data.Tenancy;

namespace TelemetryGuard.Tests.Unit.Api;

/// <summary>
/// INT-02 /admin/enforcement/* tests over the real Program composition via
/// WebApplicationFactory: auth matrix (mirrors AdminEndpointTests' pattern —
/// AdminScopeFilter is a resolved-vs-site-key backstop, not yet 'admin' scope
/// enforcement; see AdminScopeFilter's own TODO(DAT-04)), status parsing, limit
/// clamping, batch-size/note validation, response shapes (incl. platform),
/// idempotent-replay counts, and actor-hash pass-through.
///
/// IEnforcementQueueRepository/ITenantResolver are hand-rolled fakes, same
/// pattern as AdminEndpointTests/API-05/06's tests.
/// </summary>
public sealed class EnforcementAdminEndpointTests
{
    private static readonly Guid TenantGuid = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");
    private const string ApiKey = "enforcement-admin-test-api-key";

    // ---------------------------------------------------------------- fakes --

    private sealed class FakeResolver : ITenantResolver
    {
        public Task<ResolvedTenant?> ResolveApiKeyAsync(string apiKey, CancellationToken ct)
            => Task.FromResult(apiKey == ApiKey
                ? new ResolvedTenant(TenantGuid, ["admin"], null, null)
                : (ResolvedTenant?)null);

        public Task<ResolvedTenant?> ResolveSiteKeyAsync(string siteKey, CancellationToken ct)
            => Task.FromResult<ResolvedTenant?>(null);
    }

    private sealed record ListCall(string Status, int Limit);
    private sealed record ApproveCall(IReadOnlyList<long> Ids, byte[]? ActorKeyHash);
    private sealed record RejectCall(IReadOnlyList<long> Ids, string? Note, byte[]? ActorKeyHash);

    private sealed class FakeEnforcementQueueRepository : IEnforcementQueueRepository
    {
        public readonly List<ListCall> ListCalls = [];
        public IReadOnlyList<ExclusionQueueEntry> ListResult = [];

        public readonly List<ApproveCall> ApproveCalls = [];
        public int ApproveReturns;

        public readonly List<RejectCall> RejectCalls = [];
        public int RejectReturns;

        public Task<IReadOnlyList<ExclusionQueueEntry>> ListAsync(string status, int limit, CancellationToken ct)
        {
            ListCalls.Add(new ListCall(status, limit));
            return Task.FromResult(ListResult);
        }

        public Task<int> ApproveAsync(IReadOnlyList<long> ids, byte[]? actorKeyHash, CancellationToken ct)
        {
            ApproveCalls.Add(new ApproveCall(ids, actorKeyHash));
            return Task.FromResult(ApproveReturns);
        }

        public Task<int> RejectAsync(IReadOnlyList<long> ids, string? note, byte[]? actorKeyHash, CancellationToken ct)
        {
            RejectCalls.Add(new RejectCall(ids, note, actorKeyHash));
            return Task.FromResult(RejectReturns);
        }
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
    }

    // ------------------------------------------------------------- harness --

    private sealed class EnforcementApp : IDisposable
    {
        public readonly WebApplicationFactory<Program> Factory;
        public readonly FakeEnforcementQueueRepository Repo = new();

        public EnforcementApp()
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
                };
                foreach (var (k, v) in overrides)
                    b.UseSetting(k, v);
                b.ConfigureTestServices(services =>
                {
                    services.RemoveAll<ITenantResolver>();
                    services.AddSingleton<ITenantResolver>(new FakeResolver());
                    services.RemoveAll<IEnforcementQueueRepository>();
                    services.AddSingleton<IEnforcementQueueRepository>(Repo);
                    services.RemoveAll<IClock>();
                    services.AddSingleton<IClock>(new FakeClock());
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

    private static ExclusionQueueEntry SampleEntry(long id, string status) => new(
        id, "google", "ip", "203.0.113.7", null, "score=90 rules=honeypot_touched", status,
        new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc), null);

    // ========================================================= auth / scope =

    [Fact]
    public async Task NoApiKey_Returns401ProblemJson_FromMiddleware()
    {
        using var app = new EnforcementApp();
        using var client = app.AnonymousClient();

        var resp = await client.GetAsync("/admin/enforcement");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
        Assert.Empty(app.Repo.ListCalls);
    }

    [Fact]
    public async Task UnknownApiKey_Returns401ProblemJson()
    {
        using var app = new EnforcementApp();
        using var client = app.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "not-a-real-key");

        var resp = await client.GetAsync("/admin/enforcement");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Empty(app.Repo.ListCalls);
    }

    [Fact]
    public async Task SiteKeyOnly_Returns401_NeverReachesTheHandler()
    {
        // Admin routes never resolve via site key (DAT-04's construction) — the
        // middleware itself 401s before AdminScopeFilter's belt-and-braces 403
        // for a site-key-resolved context is even relevant here.
        using var app = new EnforcementApp();
        using var client = app.AnonymousClient();

        var resp = await client.GetAsync("/admin/enforcement?k=some-site-key");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Empty(app.Repo.ListCalls);
    }

    [Fact]
    public async Task ValidApiKey_PassesTheFilter_AndReachesTheHandler()
    {
        using var app = new EnforcementApp();
        using var client = app.Client();

        var resp = await client.GetAsync("/admin/enforcement");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Single(app.Repo.ListCalls);
    }

    // ================================================================ list =

    [Fact]
    public async Task List_NoStatus_DefaultsToPending()
    {
        using var app = new EnforcementApp();
        using var client = app.Client();

        var resp = await client.GetAsync("/admin/enforcement");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var call = Assert.Single(app.Repo.ListCalls);
        Assert.Equal(ExclusionStatuses.Pending, call.Status);
    }

    [Theory]
    [InlineData("PENDING", "pending")]
    [InlineData("Approved", "approved")]
    [InlineData("unsupported", "unsupported")]
    [InlineData("FAILED", "failed")]
    public async Task List_StatusIsCaseInsensitive_AndNormalized(string queryValue, string expected)
    {
        using var app = new EnforcementApp();
        using var client = app.Client();

        var resp = await client.GetAsync($"/admin/enforcement?status={queryValue}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var call = Assert.Single(app.Repo.ListCalls);
        Assert.Equal(expected, call.Status);
    }

    [Fact]
    public async Task List_InvalidStatus_Returns400ProblemJson()
    {
        using var app = new EnforcementApp();
        using var client = app.Client();

        var resp = await client.GetAsync("/admin/enforcement?status=bogus");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
        Assert.Empty(app.Repo.ListCalls);
    }

    [Fact]
    public async Task List_NoLimit_DefaultsTo100()
    {
        using var app = new EnforcementApp();
        using var client = app.Client();

        var resp = await client.GetAsync("/admin/enforcement");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(100, app.Repo.ListCalls[0].Limit);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(5000, 500)]
    public async Task List_LimitIsClampedTo1To500(int requested, int expectedClamped)
    {
        using var app = new EnforcementApp();
        using var client = app.Client();

        var resp = await client.GetAsync($"/admin/enforcement?limit={requested}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(expectedClamped, app.Repo.ListCalls[0].Limit);
    }

    [Fact]
    public async Task List_ReturnsEntries_WithCamelCaseFields_IncludingPlatform()
    {
        using var app = new EnforcementApp();
        app.Repo.ListResult = [SampleEntry(42, "pending")];
        using var client = app.Client();

        var resp = await client.GetAsync("/admin/enforcement?status=pending");
        var raw = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("\"id\":42", raw);
        Assert.Contains("\"platform\":\"google\"", raw);
        Assert.Contains("\"sourceType\":\"ip\"", raw);
        Assert.Contains("\"value\":\"203.0.113.7\"", raw);
        Assert.Contains("\"status\":\"pending\"", raw);
        Assert.Contains("\"campaignScope\":null", raw);
    }

    // ============================================================= approve =

    [Fact]
    public async Task Approve_ValidBatch_ReturnsRequestedAndApprovedCounts()
    {
        using var app = new EnforcementApp();
        app.Repo.ApproveReturns = 2; // 2 of 3 transitioned (1 already-approved skipped)
        using var client = app.Client();

        var resp = await client.SendAsync(Post("/admin/enforcement/approve", """{"ids":[1,2,3]}"""));
        var json = await ReadJson(resp);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(3, json.GetProperty("requested").GetInt32());
        Assert.Equal(2, json.GetProperty("approved").GetInt32());

        var call = Assert.Single(app.Repo.ApproveCalls);
        Assert.Equal(new long[] { 1, 2, 3 }, call.Ids);
    }

    [Fact]
    public async Task Approve_PassesActorKeyHash_MatchingSha256OfTheApiKey()
    {
        using var app = new EnforcementApp();
        using var client = app.Client();

        await client.SendAsync(Post("/admin/enforcement/approve", """{"ids":[7]}"""));

        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(ApiKey));
        var call = Assert.Single(app.Repo.ApproveCalls);
        Assert.Equal(expectedHash, call.ActorKeyHash);
    }

    [Fact]
    public async Task Approve_ReplayingTheSameRequest_ReportsZeroSecondTime_NoThrow()
    {
        using var app = new EnforcementApp();
        using var client = app.Client();

        app.Repo.ApproveReturns = 2;
        var first = await client.SendAsync(Post("/admin/enforcement/approve", """{"ids":[1,2,3]}"""));
        var firstJson = await ReadJson(first);
        Assert.Equal(2, firstJson.GetProperty("approved").GetInt32());

        app.Repo.ApproveReturns = 0; // replay: nothing left pending
        var second = await client.SendAsync(Post("/admin/enforcement/approve", """{"ids":[1,2,3]}"""));
        var secondJson = await ReadJson(second);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(3, secondJson.GetProperty("requested").GetInt32());
        Assert.Equal(0, secondJson.GetProperty("approved").GetInt32());
        Assert.Equal(2, app.Repo.ApproveCalls.Count);
    }

    [Fact]
    public async Task Approve_MissingIds_Returns400ValidationProblem_AndNeverCallsRepository()
    {
        using var app = new EnforcementApp();
        using var client = app.Client();

        var resp = await client.SendAsync(Post("/admin/enforcement/approve", "{}"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
        Assert.Empty(app.Repo.ApproveCalls);
    }

    [Fact]
    public async Task Approve_EmptyIds_Returns400ValidationProblem()
    {
        using var app = new EnforcementApp();
        using var client = app.Client();

        var resp = await client.SendAsync(Post("/admin/enforcement/approve", """{"ids":[]}"""));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Empty(app.Repo.ApproveCalls);
    }

    [Fact]
    public async Task Approve_501Ids_Returns400ValidationProblem_AndNeverCallsRepository()
    {
        using var app = new EnforcementApp();
        using var client = app.Client();

        var ids = string.Join(',', Enumerable.Range(1, 501));
        var resp = await client.SendAsync(Post("/admin/enforcement/approve", $$"""{"ids":[{{ids}}]}"""));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
        Assert.Empty(app.Repo.ApproveCalls);
    }

    [Fact]
    public async Task Approve_500Ids_IsAllowed()
    {
        using var app = new EnforcementApp();
        using var client = app.Client();

        var ids = string.Join(',', Enumerable.Range(1, 500));
        var resp = await client.SendAsync(Post("/admin/enforcement/approve", $$"""{"ids":[{{ids}}]}"""));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Single(app.Repo.ApproveCalls);
    }

    // ============================================================== reject =

    [Fact]
    public async Task Reject_ValidBatch_WithNote_ReturnsRequestedAndRejectedCounts_AndPassesNote()
    {
        using var app = new EnforcementApp();
        app.Repo.RejectReturns = 1;
        using var client = app.Client();

        var resp = await client.SendAsync(
            Post("/admin/enforcement/reject", """{"ids":[9],"note":"false positive - real customer"}"""));
        var json = await ReadJson(resp);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, json.GetProperty("requested").GetInt32());
        Assert.Equal(1, json.GetProperty("rejected").GetInt32());

        var call = Assert.Single(app.Repo.RejectCalls);
        Assert.Equal("false positive - real customer", call.Note);
    }

    [Fact]
    public async Task Reject_PassesActorKeyHash()
    {
        using var app = new EnforcementApp();
        using var client = app.Client();

        await client.SendAsync(Post("/admin/enforcement/reject", """{"ids":[9]}"""));

        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(ApiKey));
        var call = Assert.Single(app.Repo.RejectCalls);
        Assert.Equal(expectedHash, call.ActorKeyHash);
    }

    [Fact]
    public async Task Reject_NoteOver400Chars_Returns400ValidationProblem_AndNeverCallsRepository()
    {
        using var app = new EnforcementApp();
        using var client = app.Client();

        var longNote = new string('x', 401);
        var resp = await client.SendAsync(
            Post("/admin/enforcement/reject", $$"""{"ids":[9],"note":"{{longNote}}"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
        Assert.Empty(app.Repo.RejectCalls);
    }

    [Fact]
    public async Task Reject_NoteExactly400Chars_IsAllowed()
    {
        using var app = new EnforcementApp();
        using var client = app.Client();

        var note = new string('x', 400);
        var resp = await client.SendAsync(
            Post("/admin/enforcement/reject", $$"""{"ids":[9],"note":"{{note}}"}"""));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Single(app.Repo.RejectCalls);
    }

    [Fact]
    public async Task Reject_MissingIds_Returns400ValidationProblem()
    {
        using var app = new EnforcementApp();
        using var client = app.Client();

        var resp = await client.SendAsync(Post("/admin/enforcement/reject", """{"note":"x"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Empty(app.Repo.RejectCalls);
    }

    [Fact]
    public async Task Reject_501Ids_Returns400ValidationProblem()
    {
        using var app = new EnforcementApp();
        using var client = app.Client();

        var ids = string.Join(',', Enumerable.Range(1, 501));
        var resp = await client.SendAsync(Post("/admin/enforcement/reject", $$"""{"ids":[{{ids}}]}"""));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Empty(app.Repo.RejectCalls);
    }
}
