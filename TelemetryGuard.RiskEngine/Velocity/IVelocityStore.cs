namespace TelemetryGuard.RiskEngine.Velocity;

/// <summary>Thin testability seam over Redis (D5 — no provider abstraction beyond this).
/// All keys are tenant-prefixed t:{tenantId}: from the ambient ITenantContext.</summary>
public interface IVelocityStore
{
    /// <summary>Capture for a tracker click (/c). Single IBatch round trip:
    /// INCR minute bucket, PFADD ip→ua HLL, SET NX click id.
    /// Returns: true = clickId seen first time; false = replay; null = clickId was null.</summary>
    Task<bool?> RecordClickAsync(string ip, string? userAgent, string? clickId, CancellationToken ct);

    /// <summary>Capture for a beacon/session observation (/i). Single IBatch round trip:
    /// PFADD device→session HLL, PFADD ip→device HLL, PFADD ip→ua HLL,
    /// and INCR fpz:{visitorId} when storageAgeZero is true.</summary>
    Task RecordSessionAsync(string ip, string? userAgent, string? visitorId, string sessionId,
                            bool storageAgeZero, CancellationToken ct);

    /// <summary>Batched read of every velocity feature for scoring — single round trip.
    /// visitorId null → device-keyed values return 0 (extraction maps to NaN where required).</summary>
    Task<VelocitySnapshot> ReadAsync(string ip, string? visitorId, CancellationToken ct);
}
