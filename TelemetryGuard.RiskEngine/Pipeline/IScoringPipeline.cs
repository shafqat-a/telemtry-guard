using TelemetryGuard.RiskEngine.Contracts;

namespace TelemetryGuard.RiskEngine.Pipeline;

/// <summary>Final product of one ScoreSessionAsync call. Whitelisted outcomes are
/// explicitly FLAGGED (D19) so the override loop never masquerades as an organic allow
/// in verdicts or training data (RSK-08 excludes whitelisted verdicts from labels).</summary>
public sealed record ScoringOutcome(
    ScoreResult Result, VerdictBand Band, bool Whitelisted, double DurationMs);

/// <summary>The scoring orchestrator (RSK-07): whitelist short-circuit → parallel
/// prefetch → extract → rules → scorer → Math.Max floor fold → band. Runs in-process
/// inside the &lt; 50 ms budget (D3): the non-whitelisted path performs exactly two
/// awaited Redis round trips (session batch, velocity batch) plus one optional cached
/// campaign lookup — enrichment is synchronous in-process.</summary>
public interface IScoringPipeline
{
    /// <summary>Returns null when the session id is unknown (no click, no beacon).
    /// <paramref name="outcome"/> is the Turnstile result for §6.3 re-scoring:
    /// API-05 passes ChallengeOutcome.Passed/Failed after verification; first-score
    /// callers (API-06 worker, initial /decide call) omit it. Tenant stays ambient
    /// via ITenantContext (D11) — never a parameter.</summary>
    Task<ScoringOutcome?> ScoreSessionAsync(
        string sessionId,
        ChallengeOutcome outcome = ChallengeOutcome.NotChallenged,
        CancellationToken ct = default);
}
