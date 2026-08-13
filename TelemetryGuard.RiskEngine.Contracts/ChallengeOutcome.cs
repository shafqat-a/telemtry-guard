namespace TelemetryGuard.RiskEngine.Contracts;

/// <summary>Outcome of a Cloudflare Turnstile challenge for this session.
/// CTX conditioning input for re-scoring (spec §6.3: 31–70 band → challenge → re-score).</summary>
public enum ChallengeOutcome
{
    NotChallenged = 0,
    Passed = 1,
    Failed = 2
}
