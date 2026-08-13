namespace TelemetryGuard.Api.Options;

/// <summary>
/// Score-to-band thresholds (spec §6.3): 0..AllowMax -> allow, (AllowMax..ChallengeMax]
/// -> challenge, above ChallengeMax -> block. Bound from configuration section
/// "Scoring:Bands". API-05's /decide endpoint is the primary consumer and must
/// never hardcode these thresholds.
/// </summary>
public sealed class ScoringBandOptions
{
    public const string SectionName = "Scoring:Bands";

    public int AllowMax { get; set; } = 30;
    public int ChallengeMax { get; set; } = 70;
}
