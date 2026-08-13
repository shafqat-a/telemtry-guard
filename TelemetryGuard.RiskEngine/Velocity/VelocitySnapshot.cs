namespace TelemetryGuard.RiskEngine.Velocity;

/// <summary>One batched read of all velocity features for scoring. All values are
/// legitimately 0 when cold — never NaN here (NaN mapping for StorageAgeZeroRepeat
/// happens in feature extraction when visitorId is absent).</summary>
public sealed record VelocitySnapshot(
    int IpClicksLastMin,
    long IpDistinctUasLastHour,
    long DeviceSessionsLastHour,
    long DeviceIdsThisIpHour,
    long StorageAgeZeroRepeat);
