using Dapper;
using TelemetryGuard.Core.Tenancy;

namespace TelemetryGuard.Data.Repositories;

/// <summary>
/// One row queued for exclusion-list sync (D21). DAT-06 ships the
/// dbo.ExclusionQueue table only — this task (API-06, the writer) owns the
/// insert path; INT-02 (approval flow) and INT-03/INT-04 (per-platform sync)
/// own the later status transitions and are not part of this contract.
/// </summary>
public sealed record ExclusionQueueInsert(
    string Platform,    // "google" | "meta" | "tiktok" | "other" — routes the row to its sync worker (INT-03/INT-04)
    string SourceType,  // "ip" | "placement"
    string Value,       // the IP or placement id
    string Reason,      // e.g. "score=87 rules=ip_datacenter_asn|honeypot_touched"
    string Status,      // "pending" | "approved" — the only two statuses a WRITER may insert (D21)
    Guid? CampaignScope); // null = tenant-wide

/// <summary>
/// Write path into dbo.ExclusionQueue. API-06's verdict finalizer is the only
/// caller at MVP: it enqueues a row for every block-band verdict, with Status
/// derived from the tenant's EnforcementMode (D21) — 'approved' immediately
/// actionable under AutoEnforce, 'pending' until INT-02's approval flow moves it
/// under ApprovalQueue.
/// </summary>
public interface IExclusionQueueRepository
{
    Task EnqueueAsync(ExclusionQueueInsert entry, CancellationToken ct);
}

/// <summary>
/// Dapper writer for dbo.ExclusionQueue. Same pattern as the other DAT-05/06
/// repositories: connections come exclusively from <see cref="ITenantConnectionFactory"/>
/// (RLS-scoped), opened via <c>OpenAsync</c> only; every statement additionally
/// carries an explicit TenantId (D11 — RLS FILTER/BLOCK is the primary
/// enforcement, this is a backstop). C# guards the CHECK-constrained columns
/// before SQL so a bad value fails fast with a clear message instead of an
/// opaque SqlException.
/// </summary>
internal sealed class ExclusionQueueRepository(ITenantConnectionFactory connections, ITenantContext tenant)
    : IExclusionQueueRepository
{
    public async Task EnqueueAsync(ExclusionQueueInsert entry, CancellationToken ct)
    {
        if (entry.Platform is not ("google" or "meta" or "tiktok" or "other"))
        {
            throw new ArgumentException(
                "Platform must be one of: google, meta, tiktok, other.", nameof(entry));
        }

        if (entry.SourceType is not ("ip" or "placement"))
        {
            throw new ArgumentException(
                "SourceType must be one of: ip, placement.", nameof(entry));
        }

        if (entry.Status is not ("pending" or "approved"))
        {
            throw new ArgumentException(
                "Status must be one of: pending, approved (writer-side; INT-02/03/04 own later transitions).",
                nameof(entry));
        }

        await using var conn = await connections.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO dbo.ExclusionQueue (TenantId, Platform, SourceType, Value, Reason, Status, CampaignScope)
            VALUES (@TenantId, @Platform, @SourceType, @Value, @Reason, @Status, @CampaignScope);
            """,
            new
            {
                TenantId = tenant.TenantId.Value,
                entry.Platform,
                entry.SourceType,
                entry.Value,
                entry.Reason,
                entry.Status,
                entry.CampaignScope,
            },
            cancellationToken: ct));
    }
}
