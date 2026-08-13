using TelemetryGuard.RiskEngine.Features;

namespace TelemetryGuard.RiskEngine.Pipeline;

/// <summary>HTTP-layer facts captured at click time, parsed from the flat hash
/// t:{tid}:click:{sid} written by API-02/API-03 (fields: kind, ts, ip, ua, ch_ua,
/// ch_mobile, ch_platform, accept_language, referrer, header_order, site_key,
/// campaign_id, click_id_type, click_id, click_id_invalid; empty string = absent).</summary>
public sealed record ClickState(
    string Ip, string? UserAgent, IReadOnlyDictionary<string, string> Headers,
    string? ClickIdType, string? ClickId, bool? ClickIdFresh, bool IsPaidClick,
    string? TlsFingerprint, DateTimeOffset Timestamp);

/// <summary>Joined per-session read model. Beacon is RSK-04's aggregate-carrying
/// BeaconData mapped from API-04's session hash; null when no beacon arrived.</summary>
public sealed record SessionState(ClickState? Click, BeaconData? Beacon, string? CampaignId);

/// <summary>READ-ONLY view over the ingest-written Redis session state. The producers
/// own the keys, formats and TTLs: API-02/API-03 write the flat click-context hash
/// t:{tid}:click:{sid} and API-04 writes the flat aggregate session hash
/// t:{tid}:sess:{sid}. There is deliberately no write path here — the ingest endpoints
/// write their own contracts, and API-05 passes the Turnstile outcome as a
/// ScoreSessionAsync parameter.</summary>
public interface ISessionStateStore
{
    /// <summary>Batched read of both hashes; null when neither exists (unknown session).</summary>
    Task<SessionState?> GetAsync(string sessionId, CancellationToken ct);
}
