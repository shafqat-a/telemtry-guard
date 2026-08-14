using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Analytics.ClickHouse;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Core.Time;

namespace TelemetryGuard.Tests.Integration;

/// <summary>
/// Reusable seed-event builders and a drain-on-return writer for analytics
/// query tests (ANA-04). Kept internal and scenario-agnostic so the ANA-06
/// cross-provider behavior suite can lift them unchanged.
/// </summary>
internal static class TestEvents
{
    internal static ClickEvent Tracker(
        TenantId tenant,
        string ip,
        DateTime timestampUtc,
        string sessionId,
        string campaignId = "",
        string? userAgent = null,
        string? fingerprint = null,
        string siteKey = "sk-test",
        string? referrer = null) => new()
    {
        TenantId = tenant,
        SiteKey = siteKey,
        SessionId = sessionId,
        Kind = EventKind.Tracker,
        CampaignId = campaignId,
        Ip = ip,
        UserAgent = userAgent,
        FingerprintVisitorId = fingerprint,
        HasJsBeacon = false,
        Referrer = referrer, // P2-01: publisher-attribution source column
        RetentionDays = 90,
        TimestampUtc = timestampUtc
    };

    internal static ClickEvent Verdict(
        TenantId tenant,
        string ip,
        DateTime timestampUtc,
        string sessionId,
        int score,
        string band,
        string campaignId = "",
        string? userAgent = null,
        string? fingerprint = null,
        bool hasJsBeacon = true,
        string siteKey = "sk-test") => new()
    {
        TenantId = tenant,
        SiteKey = siteKey,
        SessionId = sessionId,
        Kind = EventKind.Verdict,
        CampaignId = campaignId,
        Ip = ip,
        UserAgent = userAgent,
        FingerprintVisitorId = fingerprint,
        HasJsBeacon = hasJsBeacon,
        Score = score,
        Band = band,
        Action = band,
        ScorerVersion = "heuristic-1",
        FeatureSetVersion = 1,
        RetentionDays = 90,
        TimestampUtc = timestampUtc
    };

    /// <summary>
    /// Writes the events through the real ANA-03 sink and drains it before
    /// returning, so every row is queryable immediately after this call.
    /// </summary>
    internal static async Task SeedAsync(string connectionString, IReadOnlyList<ClickEvent> events)
    {
        var sink = new ClickHouseEventSink(
            Options.Create(new ClickHouseAnalyticsOptions
            {
                ConnectionString = connectionString,
                EventMaxBatchSize = 5_000,
                EventMaxBatchAgeSeconds = 30 // deterministic visibility comes from the StopAsync drain
            }),
            NullLogger<ClickHouseEventSink>.Instance);
        await sink.StartAsync(CancellationToken.None);
        await sink.WriteBatchAsync(events.ToArray(), CancellationToken.None);
        await sink.StopAsync(CancellationToken.None);
    }
}

/// <summary>Trivial resolved-tenant stub for constructing scoped query classes in tests.</summary>
internal sealed class FixedTenantContext(TenantId tenantId) : ITenantContext
{
    public TenantId TenantId { get; } = tenantId;
    public string? SiteKey => null;
    public bool IsResolved => true;
}

/// <summary>Trivial fixed-time stub for deterministic trailing windows in tests.</summary>
internal sealed class FixedClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; } = utcNow;
}
