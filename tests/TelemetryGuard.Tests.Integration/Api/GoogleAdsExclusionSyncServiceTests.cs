using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TelemetryGuard.Api.Workers;
using TelemetryGuard.Core.Time;
using TelemetryGuard.Data;
using TelemetryGuard.Data.Repositories;
using TelemetryGuard.Integrations.GoogleAds;
using TelemetryGuard.Tests.Integration.Sql;

namespace TelemetryGuard.Tests.Integration.Api;

/// <summary>
/// INT-03 end to end against real migrated SQL Server (DAT-08's SqlServerFixture):
/// dry-run inertness, success, partial failure, idempotent re-run, missing
/// ExternalCampaignId, tenant-wide fan-out, and the tenant-enumeration/isolation
/// behavior of RunOnceAsync. Every test creates its OWN tenant/campaign rows
/// (never fx.TenantA/TenantB) — this worker's SELECT pulls EVERY approved google
/// row for a tenant, so sharing the fixture's shared tenant with other test
/// classes' seeded rows would make cross-test state leak into (and get mutated
/// by) this worker's runs.
/// </summary>
[Collection("sqlserver")]
[Trait("Category", "Integration")]
public sealed class GoogleAdsExclusionSyncServiceTests(SqlServerFixture fx)
{
    private sealed class FakeGateway : IGoogleAdsGateway
    {
        public int CallCount;
        public readonly List<(string CustomerId, IReadOnlyList<CriterionAdd> Adds, IReadOnlyList<string> Removes)> Calls = [];

        public Func<string, IReadOnlyList<CriterionAdd>, IReadOnlyList<string>, MutateOutcome> Handler { get; set; } =
            (_, adds, _) => new MutateOutcome(
                adds.Select((a, i) => (a, $"customers/1/campaignCriteria/{a.GoogleCampaignId}~{i}-{Guid.NewGuid():N}")).ToList(),
                Array.Empty<(CriterionAdd, string)>());

        public Task<MutateOutcome> MutateAsync(
            string customerId, IReadOnlyList<CriterionAdd> adds, IReadOnlyList<string> removes, CancellationToken ct)
        {
            Interlocked.Increment(ref CallCount);
            lock (Calls) Calls.Add((customerId, adds, removes));
            return Task.FromResult(Handler(customerId, adds, removes));
        }
    }

