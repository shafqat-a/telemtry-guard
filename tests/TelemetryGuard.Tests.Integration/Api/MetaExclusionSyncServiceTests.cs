using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TelemetryGuard.Api.Workers;
using TelemetryGuard.Core.Time;
using TelemetryGuard.Data;
using TelemetryGuard.Integrations.Meta;
using TelemetryGuard.Tests.Integration.Sql;

namespace TelemetryGuard.Tests.Integration.Api;

/// <summary>
/// INT-04 end to end against real migrated SQL Server (DAT-08's SqlServerFixture):
/// the ip-unsupported sweep (even in dry-run), placement push (new list / existing
/// tenant block-list id / found-by-name), MetaApiException failure mapping, the
/// dry-run gate, the pending-never-pushed guardrail, and RunOnceAsync's tenant
/// enumeration/isolation behavior — mirrors GoogleAdsExclusionSyncServiceTests
/// (INT-03) in structure.
///
/// Every test creates its OWN tenant/queue rows (never a shared fixture tenant) and
/// uses unique-per-test values (fresh GUIDs) — this worker's SELECTs pull EVERY
/// matching row for a tenant, and RunOnceAsync sweeps every Meta-enabled tenant in
/// the shared fixture, so cross-test state must never collide.
/// </summary>
[Collection("sqlserver")]
[Trait("Category", "Integration")]
public sealed class MetaExclusionSyncServiceTests(SqlServerFixture fx)
{
    private const string NoIpMessage =
        "Meta Marketing API provides no IP exclusion capability; entry cannot be enforced on Meta.";

    private sealed class FakeMetaClient : IMetaMarketingClient
    {
        public int FindCalls, CreateCalls, AddCalls;
        public readonly List<(string BusinessId, string Name)> FindRequests = [];
        public readonly List<(string BusinessId, string Name, IReadOnlyList<string> Urls)> CreateRequests = [];
        public readonly List<(string BlockListId, IReadOnlyList<string> Urls)> AddRequests = [];

        public Func<string, string, MetaBlockList?> FindHandler { get; set; } = (_, _) => null;
        public Func<string, string, IReadOnlyList<string>, string> CreateHandler { get; set; } =
            (_, _, _) => "created-block-list-id";

        public Task<MetaBlockList?> FindBlockListAsync(string businessId, string name, CancellationToken ct)
        {
            FindCalls++;
            FindRequests.Add((businessId, name));
            return Task.FromResult(FindHandler(businessId, name));
        }

        public Task<string> CreateBlockListAsync(
            string businessId, string name, IReadOnlyList<string> publisherUrls, CancellationToken ct)
        {
            CreateCalls++;
            CreateRequests.Add((businessId, name, publisherUrls));
            return Task.FromResult(CreateHandler(businessId, name, publisherUrls));
        }

        public Task AddPublisherUrlsAsync(string blockListId, IReadOnlyList<string> publisherUrls, CancellationToken ct)
        {
            AddCalls++;
            AddRequests.Add((blockListId, publisherUrls));
            return Task.CompletedTask;
        }
    }

