namespace TelemetryGuard.Portal.Options;

/// <summary>Bound from the "Portal" configuration section (P2-03). The ONLY
/// external dependency this project may ever have — no SQL/ClickHouse/Redis
/// connection string may be added here (D23).</summary>
public sealed class PortalOptions
{
    public const string SectionName = "Portal";

    public string ApiBaseUrl { get; set; } = "";
    public int ApiTimeoutSeconds { get; set; } = 15;
    public int SessionHours { get; set; } = 8;
    public int SignInAttemptsPerIpPer5Min { get; set; } = 10;
}
