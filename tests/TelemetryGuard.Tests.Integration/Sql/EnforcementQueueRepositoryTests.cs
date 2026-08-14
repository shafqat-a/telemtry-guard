using Dapper;
using Microsoft.Extensions.DependencyInjection;
using TelemetryGuard.Data.Repositories;

namespace TelemetryGuard.Tests.Integration.Sql;

/// <summary>
/// INT-02 IEnforcementQueueRepository against real migrated SQL Server (DAT-08
/// harness pattern): dbo.ExclusionQueue rows are seeded through the existing
/// writer (API-06's IExclusionQueueRepository.EnqueueAsync, status 'pending' or
/// 'approved' — the only two a writer may insert) to mirror how real rows are
/// born under each EnforcementMode, then this suite exercises every
/// IEnforcementQueueRepository method: ListAsync per status, ApproveAsync
/// (mixed batch + a deliberately duplicated id proving atomicity/no double
/// audit row), RejectAsync with a note, idempotent replay, and the cross-tenant
/// RLS proof.
/// </summary>
[Collection("sqlserver")]
[Trait("Category", "Integration")]
public sealed class EnforcementQueueRepositoryTests(SqlServerFixture fx)
{
    private static async Task<long> SeedAsync(
        IExclusionQueueRepository exclusions, SqlServerFixture fx, Guid tenantId,
        string status, string? value = null)
    {
        var ip = value ?? $"203.0.113.{Random.Shared.Next(1, 254)}-{Guid.NewGuid():N}";
        await exclusions.EnqueueAsync(
            new ExclusionQueueInsert("google", "ip", ip, "score=90 rules=honeypot_touched", status, null),
            CancellationToken.None);

        await using var conn = await fx.OpenAsync(tenantId);
        return await conn.ExecuteScalarAsync<long>(
            "SELECT Id FROM dbo.ExclusionQueue WHERE TenantId = @TenantId AND Value = @Value",
            new { TenantId = tenantId, Value = ip });
    }

    // ================================================================ list =

    [Fact]
    public async Task ListAsync_filtersByStatus_newestFirst()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var exclusions = scope.ServiceProvider.GetRequiredService<IExclusionQueueRepository>();
        var enforcement = scope.ServiceProvider.GetRequiredService<IEnforcementQueueRepository>();

        var id1 = await SeedAsync(exclusions, fx, SqlServerFixture.TenantA, "pending");
        await Task.Delay(10); // CreatedUtc ordering
        var id2 = await SeedAsync(exclusions, fx, SqlServerFixture.TenantA, "pending");

        var page = await enforcement.ListAsync(ExclusionStatuses.Pending, 500, CancellationToken.None);