    /// <summary>Only source of ISystemConnectionFactory in these tests — the tenant
    /// used to build it is irrelevant (the factory is a DI singleton, not tenant-bound).</summary>
    private ISystemConnectionFactory SystemConnections()
        => RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA)
            .GetRequiredService<ISystemConnectionFactory>();

    private static GoogleAdsExclusionSyncService BuildWorker(
        ISystemConnectionFactory systemConnections, IGoogleAdsGateway gateway, GoogleAdsOptions options)
        => new(systemConnections, gateway, Options.Create(options), SystemClock.Instance,
            NullLogger<GoogleAdsExclusionSyncService>.Instance);

    private static GoogleAdsOptions LiveOptions() => new()
    {
        DryRun = false,
        DeveloperToken = "dev-token",
        OAuthClientId = "client-id",
        OAuthClientSecret = "client-secret",
        OAuthRefreshToken = "refresh-token",
    };

    private static async Task<Guid> CreateTenantAsync(SqlServerFixture fx, string? googleAdsCustomerId)
    {
        var tenantId = Guid.NewGuid();
        await using var sys = await fx.OpenAsync(WellKnownTenants.System);
        await sys.ExecuteAsync(
            "INSERT INTO dbo.Tenants (TenantId, Name, GoogleAdsCustomerId) VALUES (@tid, N'GoogleAdsSyncTest', @cid)",
            new { tid = tenantId, cid = googleAdsCustomerId });
        return tenantId;
    }

    private static async Task<Guid> CreateCampaignAsync(
        SqlServerFixture fx, Guid tenantId, string? externalCampaignId, byte status = 0, string platform = "google")
    {
        var campaignId = Guid.NewGuid();
        await using var sys = await fx.OpenAsync(WellKnownTenants.System);
        await sys.ExecuteAsync(
            """
            INSERT INTO dbo.Campaigns (TenantId, CampaignId, Platform, ExternalCampaignId, LandingUrl, Status)
            VALUES (@tid, @cid, @platform, @ext, N'https://example.com/lp', @status)
            """,
            new { tid = tenantId, cid = campaignId, platform, ext = externalCampaignId, status });
        return campaignId;
    }

    private static async Task<long> SeedQueueRowAsync(
        SqlServerFixture fx, Guid tenantId, string sourceType, string value, Guid? campaignScope, string status = "approved")
    {
        await using var conn = await fx.OpenAsync(tenantId);
        return await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO dbo.ExclusionQueue (TenantId, Platform, SourceType, Value, Reason, Status, CampaignScope)
            OUTPUT inserted.Id
            VALUES (@tid, 'google', @st, @val, N'score=90 rules=test', @status, @scope)
            """,
            new { tid = tenantId, st = sourceType, val = value, status, scope = campaignScope });
    }

    private static async Task<(string Status, DateTime? PushedUtc, string? LastError)> ReadRowAsync(
        SqlServerFixture fx, Guid tenantId, long id)
    {
        await using var conn = await fx.OpenAsync(tenantId);
        return await conn.QuerySingleAsync<(string, DateTime?, string?)>(
            "SELECT Status, PushedUtc, LastError FROM dbo.ExclusionQueue WHERE TenantId = @tid AND Id = @id",
            new { tid = tenantId, id });
    }

    // =========================================================== dry-run ===

    [Fact]
    public async Task DryRun_Default_MakesNoGatewayCalls_AndLeavesRowApproved()
    {
        var tenantId = await CreateTenantAsync(fx, "1112223330");
        var campaignId = await CreateCampaignAsync(fx, tenantId, "999000111");
        var id = await SeedQueueRowAsync(fx, tenantId, "ip", $"203.0.113.{Random.Shared.Next(1, 254)}", campaignId);

        var gateway = new FakeGateway();
        var worker = BuildWorker(SystemConnections(), gateway, new GoogleAdsOptions()); // DryRun=true, no creds

        await using var conn = await fx.OpenAsync(tenantId);
        await worker.SyncTenantAsync(tenantId, "1112223330", conn, CancellationToken.None);

        Assert.Equal(0, gateway.CallCount);
        var row = await ReadRowAsync(fx, tenantId, id);
        Assert.Equal("approved", row.Status);
        Assert.Null(row.PushedUtc);
    }

    [Fact]
    public async Task DryRunFalse_WithEmptyCredentials_StillDryRuns()
    {
        var tenantId = await CreateTenantAsync(fx, "1112223331");
        var campaignId = await CreateCampaignAsync(fx, tenantId, "999000112");
        var id = await SeedQueueRowAsync(fx, tenantId, "ip", $"203.0.113.{Random.Shared.Next(1, 254)}", campaignId);

        var gateway = new FakeGateway();
        var options = new GoogleAdsOptions { DryRun = false }; // credentials still empty
        Assert.True(options.EffectiveDryRun);
        var worker = BuildWorker(SystemConnections(), gateway, options);

        await using var conn = await fx.OpenAsync(tenantId);
        await worker.SyncTenantAsync(tenantId, "1112223331", conn, CancellationToken.None);

        Assert.Equal(0, gateway.CallCount);
        var row = await ReadRowAsync(fx, tenantId, id);
        Assert.Equal("approved", row.Status);
    }

    // ========================================================= success =====

    [Fact]
    public async Task SuccessPath_IpRow_TransitionsToPushed_WithStateRow()
    {
        var tenantId = await CreateTenantAsync(fx, "2223334440");
        var campaignId = await CreateCampaignAsync(fx, tenantId, "111222333");
        var ip = $"198.51.100.{Random.Shared.Next(1, 254)}";
        var id = await SeedQueueRowAsync(fx, tenantId, "ip", ip, campaignId);

        var gateway = new FakeGateway();
        var worker = BuildWorker(SystemConnections(), gateway, LiveOptions());

        await using var conn = await fx.OpenAsync(tenantId);
        await worker.SyncTenantAsync(tenantId, "2223334440", conn, CancellationToken.None);

        Assert.Equal(1, gateway.CallCount);
        var row = await ReadRowAsync(fx, tenantId, id);
        Assert.Equal("pushed", row.Status);
        Assert.NotNull(row.PushedUtc);
        Assert.Null(row.LastError);

        await using var conn2 = await fx.OpenAsync(tenantId);
        var state = await conn2.QuerySingleAsync<(string GoogleCampaignId, string SourceType, string SourceValue)>(
            "SELECT GoogleCampaignId, SourceType, SourceValue FROM dbo.GoogleAdsPushedExclusions WHERE TenantId = @tid AND ExclusionQueueId = @id",
            new { tid = tenantId, id });
        Assert.Equal("111222333", state.GoogleCampaignId);
        Assert.Equal("ip", state.SourceType);
        Assert.Equal(ip, state.SourceValue);
    }

    [Fact] // REALITY CHECK (see the worker's class doc): no producer writes 'placement'
           // rows today, so this seeds one directly to prove the path works end to end.
    public async Task SuccessPath_PlacementRow_TransitionsToPushed()
    {
        var tenantId = await CreateTenantAsync(fx, "2223334441");
        var campaignId = await CreateCampaignAsync(fx, tenantId, "111222334");
        var placement = $"example{Guid.NewGuid():N}.com/ads";
        var id = await SeedQueueRowAsync(fx, tenantId, "placement", placement, campaignId);

        var gateway = new FakeGateway();
        var worker = BuildWorker(SystemConnections(), gateway, LiveOptions());

        await using var conn = await fx.OpenAsync(tenantId);
        await worker.SyncTenantAsync(tenantId, "2223334441", conn, CancellationToken.None);

        var row = await ReadRowAsync(fx, tenantId, id);
        Assert.Equal("pushed", row.Status);

        await using var conn2 = await fx.OpenAsync(tenantId);
        var sourceType = await conn2.ExecuteScalarAsync<string>(
            "SELECT SourceType FROM dbo.GoogleAdsPushedExclusions WHERE TenantId = @tid AND ExclusionQueueId = @id",
            new { tid = tenantId, id });
        Assert.Equal("placement", sourceType);
    }

    [Fact]
    public async Task NonApprovedRows_AreNeverSelected()
    {
        var tenantId = await CreateTenantAsync(fx, "2223334442");
        var campaignId = await CreateCampaignAsync(fx, tenantId, "111222335");
        var pendingId = await SeedQueueRowAsync(fx, tenantId, "ip", $"203.0.113.{Random.Shared.Next(1, 254)}", campaignId, "pending");
        var rejectedId = await SeedQueueRowAsync(fx, tenantId, "ip", $"203.0.113.{Random.Shared.Next(1, 254)}", campaignId, "rejected");
        var failedId = await SeedQueueRowAsync(fx, tenantId, "ip", $"203.0.113.{Random.Shared.Next(1, 254)}", campaignId, "failed");
        var approvedIp = $"203.0.113.{Random.Shared.Next(1, 254)}";
        var approvedId = await SeedQueueRowAsync(fx, tenantId, "ip", approvedIp, campaignId);

        var gateway = new FakeGateway();
        var worker = BuildWorker(SystemConnections(), gateway, LiveOptions());

        await using var conn = await fx.OpenAsync(tenantId);
        await worker.SyncTenantAsync(tenantId, "2223334442", conn, CancellationToken.None);

        Assert.Equal("pending", (await ReadRowAsync(fx, tenantId, pendingId)).Status);
        Assert.Equal("rejected", (await ReadRowAsync(fx, tenantId, rejectedId)).Status);
        Assert.Equal("failed", (await ReadRowAsync(fx, tenantId, failedId)).Status);
        Assert.Equal("pushed", (await ReadRowAsync(fx, tenantId, approvedId)).Status);

        // Only the approved row's value ever reached the gateway.
        var allAdds = gateway.Calls.SelectMany(c => c.Adds).Select(a => a.SourceValue).ToList();
        Assert.Single(allAdds);
        Assert.Equal(approvedIp, allAdds[0]);
    }

    // ==================================================== partial failure ===

    [Fact]
    public async Task PartialFailure_OnlyFailedAddsBecomeFailed_LastErrorTruncated()
    {
        var tenantId = await CreateTenantAsync(fx, "2223334443");
        var campaignId = await CreateCampaignAsync(fx, tenantId, "111222336");
        var okIp = $"203.0.113.{Random.Shared.Next(1, 254)}";
        var badIp = $"203.0.113.{Random.Shared.Next(1, 254)}";
        var okId = await SeedQueueRowAsync(fx, tenantId, "ip", okIp, campaignId);
        var badId = await SeedQueueRowAsync(fx, tenantId, "ip", badIp, campaignId);

        var longError = new string('e', 2500); // must be truncated to <= 2000 chars
        var gateway = new FakeGateway
        {
            Handler = (_, adds, _) =>
            {
                var created = adds.Where(a => a.SourceValue == okIp)
                    .Select(a => (a, $"customers/1/campaignCriteria/{a.GoogleCampaignId}~{Guid.NewGuid():N}")).ToList();
                var failures = adds.Where(a => a.SourceValue == badIp)
                    .Select(a => (a, longError)).ToList();
                return new MutateOutcome(created, failures);
            },
        };
        var worker = BuildWorker(SystemConnections(), gateway, LiveOptions());

        await using var conn = await fx.OpenAsync(tenantId);
        await worker.SyncTenantAsync(tenantId, "2223334443", conn, CancellationToken.None);

        var okRow = await ReadRowAsync(fx, tenantId, okId);
        Assert.Equal("pushed", okRow.Status);
        Assert.NotNull(okRow.PushedUtc);

        var badRow = await ReadRowAsync(fx, tenantId, badId);
        Assert.Equal("failed", badRow.Status);
        Assert.NotNull(badRow.LastError);
        Assert.True(badRow.LastError!.Length <= 2000);
        Assert.False(string.IsNullOrEmpty(badRow.LastError));
    }

    // ======================================================= idempotency ===

    [Fact]
    public async Task Idempotent_ReRunAfterSuccess_MakesNoFurtherGatewayCalls_NoDuplicateStateRows()
    {
        var tenantId = await CreateTenantAsync(fx, "2223334444");
        var campaignId = await CreateCampaignAsync(fx, tenantId, "111222337");
        var ip = $"203.0.113.{Random.Shared.Next(1, 254)}";
        var id = await SeedQueueRowAsync(fx, tenantId, "ip", ip, campaignId);

        var gateway = new FakeGateway();
        var worker = BuildWorker(SystemConnections(), gateway, LiveOptions());

        await using (var conn = await fx.OpenAsync(tenantId))
            await worker.SyncTenantAsync(tenantId, "2223334444", conn, CancellationToken.None);
        Assert.Equal(1, gateway.CallCount);

        await using (var conn = await fx.OpenAsync(tenantId))
            await worker.SyncTenantAsync(tenantId, "2223334444", conn, CancellationToken.None); // re-run
        Assert.Equal(1, gateway.CallCount); // row is no longer 'approved' -> SELECT returns nothing -> no 2nd call

        await using var check = await fx.OpenAsync(tenantId);
        var stateCount = await check.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.GoogleAdsPushedExclusions WHERE TenantId = @tid AND ExclusionQueueId = @id",
            new { tid = tenantId, id });
        Assert.Equal(1, stateCount);
    }

    // ============================================== missing campaign data ===

    [Fact]
    public async Task ScopedRow_CampaignWithNoExternalCampaignId_MarksRowFailed_NeverCallsGateway()
    {
        var tenantId = await CreateTenantAsync(fx, "2223334445");
        var campaignId = await CreateCampaignAsync(fx, tenantId, externalCampaignId: null);
        var id = await SeedQueueRowAsync(fx, tenantId, "ip", $"203.0.113.{Random.Shared.Next(1, 254)}", campaignId);

        var gateway = new FakeGateway();
        var worker = BuildWorker(SystemConnections(), gateway, LiveOptions());

        await using var conn = await fx.OpenAsync(tenantId);
        await worker.SyncTenantAsync(tenantId, "2223334445", conn, CancellationToken.None);

        Assert.Equal(0, gateway.CallCount);
        var row = await ReadRowAsync(fx, tenantId, id);
        Assert.Equal("failed", row.Status);
        Assert.Equal("campaign has no ExternalCampaignId", row.LastError);
    }

    // ========================================================== fan-out ====

    [Fact]
    public async Task TenantWideRow_FansOutToEveryActiveGoogleCampaign_WithExternalCampaignId()
    {
        var tenantId = await CreateTenantAsync(fx, "2223334446");
        var eligible1 = await CreateCampaignAsync(fx, tenantId, "300000001");
        var eligible2 = await CreateCampaignAsync(fx, tenantId, "300000002");
        await CreateCampaignAsync(fx, tenantId, externalCampaignId: null);       // excluded: no ExternalCampaignId
        await CreateCampaignAsync(fx, tenantId, "300000003", status: 1);         // excluded: paused (Status != 0)
        await CreateCampaignAsync(fx, tenantId, "300000004", platform: "meta");  // excluded: not google

        var ip = $"203.0.113.{Random.Shared.Next(1, 254)}";
        var id = await SeedQueueRowAsync(fx, tenantId, "ip", ip, campaignScope: null); // tenant-wide

        var gateway = new FakeGateway();
        var worker = BuildWorker(SystemConnections(), gateway, LiveOptions());

        await using var conn = await fx.OpenAsync(tenantId);
        await worker.SyncTenantAsync(tenantId, "2223334446", conn, CancellationToken.None);

        var row = await ReadRowAsync(fx, tenantId, id);
        Assert.Equal("pushed", row.Status);

        await using var check = await fx.OpenAsync(tenantId);
        var pushedCampaigns = (await check.QueryAsync<string>(
            "SELECT GoogleCampaignId FROM dbo.GoogleAdsPushedExclusions WHERE TenantId = @tid AND ExclusionQueueId = @id",
            new { tid = tenantId, id })).ToList();
        Assert.Equal(2, pushedCampaigns.Count);
        Assert.Contains("300000001", pushedCampaigns);
        Assert.Contains("300000002", pushedCampaigns);
        _ = eligible1; _ = eligible2;
    }

    // ============================================ tenant enumeration/isolation ===

    private sealed class FailingForTenantConnectionFactory(ISystemConnectionFactory inner, Guid failFor) : ISystemConnectionFactory
    {
        public Task<SqlConnection> OpenSystemAsync(CancellationToken ct) => inner.OpenSystemAsync(ct);

        public Task<SqlConnection> OpenForTenantAsync(Guid tenantId, CancellationToken ct)
            => tenantId == failFor
                ? throw new InvalidOperationException("simulated per-tenant failure")
                : inner.OpenForTenantAsync(tenantId, ct);
    }

    [Fact]
    public async Task RunOnceAsync_TenantWithNullCustomerId_IsSkippedEntirely()
    {
        var tenantId = await CreateTenantAsync(fx, googleAdsCustomerId: null);
        var campaignId = await CreateCampaignAsync(fx, tenantId, "400000001");
        var ip = $"203.0.113.{Random.Shared.Next(1, 254)}";
        var id = await SeedQueueRowAsync(fx, tenantId, "ip", ip, campaignId);

        var gateway = new FakeGateway();
        var worker = BuildWorker(SystemConnections(), gateway, LiveOptions());

        await worker.RunOnceAsync(CancellationToken.None);

        // RunOnceAsync sweeps every tenant in the shared fixture (including ones left
        // 'approved' by earlier dry-run tests in this class) — so a global call-count
        // assertion would be flaky. What THIS test proves is that its own NULL-customerId
        // tenant/row was never touched: it never reaches the gateway, and its row stays
        // untouched (the tenant was never even enumerated by the WHERE GoogleAdsCustomerId
        // IS NOT NULL filter).
        Assert.DoesNotContain(gateway.Calls, c => c.Adds.Any(a => a.SourceValue == ip));
        var row = await ReadRowAsync(fx, tenantId, id);
        Assert.Equal("approved", row.Status);
        Assert.Null(row.PushedUtc);
    }

    [Fact]
    public async Task RunOnceAsync_OneTenantFailure_DoesNotBlockAnotherTenantInTheSameCycle()
    {
        var tenantA = await CreateTenantAsync(fx, "5000000001");
        var campaignA = await CreateCampaignAsync(fx, tenantA, "500000001");
        var idA = await SeedQueueRowAsync(fx, tenantA, "ip", $"203.0.113.{Random.Shared.Next(1, 254)}", campaignA);

        var tenantB = await CreateTenantAsync(fx, "5000000002");
        var campaignB = await CreateCampaignAsync(fx, tenantB, "500000002");
        var ipB = $"203.0.113.{Random.Shared.Next(1, 254)}";
        var idB = await SeedQueueRowAsync(fx, tenantB, "ip", ipB, campaignB);

        var gateway = new FakeGateway();
        var failingFactory = new FailingForTenantConnectionFactory(SystemConnections(), failFor: tenantA);
        var worker = BuildWorker(failingFactory, gateway, LiveOptions());

        await worker.RunOnceAsync(CancellationToken.None);

        // Tenant A's connection acquisition threw before any write — its row is untouched.
        var rowA = await ReadRowAsync(fx, tenantA, idA);
        Assert.Equal("approved", rowA.Status);

        // Tenant B still synced in the same cycle.
        var rowB = await ReadRowAsync(fx, tenantB, idB);
        Assert.Equal("pushed", rowB.Status);
    }

    // ==================================================================== RLS ===

    [Fact]
    public async Task GoogleAdsPushedExclusions_IsRlsProtected_UnstampedZeroRows_CrossTenantInvisible()
    {
        var tenantA = await CreateTenantAsync(fx, "6000000001");
        var campaignA = await CreateCampaignAsync(fx, tenantA, "600000001");
        var ip = $"203.0.113.{Random.Shared.Next(1, 254)}";
        var id = await SeedQueueRowAsync(fx, tenantA, "ip", ip, campaignA);

        var gateway = new FakeGateway();
        var worker = BuildWorker(SystemConnections(), gateway, LiveOptions());
        await using (var conn = await fx.OpenAsync(tenantA))
            await worker.SyncTenantAsync(tenantA, "6000000001", conn, CancellationToken.None);

        Assert.Equal("pushed", (await ReadRowAsync(fx, tenantA, id)).Status);

        // Unstamped session: FILTER predicate makes every row invisible.
        await using (var unstamped = await fx.OpenAsync(null))
        {
            var count = await unstamped.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.GoogleAdsPushedExclusions");
            Assert.Equal(0, count);
        }

        // A different tenant's stamped connection never sees tenant A's pushed state.
        var tenantB = await CreateTenantAsync(fx, "6000000002");
        await using (var asB = await fx.OpenAsync(tenantB))
        {
            var rows = await asB.QueryAsync(
                "SELECT * FROM dbo.GoogleAdsPushedExclusions WHERE ExclusionQueueId = @id", new { id });
            Assert.Empty(rows);
        }

        // BLOCK predicate: an unstamped INSERT into the protected table must fail.
        await using var unstamped2 = await fx.OpenAsync(null);
        await Assert.ThrowsAsync<SqlException>(() => unstamped2.ExecuteAsync(
            """
            INSERT INTO dbo.GoogleAdsPushedExclusions
                (TenantId, CriterionResourceName, GoogleCampaignId, SourceType, SourceValue, ExclusionQueueId, PushedUtc)
            VALUES (@tid, N'rn-forged', '1', 'ip', N'1.2.3.4', 1, SYSUTCDATETIME())
            """,
            new { tid = tenantA }));
    }
}
