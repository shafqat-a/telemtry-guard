namespace TelemetryGuard.Client;

/// <summary>A TG-scored event ready for the owning platform's durable ingest pipeline.</summary>
public readonly record struct TelemetryGuardSubmission(
    string Body,
    string PartitionKey,
    int CompanyId,
    Guid TenantId,
    string SiteKey,
    string EventId);

/// <summary>
/// Implemented by the host. The package deliberately knows nothing about Event Hubs, ADX,
/// SQL, or enforcement and therefore remains safe to embed in more than one product.
/// </summary>
public interface ITelemetryGuardRelay
{
    Task RelayAsync(TelemetryGuardSubmission submission, CancellationToken ct = default);
}

public readonly record struct TelemetryGuardScore(
    int Score,
    string Band,
    IReadOnlyList<string> RuleHits,
    string FeatureVersion = "tg-native-1");

public interface ITelemetryGuardClientScorer
{
    TelemetryGuardScore Score(TelemetryGuardSessionState state);
}

public sealed record TelemetryGuardSessionState
{
    public long FirstSeenUnixMs { get; init; }
    public long LastSeenUnixMs { get; init; }
    public long MouseEvents { get; init; }
    public long TouchEvents { get; init; }
    public long ScrollEvents { get; init; }
    public long Keystrokes { get; init; }
    public long PagesViewed { get; init; }
    public bool WebDriver { get; init; }
    public bool Headless { get; init; }
    public bool HoneypotFieldFilled { get; init; }
    public bool HoneypotLinkClicked { get; init; }
    public bool FormSubmitted { get; init; }
    public bool PasteInIdentityField { get; init; }
}

public interface ITelemetryGuardSessionStore
{
    Task<TelemetryGuardSessionState> UpdateAsync(
        Guid tenantId, string sessionId, TelemetryGuardObservation observation,
        TimeSpan ttl, CancellationToken ct = default);
    Task StoreNonceAsync(Guid tenantId, string sessionId, string nonce, TimeSpan ttl,
        CancellationToken ct = default);
    Task<bool> ValidateNonceAsync(Guid tenantId, string sessionId, string nonce,
        CancellationToken ct = default);
}

public sealed record TelemetryGuardObservation(
    long SeenUnixMs,
    long MouseEvents,
    long TouchEvents,
    long ScrollEvents,
    long Keystrokes,
    bool WebDriver,
    bool Headless,
    bool HoneypotFieldFilled,
    bool HoneypotLinkClicked,
    bool FormSubmitted,
    bool PasteInIdentityField);
