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

/// <summary>Host seam for site/tenant management. MarketIQ resolves this from tgm_Sites.</summary>
public interface ITelemetryGuardSiteResolver
{
    Task<TelemetryGuardSiteOptions?> ResolveAsync(string siteKey,
        CancellationToken ct = default);
}

public readonly record struct TelemetryGuardScore(
    int Score,
    string Band,
    IReadOnlyList<string> RuleHits,
    string FeatureVersion = "tg-native-1");

public interface ITelemetryGuardClientScorer
{
    TelemetryGuardScore Score(TelemetryGuardVisitState state);
}

/// <summary>Behavior accumulated for one document/page load.</summary>
public sealed record TelemetryGuardVisitState
{
    public long FirstSeenUnixMs { get; init; }
    public long LastSeenUnixMs { get; init; }
    public long MouseEvents { get; init; }
    public long TouchEvents { get; init; }
    public long ScrollEvents { get; init; }
    public long Keystrokes { get; init; }
    public bool WebDriver { get; init; }
    public bool Headless { get; init; }
    public bool HoneypotFieldFilled { get; init; }
    public bool HoneypotLinkClicked { get; init; }
    public bool HoneyIdentifierSeen { get; init; }
    public bool DecoyPage { get; init; }
    public bool FormSubmitted { get; init; }
    public bool PasteInIdentityField { get; init; }
    public bool VerifiedConversion { get; init; }
    public long? PageToConversionMs { get; init; }
    public IReadOnlyList<string> ConversionGoalIds { get; init; } = Array.Empty<string>();
    public bool IntegrityFailed { get; init; }
}

/// <summary>Cross-page counters for one browser session.</summary>
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

public sealed record TelemetryGuardAggregateState(
    TelemetryGuardVisitState Visit,
    TelemetryGuardSessionState Session);

public interface ITelemetryGuardSessionStore
{
    Task<TelemetryGuardAggregateState> UpdateAsync(
        Guid tenantId, string sessionId, string visitId, TelemetryGuardObservation observation,
        TimeSpan ttl, CancellationToken ct = default);
    Task StoreNonceAsync(Guid tenantId, string visitId, string nonce, TimeSpan ttl,
        CancellationToken ct = default);
    Task<bool> ValidateNonceAsync(Guid tenantId, string visitId, string nonce,
        CancellationToken ct = default);
}

/// <summary>
/// Debounces the SDK's many beacon envelopes into one MarketIQ event per page visit.
/// Implementations must be shared across replicas and give claimed rows a visibility lease.
/// </summary>
public interface ITelemetryGuardVisitQueue
{
    Task ScheduleAsync(TelemetryGuardSubmission submission, DateTimeOffset due,
        TimeSpan ttl, CancellationToken ct = default);
    Task<IReadOnlyList<TelemetryGuardPendingVisit>> ClaimDueAsync(
        int max, TimeSpan lease, CancellationToken ct = default);
    Task CompleteAsync(TelemetryGuardPendingVisit visit, CancellationToken ct = default);
    Task RetryAsync(TelemetryGuardPendingVisit visit, DateTimeOffset due,
        CancellationToken ct = default);
}

public sealed record TelemetryGuardPendingVisit(string Token, TelemetryGuardSubmission Submission);

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
    bool HoneyIdentifierSeen,
    bool DecoyPage,
    bool FormSubmitted,
    bool PasteInIdentityField,
    bool VerifiedConversion,
    long? PageToConversionMs,
    string? ConversionGoalId,
    bool IntegrityFailed);
