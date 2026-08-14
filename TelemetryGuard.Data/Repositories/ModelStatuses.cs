namespace TelemetryGuard.Data.Repositories;

/// <summary>Mirrors dbo.ModelRegistry.Status (varchar(16), CK_MR_Status). String
/// constants, NOT an enum — Dapper parameters compare as strings, and these values are
/// the CLI's wire contract (TelemetryGuard.Training's promote/rollback commands).</summary>
public static class ModelStatuses
{
    /// <summary>Trained, passed every gate, NOT serving anywhere. Only a human promotes it.</summary>
    public const string Candidate = "candidate";
    /// <summary>Listen-only (D18): loaded and scored, logged to tg_events.shadow_*, enforcing NOTHING.</summary>
    public const string Shadow = "shadow";
    /// <summary>Enforcing (D18 promotion). Reached ONLY via `promote --status active`.</summary>
    public const string Active = "active";
    /// <summary>Terminal: failed the AUC / incumbent-comparison / latency gate. NEVER servable.</summary>
    public const string Rejected = "rejected";
    /// <summary>Formerly shadow/active, superseded or rolled back. Re-promotable.</summary>
    public const string Retired = "retired";

    public static readonly IReadOnlyList<string> All = [Candidate, Shadow, Active, Rejected, Retired];
}
