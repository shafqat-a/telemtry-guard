namespace TelemetryGuard.Api.Options;

/// <summary>
/// Options for the beacon ingestion pair GET /i/init + POST /i (API-04).
/// Bound from the "Beacon" section of appsettings (layout owned by API-01).
/// Program.cs adds startup validation: HmacSecret must be non-empty and at
/// least 32 characters — an empty key would silently HMAC with nothing and
/// make storage_sig_ok meaningless. The dev secret ships in
/// appsettings.Development.json only; the base file deliberately leaves it blank.
/// </summary>
public sealed class BeaconOptions
{
    /// <summary>Key for the storage-timestamp HMAC (storageSig on /i/init,
    /// verification of the fp event's reported {ts, sig} pair on /i).</summary>
    public string HmacSecret { get; init; } = "";

    /// <summary>POST /i body cap; larger bodies get 413 (the only non-204 status).</summary>
    public int MaxBodyBytes { get; init; } = 65536;

    /// <summary>TTL of the per-session server nonce minted by /i/init.</summary>
    public int NonceTtlSeconds { get; init; } = 900;

    /// <summary>Max accepted |server receive ms − envelope sent_at|; beyond it
    /// skew_bad=1 and integrity_fails increments (recorded, never rejected).</summary>
    public long MaxClockSkewMs { get; init; } = 120000;

    /// <summary>TTL of the per-session aggregate hash t:{tid}:sess:{sid}.</summary>
    public int SessionTtlSeconds { get; init; } = 1800;

    /// <summary>Seconds of beacon inactivity before an SDK session is finalized.
    /// Each accepted, non-replayed batch pushes the deadline forward so scoring
    /// observes the complete browser session rather than its first page only.</summary>
    public int FinalizeQuietSeconds { get; init; } = 30;

    /// <summary>Sink cadence: a kind-beacon ClickEvent snapshot is emitted on the
    /// first beacon, on fp/fs-bearing batches, and on every Nth beacon.</summary>
    public int SinkEveryNthBeacon { get; init; } = 10;
}
