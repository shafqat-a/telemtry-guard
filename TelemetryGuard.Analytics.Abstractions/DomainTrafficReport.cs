namespace TelemetryGuard.Analytics.Abstractions;

public sealed record DomainTrafficSession(
    string SessionId,
    string PageUrl,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc,
    string Ip,
    string? UserAgent,
    string? Country,
    string? AsnOrganization,
    IReadOnlyList<string> HeaderNames,
    bool WebDriver,
    bool HeadlessBrowser,
    bool HoneypotTouched,
    int? Score,
    string? Band,
    string? Action,
    IReadOnlyList<string> RuleHits,
    string BotSource);

public sealed record DomainTrafficPage(
    string Host,
    long TotalSessions,
    long TotalEvents,
    long BotSessions,
    long Allowed,
    long Challenged,
    long Blocked,
    IReadOnlyList<DomainBotSource> BotSources,
    int Page,
    int PageSize,
    IReadOnlyList<DomainTrafficSession> Rows);

public sealed record DomainBotSource(string Source, long Total);