        var ids = page.Select(e => e.Id).ToList();
        Assert.Contains(id1, ids);
        Assert.Contains(id2, ids);
        Assert.True(ids.IndexOf(id2) < ids.IndexOf(id1)); // newest first
        var entry = page.First(e => e.Id == id2);
        Assert.Equal("google", entry.Platform);
        Assert.Equal("ip", entry.SourceType);
        Assert.Equal(ExclusionStatuses.Pending, entry.Status);
        Assert.Null(entry.UpdatedUtc);
    }

    [Fact] // simulates the AutoEnforce mode's born-'approved' rows (API-06 owns mode
           // selection; this proves the repository-level list/filter contract those
           // rows depend on).
    public async Task ListAsync_autoEnforceStyleApprovedRow_neverAppearsUnderPending()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var exclusions = scope.ServiceProvider.GetRequiredService<IExclusionQueueRepository>();
        var enforcement = scope.ServiceProvider.GetRequiredService<IEnforcementQueueRepository>();

        var value = $"203.0.113.{Random.Shared.Next(1, 254)}-{Guid.NewGuid():N}";
        var id = await SeedAsync(exclusions, fx, SqlServerFixture.TenantA, "approved", value);

        var pending = await enforcement.ListAsync(ExclusionStatuses.Pending, 500, CancellationToken.None);
        Assert.DoesNotContain(pending, e => e.Id == id);

        var approved = await enforcement.ListAsync(ExclusionStatuses.Approved, 500, CancellationToken.None);
        Assert.Contains(approved, e => e.Id == id);
    }

    // ============================================================= approve =

    [Fact]
    public async Task ApproveAsync_mixedBatch_transitionsOnlyPending_writesOneAuditRowEach_andSkipsAlreadyApproved()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var exclusions = scope.ServiceProvider.GetRequiredService<IExclusionQueueRepository>();
        var enforcement = scope.ServiceProvider.GetRequiredService<IEnforcementQueueRepository>();

        var pendingId1 = await SeedAsync(exclusions, fx, SqlServerFixture.TenantA, "pending");
        var pendingId2 = await SeedAsync(exclusions, fx, SqlServerFixture.TenantA, "pending");
        var alreadyApprovedId = await SeedAsync(exclusions, fx, SqlServerFixture.TenantA, "approved");

        var actorHash = new byte[32];
        Random.Shared.NextBytes(actorHash);

        // pendingId1 deliberately duplicated -> proves atomicity: one row, one audit row.
        var approved = await enforcement.ApproveAsync(
            [pendingId1, pendingId1, pendingId2, alreadyApprovedId], actorHash, CancellationToken.None);

        Assert.Equal(2, approved); // only the two genuinely-pending ids transitioned

        await using var conn = await fx.OpenAsync(SqlServerFixture.TenantA);
        var statuses = await conn.QueryAsync<(long Id, string Status, DateTime? UpdatedUtc)>(
            "SELECT Id, Status, UpdatedUtc FROM dbo.ExclusionQueue WHERE TenantId = @TenantId AND Id IN @Ids",
            new { TenantId = SqlServerFixture.TenantA, Ids = new[] { pendingId1, pendingId2, alreadyApprovedId } });
        var byId = statuses.ToDictionary(s => s.Id);
        Assert.Equal("approved", byId[pendingId1].Status);
        Assert.NotNull(byId[pendingId1].UpdatedUtc);
        Assert.Equal("approved", byId[pendingId2].Status);
        Assert.Equal("approved", byId[alreadyApprovedId].Status); // untouched, was already approved

        var auditRows = (await conn.QueryAsync<(long ExclusionQueueId, byte Action, byte[] ActorKeyHash)>(
            "SELECT ExclusionQueueId, Action, ActorKeyHash FROM dbo.EnforcementAudit " +
            "WHERE TenantId = @TenantId AND ExclusionQueueId IN @Ids",
            new { TenantId = SqlServerFixture.TenantA, Ids = new[] { pendingId1, pendingId2, alreadyApprovedId } }))
            .ToList();

        Assert.Equal(2, auditRows.Count); // exactly one audit row per transitioned id — the
                                           // duplicate did NOT produce a second row.
        Assert.All(auditRows, r => Assert.Equal(0, r.Action));
        Assert.All(auditRows, r => Assert.Equal(actorHash, r.ActorKeyHash));
        Assert.DoesNotContain(auditRows, r => r.ExclusionQueueId == alreadyApprovedId);
    }

    [Fact]
    public async Task ApproveAsync_replayingTheSameRequest_transitionsNothing_andWritesNoNewAuditRows()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var exclusions = scope.ServiceProvider.GetRequiredService<IExclusionQueueRepository>();
        var enforcement = scope.ServiceProvider.GetRequiredService<IEnforcementQueueRepository>();

        var pendingId = await SeedAsync(exclusions, fx, SqlServerFixture.TenantA, "pending");

        var first = await enforcement.ApproveAsync([pendingId], null, CancellationToken.None);
        Assert.Equal(1, first);

        var second = await enforcement.ApproveAsync([pendingId], null, CancellationToken.None);
        Assert.Equal(0, second);

        await using var conn = await fx.OpenAsync(SqlServerFixture.TenantA);
        var auditCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.EnforcementAudit WHERE TenantId = @TenantId AND ExclusionQueueId = @Id",
            new { TenantId = SqlServerFixture.TenantA, Id = pendingId });
        Assert.Equal(1, auditCount);
    }

    [Fact]
    public async Task ApproveAsync_nullActorKeyHash_recordsSystemAction()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var exclusions = scope.ServiceProvider.GetRequiredService<IExclusionQueueRepository>();
        var enforcement = scope.ServiceProvider.GetRequiredService<IEnforcementQueueRepository>();

        var pendingId = await SeedAsync(exclusions, fx, SqlServerFixture.TenantA, "pending");
        await enforcement.ApproveAsync([pendingId], actorKeyHash: null, CancellationToken.None);

        await using var conn = await fx.OpenAsync(SqlServerFixture.TenantA);
        var actorHash = await conn.ExecuteScalarAsync<byte[]?>(
            "SELECT ActorKeyHash FROM dbo.EnforcementAudit WHERE TenantId = @TenantId AND ExclusionQueueId = @Id",
            new { TenantId = SqlServerFixture.TenantA, Id = pendingId });
        Assert.Null(actorHash);
    }

    // ============================================================== reject =

    [Fact]
    public async Task RejectAsync_pendingRow_transitionsToRejected_andAuditRowCarriesTheNote()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var exclusions = scope.ServiceProvider.GetRequiredService<IExclusionQueueRepository>();
        var enforcement = scope.ServiceProvider.GetRequiredService<IEnforcementQueueRepository>();

        var pendingId = await SeedAsync(exclusions, fx, SqlServerFixture.TenantA, "pending");
        var actorHash = new byte[32];
        Random.Shared.NextBytes(actorHash);

        var rejected = await enforcement.RejectAsync(
            [pendingId], "false positive - confirmed real customer", actorHash, CancellationToken.None);
        Assert.Equal(1, rejected);

        await using var conn = await fx.OpenAsync(SqlServerFixture.TenantA);
        var status = await conn.ExecuteScalarAsync<string>(
            "SELECT Status FROM dbo.ExclusionQueue WHERE TenantId = @TenantId AND Id = @Id",
            new { TenantId = SqlServerFixture.TenantA, Id = pendingId });
        Assert.Equal("rejected", status);

        var audit = await conn.QuerySingleAsync<(byte Action, string Note)>(
            "SELECT Action, Note FROM dbo.EnforcementAudit WHERE TenantId = @TenantId AND ExclusionQueueId = @Id",
            new { TenantId = SqlServerFixture.TenantA, Id = pendingId });
        Assert.Equal(1, audit.Action);
        Assert.Equal("false positive - confirmed real customer", audit.Note);
    }

    // ------------------------------------------------------------- RLS proof --

    [Fact]
    public async Task CrossTenant_TenantBConnection_SeesNoneOfTenantAsEntries_ViaListOrTransition()
    {
        await using var providerA = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scopeA = providerA.CreateScope();
        var exclusionsA = scopeA.ServiceProvider.GetRequiredService<IExclusionQueueRepository>();

        var pendingIdA = await SeedAsync(exclusionsA, fx, SqlServerFixture.TenantA, "pending");

        await using var providerB = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantB);
        using var scopeB = providerB.CreateScope();
        var enforcementB = scopeB.ServiceProvider.GetRequiredService<IEnforcementQueueRepository>();

        var pendingForB = await enforcementB.ListAsync(ExclusionStatuses.Pending, 500, CancellationToken.None);
        Assert.DoesNotContain(pendingForB, e => e.Id == pendingIdA);

        // RLS also blocks the transition itself (tenant B's stamped connection can
        // never see tenant A's row to update it) — not just the list read.
        var approvedFromB = await enforcementB.ApproveAsync([pendingIdA], null, CancellationToken.None);
        Assert.Equal(0, approvedFromB);

        await using var connA = await fx.OpenAsync(SqlServerFixture.TenantA);
        var statusStillPending = await connA.ExecuteScalarAsync<string>(
            "SELECT Status FROM dbo.ExclusionQueue WHERE TenantId = @TenantId AND Id = @Id",
            new { TenantId = SqlServerFixture.TenantA, Id = pendingIdA });
        Assert.Equal("pending", statusStillPending);
    }

    [Fact] // RLS proof, extends DAT-08's harness (unstamped session sees zero rows)
    public async Task UnstampedSession_SeesZeroEnforcementAuditRows()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var exclusions = scope.ServiceProvider.GetRequiredService<IExclusionQueueRepository>();
        var enforcement = scope.ServiceProvider.GetRequiredService<IEnforcementQueueRepository>();

        var pendingId = await SeedAsync(exclusions, fx, SqlServerFixture.TenantA, "pending");
        await enforcement.ApproveAsync([pendingId], null, CancellationToken.None);

        await using var unstamped = await fx.OpenAsync(null);
        var count = await unstamped.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.EnforcementAudit;");
        Assert.Equal(0, count);
    }

    // -------------------------------------------------------------- errors --

    [Fact]
    public async Task ListAsync_invalidStatus_throwsArgumentException()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var enforcement = scope.ServiceProvider.GetRequiredService<IEnforcementQueueRepository>();

        await Assert.ThrowsAsync<ArgumentException>(
            () => enforcement.ListAsync("bogus", 10, CancellationToken.None));
    }

    [Fact]
    public async Task ApproveAsync_emptyIds_returnsZero_withoutTouchingTheDatabase()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var enforcement = scope.ServiceProvider.GetRequiredService<IEnforcementQueueRepository>();

        var result = await enforcement.ApproveAsync([], null, CancellationToken.None);
        Assert.Equal(0, result);
    }
}
