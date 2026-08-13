namespace TelemetryGuard.Api.Services;

using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.RiskEngine.Pipeline;   // ScoringOutcome (RSK-07)

/// <summary>What triggered a verdict finalization: the grace-period worker's
/// deadline (API-06) or an immediate /decide response (API-05).</summary>
public enum FinalizeTrigger { GraceExpired, Decide }

/// <summary>
/// API-06's shared finalization seam. Declared here as a build-order shim
/// (API-05 depends on the interface before API-06 exists): whichever of
/// API-05/API-06 lands first creates this file with EXACTLY this contract;
/// the other consumes it as-is. API-06's <see cref="TelemetryGuard.Api.Services.VerdictFinalizer"/>
/// is the real implementation (verdict persistence, exclusion-queue writes,
/// EnforcementMode handling, summary MERGEs); it replaced API-05's build-order
/// stub without changing this interface.
/// </summary>
public interface IVerdictFinalizer
{
    /// <summary>Idempotently finalizes a session's verdict. precomputed avoids double scoring
    /// when the caller already ran the pipeline.</summary>
    Task FinalizeAsync(TenantId tenantId, string sessionId, FinalizeTrigger trigger,
                       ScoringOutcome? precomputed, CancellationToken ct);
}
