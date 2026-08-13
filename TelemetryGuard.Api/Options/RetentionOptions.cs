namespace TelemetryGuard.Api.Options;

/// <summary>
/// Fallback analytics retention (spec D20). Per-tenant RetentionDays (30–180)
/// is denormalized onto every analytics row at ingest; this default applies
/// only when the tenant config row cannot be read. Bound from the "Retention"
/// section (API-01's appsettings layout).
/// </summary>
public sealed class RetentionOptions
{
    public int DefaultDays { get; init; } = 90;
}
