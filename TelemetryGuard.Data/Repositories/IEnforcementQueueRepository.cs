namespace TelemetryGuard.Data.Repositories;

/// <summary>
/// One row read back from dbo.ExclusionQueue for the INT-02 admin approval-queue
/// endpoints. Carries Platform (NOT NULL on the table — 'google' | 'meta' |
/// 'tiktok' | 'other') so the list endpoint lets a tenant/operator distinguish,
/// e.g., Meta 'unsupported' rows (INT-04) from Google ones at a glance.
/// </summary>
public sealed record ExclusionQueueEntry(
    long Id, string Platform, string SourceType, string Value,
    Guid? CampaignScope, string Reason, string Status,
    DateTime CreatedUtc, DateTime? UpdatedUtc);

/// <summary>
/// Read + transition path for dbo.ExclusionQueue's approval-queue lifecycle
/// (D21/D19): pending -&gt; approved | rejected. API-06's <see cref="IExclusionQueueRepository"/>
/// remains the sole writer of new rows; this repository never inserts a row, only
/// lists and transitions existing ones, and every transition writes one
/// dbo.EnforcementAudit row (INT-02).
/// </summary>
public interface IEnforcementQueueRepository
{
    /// <summary>Entries for the current tenant filtered by status (an
    /// <see cref="ExclusionStatuses"/> value), newest first.</summary>
    Task<IReadOnlyList<ExclusionQueueEntry>> ListAsync(
        string status, int limit, CancellationToken ct);

    /// <summary>Transitions pending-&gt;approved for the given queue Ids and writes one
    /// audit row per transitioned entry, in ONE transaction. Ids not currently
    /// pending (already approved/rejected/etc., or belonging to another tenant —
    /// RLS makes those invisible) are silently skipped, which makes replaying the
    /// same request safe. Returns the number actually transitioned.</summary>
    Task<int> ApproveAsync(IReadOnlyList<long> ids, byte[]? actorKeyHash, CancellationToken ct);

    /// <summary>Transitions pending-&gt;rejected; same batching/audit/idempotency
    /// contract as <see cref="ApproveAsync"/>. <paramref name="note"/> (≤400 chars)
    /// is stored on every audit row written by this call.</summary>
    Task<int> RejectAsync(IReadOnlyList<long> ids, string? note, byte[]? actorKeyHash, CancellationToken ct);
}
