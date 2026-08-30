namespace TelemetryGuard.Client;

/// <summary>Host-owned configuration for the embeddable, collection-only TG surface.</summary>
public sealed class TelemetryGuardClientOptions
{
    public const string SectionName = "TelemetryGuard";

    public bool Enabled { get; set; }
    public string PathBase { get; set; } = "/tg";
    public int SessionTtlMinutes { get; set; } = 30;
    public int FinalizeQuietSeconds { get; set; } = 10;
    /// <summary>HMAC key used to sign the SDK's first-party storage timestamp.</summary>
    public string IntegrityHmacSecret { get; set; } = "";
    public RedisOptions Redis { get; set; } = new();
    public Dictionary<string, TelemetryGuardSiteOptions> Sites { get; set; }
        = new(StringComparer.Ordinal);
}

public sealed class RedisOptions
{
    public string? ConnectionString { get; set; }
    public string KeyPrefix { get; set; } = "tg:native:";
}

/// <summary>
/// The explicit security boundary between a public site key, TG tenant, and MarketIQ tenant.
/// The browser never supplies CompanyId or TenantId.
/// </summary>
public sealed class TelemetryGuardSiteOptions
{
    public bool Enabled { get; set; } = true;
    public Guid TenantId { get; set; }
    public int CompanyId { get; set; }
    public string Domain { get; set; } = "";
    public string? LandingUrl { get; set; }
    public IReadOnlyList<string> DecoyPaths { get; set; } = Array.Empty<string>();
    public IReadOnlyList<TelemetryGuardConversionGoal> ConversionGoals { get; set; }
        = Array.Empty<TelemetryGuardConversionGoal>();
}

public sealed class TelemetryGuardConversionGoal
{
    public TelemetryGuardConversionGoal() { }

    public TelemetryGuardConversionGoal(
        string goalId, string name, string triggerType, IReadOnlyList<string> pagePaths,
        string? selector, int? minimumSeconds)
    {
        GoalId = goalId;
        Name = name;
        TriggerType = triggerType;
        PagePaths = pagePaths;
        Selector = selector;
        MinimumSeconds = minimumSeconds;
    }

    public string GoalId { get; set; } = "";
    public string Name { get; set; } = "";
    public string TriggerType { get; set; } = "";
    public IReadOnlyList<string> PagePaths { get; set; } = Array.Empty<string>();
    public string? Selector { get; set; }
    public int? MinimumSeconds { get; set; }
}
