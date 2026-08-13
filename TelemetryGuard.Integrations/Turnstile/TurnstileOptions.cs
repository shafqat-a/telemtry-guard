namespace TelemetryGuard.Integrations.Turnstile;

/// <summary>
/// Platform-level Turnstile configuration (spec D14). MVP uses ONE key pair for the
/// whole platform; per-tenant keys are a known later enhancement — when that lands,
/// the secret becomes a per-tenant lookup and this options class keeps the defaults.
/// Bound from configuration section "Turnstile" (see TelemetryGuard.Api/appsettings.json).
/// </summary>
public sealed class TurnstileOptions
{
    public const string SectionName = "Turnstile";

    /// <summary>Public site key rendered into the widget (used by API-05 / SDK; not used for verification).</summary>
    public string SiteKey { get; set; } = "";

    /// <summary>Secret key for siteverify. Empty in dev ⇒ every verification returns unavailable (fail-closed).</summary>
    public string SecretKey { get; set; } = "";

    public string VerifyUrl { get; set; } = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    /// <summary>Per-attempt timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 2;

    /// <summary>Retries AFTER the first attempt (2 ⇒ up to 3 attempts total).</summary>
    public int MaxRetries { get; set; } = 2;
}
