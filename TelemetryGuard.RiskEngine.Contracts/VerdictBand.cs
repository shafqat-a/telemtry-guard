namespace TelemetryGuard.RiskEngine.Contracts;

/// <summary>Enforcement band (spec §6.3).</summary>
public enum VerdictBand
{
    Allow = 0,      // 0–30: count conversion / valid click
    Challenge = 1,  // 31–70: Cloudflare Turnstile; re-score with outcome
    Block = 2       // 71–100: block; exclude from attribution; feed exclusion sync
}

public static class BandMapper
{
    /// <summary>Maps a 0–100 score to its band. Throws outside 0–100 — an out-of-range
    /// score is a scorer bug and must never be silently clamped into an enforcement band.</summary>
    public static VerdictBand ToBand(int score) => score switch
    {
        >= 0  and <= 30  => VerdictBand.Allow,
        >= 31 and <= 70  => VerdictBand.Challenge,
        >= 71 and <= 100 => VerdictBand.Block,
        _ => throw new ArgumentOutOfRangeException(nameof(score), score, "Score must be within 0–100.")
    };
}
