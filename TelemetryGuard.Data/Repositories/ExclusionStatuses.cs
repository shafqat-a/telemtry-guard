namespace TelemetryGuard.Data.Repositories;

/// <summary>
/// Mirrors dbo.ExclusionQueue.Status (varchar(16), DAT-06's CHECK constraint,
/// which already includes 'rejected' — see 0003_summaries_exclusions.sql). String
/// constants, NOT an enum: Dapper parameters must compare as strings against the
/// varchar column, and the values themselves are the wire contract (spec D21).
///
/// SEAM: this is the canonical namespace for exclusion-queue status literals.
/// INT-03 (Google sync) and INT-04 (Meta sync) import these constants instead of
/// re-declaring their own — do not fork a second status type.
/// </summary>
public static class ExclusionStatuses
{
    /// <summary>ApprovalQueue tenants: awaiting tenant approval (this task, INT-02).</summary>
    public const string Pending = "pending";

    /// <summary>Eligible for platform push (INT-03/INT-04 sync workers).</summary>
    public const string Approved = "approved";

    /// <summary>Terminal: tenant declined via the approval queue (this task, INT-02).</summary>
    public const string Rejected = "rejected";

    /// <summary>Terminal: platform accepted the exclusion (sync workers).</summary>
    public const string Pushed = "pushed";

    /// <summary>Push errored; LastError (INT-03/INT-04) holds the detail.</summary>
    public const string Failed = "failed";

    /// <summary>Terminal: the target platform cannot enforce this source type
    /// (e.g. INT-04 Meta 'ip' rows).</summary>
    public const string Unsupported = "unsupported";

    /// <summary>All six values, in CHECK-constraint declaration order.</summary>
    public static readonly IReadOnlyList<string> All =
        [Pending, Approved, Rejected, Pushed, Failed, Unsupported];
}