    /// <summary>Only source of ISystemConnectionFactory in these tests — the tenant
    /// used to build it is irrelevant (the factory is a DI singleton, not tenant-bound).</summary>
    private ISystemConnectionFactory SystemConnections()
        => RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA)
            .GetRequiredService<ISystemConnectionFactory>();

    private static MetaExclusionSyncService BuildWorker(
        ISystemConnectionFactory systemConnections, IMetaMarketingClient client, MetaOptions options)
        => new(systemConnections, client, Options.Create(options), SystemClock.Instance,
            NullLogger<MetaExclusionSyncService>.Instance);

    private static MetaOptions LiveOptions() => new() { DryRun = false, SystemUserToken = "test-system-user-token" };

    private static async Task<Guid> CreateTenantAsync(
        SqlServerFixture fx, string? metaBusinessId, string? metaBlockListId = null)
    {
        var tenantId = Guid.NewGuid();
        await using var sys = await fx.OpenAsync(WellKnownTenants.System);
        await sys.ExecuteAsync(
            "INSERT INTO dbo.Tenants (TenantId, Name, MetaBusinessId, MetaBlockListId) VALUES (@tid, N'MetaSyncTest', @biz, @blockListId)",
            new { tid = tenantId, biz = metaBusinessId, blockListId = metaBlockListId });
        return tenantId;
    }

    private static async Task<long> SeedQueueRowAsync(
        SqlServerFixture fx, Guid tenantId, string platform, string sourceType, string value, string status = "approved")
    {
        await using var conn = await fx.OpenAsync(tenantId);
        return await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO dbo.ExclusionQueue (TenantId, Platform, SourceType, Value, Reason, Status)
            OUTPUT inserted.Id
            VALUES (@tid, @platform, @st, @val, N'score=90 rules=test', @status)
            """,
            new { tid = tenantId, platform, st = sourceType, val = value, status });
    }

    private static async Task<(string Status, DateTime? PushedUtc, string? LastError)> ReadRowAsync(
        SqlServerFixture fx, Guid tenantId, long id)
    {
        await using var conn = await fx.OpenAsync(tenantId);
        return await conn.QuerySingleAsync<(string, DateTime?, string?)>(
            "SELECT Status, PushedUtc, LastError FROM dbo.ExclusionQueue WHERE TenantId = @tid AND Id = @id",
            new { tid = tenantId, id });
    }

    private static async Task<string?> ReadMetaBlockListIdAsync(SqlServerFixture fx, Guid tenantId)
    {
        await using var conn = await fx.OpenAsync(tenantId);
        return await conn.ExecuteScalarAsync<string?>(
            "SELECT MetaBlockListId FROM dbo.Tenants WHERE TenantId = @tid", new { tid = tenantId });
    }

    // ============================================================ ip sweep / honesty ===

    [Fact]
    public async Task IpSweep_MarksPendingAndApproved_Unsupported_EvenInDryRun_GoogleRowsUntouched()
    {
        var tenantId = await CreateTenantAsync(fx, "biz-100");
        var pendingIp = await SeedQueueRowAsync(fx, tenantId, "meta", "ip", "203.0.113.10", "pending");
        var approvedIp = await SeedQueueRowAsync(fx, tenantId, "meta", "ip", "203.0.113.11", "approved");
        var googleIp = await SeedQueueRowAsync(fx, tenantId, "google", "ip", "203.0.113.12", "approved");
        var placementId = await SeedQueueRowAsync(fx, tenantId, "meta", "placement", "bad-publisher.example");

        var client = new FakeMetaClient();
        var worker = BuildWorker(SystemConnections(), client, new MetaOptions()); // DryRun=true, no token

        await using var conn = await fx.OpenAsync(tenantId);
        await worker.SyncTenantAsync(tenantId, "biz-100", conn, CancellationToken.None);

        var pendingRow = await ReadRowAsync(fx, tenantId, pendingIp);
        Assert.Equal("unsupported", pendingRow.Status);
        Assert.Equal(NoIpMessage, pendingRow.LastError);

        var approvedRow = await ReadRowAsync(fx, tenantId, approvedIp);
        Assert.Equal("unsupported", approvedRow.Status);
        Assert.Equal(NoIpMessage, approvedRow.LastError);

        var googleRow = await ReadRowAsync(fx, tenantId, googleIp);
        Assert.Equal("approved", googleRow.Status); // different platform — sweep never touches it

        var placementRow = await ReadRowAsync(fx, tenantId, placementId);
        Assert.Equal("approved", placementRow.Status); // dry-run: placements untouched

        Assert.Equal(0, client.FindCalls);
        Assert.Equal(0, client.CreateCalls);
        Assert.Equal(0, client.AddCalls);
    }

    // =============================================================== dry-run gate ===

    [Fact]
    public async Task DryRunFalse_WithNoToken_StillDryRuns_ZeroClientCalls()
    {
        var tenantId = await CreateTenantAsync(fx, "biz-101");
        var id = await SeedQueueRowAsync(fx, tenantId, "meta", "placement", "bad.example");

        var options = new MetaOptions { DryRun = false }; // token still empty
        Assert.True(options.EffectiveDryRun);
        var client = new FakeMetaClient();
        var worker = BuildWorker(SystemConnections(), client, options);

        await using var conn = await fx.OpenAsync(tenantId);
        await worker.SyncTenantAsync(tenantId, "biz-101", conn, CancellationToken.None);

        Assert.Equal("approved", (await ReadRowAsync(fx, tenantId, id)).Status);
        Assert.Equal(0, client.FindCalls + client.CreateCalls + client.AddCalls);
    }

    // ==================================================================== success ===

    [Fact]
    public async Task SuccessPath_NoExistingBlockList_CreatesNewList_PersistsIdAndPushesRows()
    {
        var tenantId = await CreateTenantAsync(fx, "biz-200"); // MetaBlockListId starts NULL
        var id1 = await SeedQueueRowAsync(fx, tenantId, "meta", "placement", "bad1.example");
        var id2 = await SeedQueueRowAsync(fx, tenantId, "meta", "placement", "bad2.example");

        var client = new FakeMetaClient { CreateHandler = (_, _, _) => "new-block-list-id" };
        var worker = BuildWorker(SystemConnections(), client, LiveOptions());

        await using var conn = await fx.OpenAsync(tenantId);
        await worker.SyncTenantAsync(tenantId, "biz-200", conn, CancellationToken.None);

        Assert.Equal(1, client.FindCalls);
        Assert.Equal(1, client.CreateCalls);
        Assert.Equal(0, client.AddCalls);
        Assert.Equal(2, client.CreateRequests[0].Urls.Count);

        Assert.Equal("new-block-list-id", await ReadMetaBlockListIdAsync(fx, tenantId));
        Assert.Equal("pushed", (await ReadRowAsync(fx, tenantId, id1)).Status);
        var row2 = await ReadRowAsync(fx, tenantId, id2);
        Assert.Equal("pushed", row2.Status);
        Assert.NotNull(row2.PushedUtc);
        Assert.Null(row2.LastError);
    }

    [Fact]
    public async Task SuccessPath_ExistingBlockListIdOnTenant_SkipsFindAndCreate_AppendsDirectly()
    {
        var tenantId = await CreateTenantAsync(fx, "biz-201", metaBlockListId: "already-known-id");
        var id = await SeedQueueRowAsync(fx, tenantId, "meta", "placement", "bad.example");

        var client = new FakeMetaClient();
        var worker = BuildWorker(SystemConnections(), client, LiveOptions());

        await using var conn = await fx.OpenAsync(tenantId);
        await worker.SyncTenantAsync(tenantId, "biz-201", conn, CancellationToken.None);

        Assert.Equal(0, client.FindCalls);
        Assert.Equal(0, client.CreateCalls);
        Assert.Equal(1, client.AddCalls);
        Assert.Equal("already-known-id", client.AddRequests[0].BlockListId);

        Assert.Equal("pushed", (await ReadRowAsync(fx, tenantId, id)).Status);
        Assert.Equal("already-known-id", await ReadMetaBlockListIdAsync(fx, tenantId));
    }

    [Fact]
    public async Task SuccessPath_FindsExistingListByName_PersistsIdAndAppendsUrls()
    {
        var tenantId = await CreateTenantAsync(fx, "biz-202"); // MetaBlockListId starts NULL
        var id = await SeedQueueRowAsync(fx, tenantId, "meta", "placement", "bad.example");

        var client = new FakeMetaClient { FindHandler = (_, name) => new MetaBlockList("found-id-777", name) };
        var worker = BuildWorker(SystemConnections(), client, LiveOptions());

        await using var conn = await fx.OpenAsync(tenantId);
        await worker.SyncTenantAsync(tenantId, "biz-202", conn, CancellationToken.None);

        Assert.Equal(1, client.FindCalls);
        Assert.Equal(0, client.CreateCalls);
        Assert.Equal(1, client.AddCalls);
        Assert.Equal("found-id-777", client.AddRequests[0].BlockListId);

        Assert.Equal("found-id-777", await ReadMetaBlockListIdAsync(fx, tenantId));
        Assert.Equal("pushed", (await ReadRowAsync(fx, tenantId, id)).Status);
    }

    // ================================================================== failure ===

    [Fact]
    public async Task Failure_MetaApiException_MarksBatchFailed_WithFbTraceIdInLastError_NeverPersistsBlockListId()
    {
        var tenantId = await CreateTenantAsync(fx, "biz-300");
        var id = await SeedQueueRowAsync(fx, tenantId, "meta", "placement", "bad.example");

        var client = new FakeMetaClient
        {
            CreateHandler = (_, _, _) => throw new MetaApiException(
                "Invalid parameter", "OAuthException", 100, null, "trace-abc-123", retryable: false),
        };
        var worker = BuildWorker(SystemConnections(), client, LiveOptions());

        await using var conn = await fx.OpenAsync(tenantId);
        await worker.SyncTenantAsync(tenantId, "biz-300", conn, CancellationToken.None);

        var row = await ReadRowAsync(fx, tenantId, id);
        Assert.Equal("failed", row.Status);
        Assert.NotNull(row.LastError);
        Assert.Contains("trace-abc-123", row.LastError);
        Assert.Contains("Invalid parameter", row.LastError);
        Assert.True(row.LastError!.Length <= 2000);

        Assert.Null(await ReadMetaBlockListIdAsync(fx, tenantId)); // create failed — never persisted
    }

    // ============================================================== D21 guardrail ===

    [Fact]
    public async Task PendingPlacements_AreNeverPushed()
    {
        var tenantId = await CreateTenantAsync(fx, "biz-400");
        var pendingId = await SeedQueueRowAsync(fx, tenantId, "meta", "placement", "bad.example", "pending");

        var client = new FakeMetaClient();
        var worker = BuildWorker(SystemConnections(), client, LiveOptions());

        await using var conn = await fx.OpenAsync(tenantId);
        await worker.SyncTenantAsync(tenantId, "biz-400", conn, CancellationToken.None);

        Assert.Equal(0, client.FindCalls + client.CreateCalls + client.AddCalls);
        Assert.Equal("pending", (await ReadRowAsync(fx, tenantId, pendingId)).Status);
    }

    // ================================================ tenant enumeration/isolation ===

    private sealed class FailingForTenantConnectionFactory(ISystemConnectionFactory inner, Guid failFor)
        : ISystemConnectionFactory
    {
        public Task<SqlConnection> OpenSystemAsync(CancellationToken ct) => inner.OpenSystemAsync(ct);

        public Task<SqlConnection> OpenForTenantAsync(Guid tenantId, CancellationToken ct)
            => tenantId == failFor
                ? throw new InvalidOperationException("simulated per-tenant failure")
                : inner.OpenForTenantAsync(tenantId, ct);
    }

    [Fact]
    public async Task RunOnceAsync_TenantWithNullMetaBusinessId_IsSkippedEntirely()
    {
        var tenantId = await CreateTenantAsync(fx, metaBusinessId: null);
        var value = $"skip-{Guid.NewGuid():N}.example";
        var id = await SeedQueueRowAsync(fx, tenantId, "meta", "placement", value);

        var client = new FakeMetaClient();
        var worker = BuildWorker(SystemConnections(), client, LiveOptions());

        await worker.RunOnceAsync(CancellationToken.None);

        // RunOnceAsync sweeps every Meta-enabled tenant in the shared fixture (including
        // ones left dirty by earlier tests in this class), so a global call-count
        // assertion would be flaky. What THIS test proves is that its own tenant/row —
        // which has no MetaBusinessId — was never enumerated at all.
        Assert.DoesNotContain(client.CreateRequests, r => r.Urls.Contains(value));
        Assert.DoesNotContain(client.AddRequests, r => r.Urls.Contains(value));
        var row = await ReadRowAsync(fx, tenantId, id);
        Assert.Equal("approved", row.Status);
    }

    [Fact]
    public async Task RunOnceAsync_OneTenantFailure_DoesNotBlockAnotherTenantInTheSameCycle()
    {
        var tenantA = await CreateTenantAsync(fx, "biz-A-fails");
        var valueA = $"a-{Guid.NewGuid():N}.example";
        var idA = await SeedQueueRowAsync(fx, tenantA, "meta", "placement", valueA);

        var tenantB = await CreateTenantAsync(fx, "biz-B-ok");
        var valueB = $"b-{Guid.NewGuid():N}.example";
        var idB = await SeedQueueRowAsync(fx, tenantB, "meta", "placement", valueB);

        var client = new FakeMetaClient();
        var failingFactory = new FailingForTenantConnectionFactory(SystemConnections(), failFor: tenantA);
        var worker = BuildWorker(failingFactory, client, LiveOptions());

        await worker.RunOnceAsync(CancellationToken.None);

        // Tenant A's connection acquisition threw before any write — its row is untouched.
        var rowA = await ReadRowAsync(fx, tenantA, idA);
        Assert.Equal("approved", rowA.Status);

        // Tenant B still synced in the same cycle.
        var rowB = await ReadRowAsync(fx, tenantB, idB);
        Assert.Equal("pushed", rowB.Status);
    }
}
