using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Core.Tenancy;

namespace TelemetryGuard.Tests.Contracts;

/// <summary>
/// Provider-agnostic seed-event builder for the contract suite. Produces a
/// valid <see cref="ClickEvent"/> with only the identity fields filled in, so
/// every SDK numeric stays at its NaN default and every optional flag stays
/// null (spec §7: missing != zero). <c>Score</c> is never defaulted — it is
/// null unless a test explicitly seeds a verdict.
/// </summary>
public static class TestEvents
{
    public static ClickEvent Create(
        TenantId tenantId,
        string ip,
        string sessionId,
        string campaignId = "",
        EventKind kind = EventKind.Tracker,
        string? band = null,
        int? score = null,
        DateTime? timestampUtc = null,
        string? userAgent = null,
        string? fingerprint = null,
        bool hasJsBeacon = false,
        string siteKey = "site-1",
        string? referrer = null) => new()
    {
        TenantId = tenantId,
        SiteKey = siteKey,
        SessionId = sessionId,
        Kind = kind,
        CampaignId = campaignId,
        Ip = ip,
        UserAgent = userAgent,
        FingerprintVisitorId = fingerprint,
        HasJsBeacon = hasJsBeacon,
        Score = score,      // stays null on non-verdict rows — never 0
        Band = band,
        Action = band,
        Referrer = referrer, // P2-01: only tracker/pixel rows carry one in real traffic
        RetentionDays = 90,
        TimestampUtc = timestampUtc ?? DateTime.UtcNow
    };

    /// <summary>Convenience wrapper for a scored verdict row.</summary>
    public static ClickEvent Verdict(
        TenantId tenantId,
        string ip,
        string sessionId,
        int score,
        string band,
        string campaignId = "",
        DateTime? timestampUtc = null,
        string? userAgent = null,
        string? fingerprint = null,
        bool hasJsBeacon = false,
        string siteKey = "site-1",
        string? referrer = null) =>
        Create(tenantId, ip, sessionId, campaignId, EventKind.Verdict, band, score,
            timestampUtc, userAgent, fingerprint, hasJsBeacon, siteKey, referrer);
}
