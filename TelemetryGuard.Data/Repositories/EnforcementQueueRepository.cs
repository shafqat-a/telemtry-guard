using Dapper;
using TelemetryGuard.Core.Tenancy;

namespace TelemetryGuard.Data.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IEnforcementQueueRepository"/> (INT-02).
/// Same pattern as the other DAT-05/06 repositories: connections come
/// exclusively from <see cref="ITenantConnectionFactory"/> (RLS-scoped), opened
/// via <c>OpenAsync</c> only; every statement additionally carries an explicit
/// TenantId = @TenantId predicate for index seeks (D11 — RLS FILTER/BLOCK is the
/// primary enforcement, this is a backstop).
///
/// ApproveAsync/RejectAsync each run as ONE transaction: the UPDATE ... OUTPUT
/// determines exactly which ids were actually pending (and therefore
/// transitioned), and one dbo.EnforcementAudit row is written per transitioned
/// id before the commit — a partial batch (some ids already non-pending) never
/// leaves an orphaned audit row for an id that did not transition.
/// </summary>
internal sealed class EnforcementQueueRepository(ITenantConnectionFactory connections, ITenantContext tenant)
    : IEnforcementQueueRepository
{
    private const byte ActionApprove = 0;
    private const byte ActionReject = 1;

    public async Task<IReadOnlyList<ExclusionQueueEntry>> ListAsync(
        string status, int limit, CancellationToken ct)
    {
        if (!ExclusionStatuses.All.Contains(status, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"status must be one of: {string.Join(", ", ExclusionStatuses.All)}.", nameof(status));
        }

        if (limit is not (> 0 and <= 500))
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "limit must be between 1 and 500.");
        }

        await using var conn = await connections.OpenAsync(ct);
        var rows = await conn.QueryAsync<ExclusionQueueEntry>(new CommandDefinition(
            """
            SELECT TOP (@Limit)
                Id, Platform, SourceType, Value, CampaignScope, Reason, Status, CreatedUtc, UpdatedUtc
            FROM dbo.ExclusionQueue
            WHERE TenantId = @TenantId AND Status = @Status
            ORDER BY CreatedUtc DESC, Id DESC;
            """,
            new { TenantId = tenant.TenantId.Value, Status = status, Limit = limit },
            cancellationToken: ct));
        return rows.AsList();
    }

    public Task<int> ApproveAsync(IReadOnlyList<long> ids, byte[]? actorKeyHash, CancellationToken ct)
        => TransitionAsync(
            ids, ExclusionStatuses.Pending, ExclusionStatuses.Approved,
            ActionApprove, note: null, actorKeyHash, ct);

    public Task<int> RejectAsync(IReadOnlyList<long> ids, string? note, byte[]? actorKeyHash, CancellationToken ct)
    {
        if (note is { Length: > 400 })
        {
            throw new ArgumentException("note must be at most 400 characters.", nameof(note));
        }

        return TransitionAsync(
            ids, ExclusionStatuses.Pending, ExclusionStatuses.Rejected,
            ActionReject, note, actorKeyHash, ct);
    }

    private async Task<int> TransitionAsync(
        IReadOnlyList<long> ids, string fromStatus, string toStatus,
        byte action, string? note, byte[]? actorKeyHash, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return 0; // avoid an empty `IN ()` and a pointless round trip.
        }

        var tenantId = tenant.TenantId.Value;

        await using var conn = await connections.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // OUTPUT tells us exactly which ids were pending (and therefore actually
        // transitioned) — duplicates in `ids` collapse to the same row via SQL IN
        // semantics, so a deliberately duplicated id never produces two audit rows.
        var transitioned = (await conn.QueryAsync<long>(new CommandDefinition(
            """
            UPDATE dbo.ExclusionQueue
            SET Status = @ToStatus, UpdatedUtc = SYSUTCDATETIME()
            OUTPUT inserted.Id
            WHERE TenantId = @TenantId AND Id IN @Ids AND Status = @FromStatus;
            """,
            new { TenantId = tenantId, Ids = ids, FromStatus = fromStatus, ToStatus = toStatus },
            transaction: tx,
            cancellationToken: ct))).AsList();

        foreach (var id in transitioned)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO dbo.EnforcementAudit
                    (TenantId, AuditId, ExclusionQueueId, Action, ActorKeyHash, Note)
                VALUES (@TenantId, @AuditId, @ExclusionQueueId, @Action, @ActorKeyHash, @Note);
                """,
                new
                {
                    TenantId = tenantId,
                    AuditId = Guid.NewGuid(),
                    ExclusionQueueId = id,
                    Action = action,
                    ActorKeyHash = actorKeyHash,
                    Note = note,
                },
                transaction: tx,
                cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return transitioned.Count;
    }
}
