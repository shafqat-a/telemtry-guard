namespace TelemetryGuard.Client;

/// <summary>Host-owned configuration for the embeddable, collection-only TG surface.</summary>
public sealed class TelemetryGuardClientOptions
{
    public const string SectionName = "TelemetryGuard";

    public bool Enabled { get; set; }
    public string PathBase { get; set; } = "/tg";
    public int SessionTtlMinutes { get; set; } = 30;
    public int FinalizeQuietSeconds { get; set; } = 10;
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
}
