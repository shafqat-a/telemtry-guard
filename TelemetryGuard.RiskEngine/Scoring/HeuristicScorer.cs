using Microsoft.Extensions.Options;
using TelemetryGuard.RiskEngine.Contracts;

namespace TelemetryGuard.RiskEngine.Scoring;

/// <summary>
/// The transparent weighted-heuristic scorer that ships at MVP because no labeled data
/// exists pre-launch (D18). Weighted contributions for T2 signals, small jointly-capped
/// contributions for T3, negative-evidence reductions, clamped to 0–100. NaN floats and
/// null bools contribute EXACTLY 0 (missing ≠ zero) — but HasJsBeacon == false is itself
/// a real T2 signal ("no JS executed") with a real weight.
///
/// This scorer NEVER sees T1 rule floors: <see cref="ScoreResult.RuleHits"/> is empty by
/// contract (RSK-01) and the scoring pipeline (RSK-07) applies the floor exactly once via
/// <c>finalScore = Math.Max(heuristicScore, ruleFloor ?? 0)</c> and attaches the hits.
///
/// Pure, thread-safe, synchronous, allocation-light — runs in-process inside the
/// &lt; 50 ms scoring budget (D3). Weights are read from IOptionsMonitor per call, so a
/// config change retunes the scorer without redeploy.
/// </summary>
public sealed class HeuristicScorer(IOptionsMonitor<HeuristicWeights> weights) : IScorer
{
    public string ScorerVersion => "heuristic-1";

    public ScoreResult Score(in FraudFeatureVector v)
    {
        var w = weights.CurrentValue;
        double s = 0;
        // T2 booleans — null contributes 0 (missing ≠ zero):
        if (!v.HasJsBeacon) s += w.NoJsBeacon;
        if (v.EmulatorOrVm == true) s += w.EmulatorOrVm;
        if (v.ScreenResAnomalous == true) s += w.ScreenResAnomalous;
        if (v.IpProxyOrVpn == true) s += w.IpProxyOrVpn;           // Private Relay already carved out upstream
        if (v.IpGeoTargetMismatch == true) s += w.IpGeoTargetMismatch;
        if (v.InputModalityMismatch == true) s += w.InputModalityMismatch;
        // T2 scaled — every float guarded by IsNaN (NaN contributes 0):
        if (!float.IsNaN(v.StorageAgeZeroRepeat) && v.StorageAgeZeroRepeat > 1)
            s += Math.Min((v.StorageAgeZeroRepeat - 1) * w.StorageAgeZeroRepeatPerHit, w.StorageAgeZeroRepeatCap);
        if (!float.IsNaN(v.TimeOnPageSec) && v.TimeOnPageSec < w.ShortDwellThresholdSec) s += w.ShortDwell;
        if (!float.IsNaN(v.FormFillTimeSec) && v.FormFillTimeSec < w.FastFormThresholdSec
            && v.AutofillDetected != true) s += w.FastForm;        // CTX conditioning
        if (!float.IsNaN(v.FirstInteractionDelayMs) && v.FirstInteractionDelayMs < w.InstantInteractionThresholdMs)
            s += w.InstantInteraction;
        // Velocity ramps (plain numerics; 0 when cold contributes 0 naturally):
        s += Math.Min(v.IpClicksLastMin, 30) / 30.0 * w.IpClicksPerMinFull;
        s += Ramp(v.DeviceSessionsLastHour, w.DeviceSessionsThreshold, w.DeviceSessionsPerUnit, w.DeviceSessionsCap);
        s += Ramp(v.IpDistinctUasLastHour, w.IpDistinctUasThreshold, w.IpDistinctUasPerUnit, w.IpDistinctUasCap);
        var devThr = v.AsnType == AsnType.Mobile ? w.DeviceIdsPerIpThresholdMobile : w.DeviceIdsPerIpThreshold;
        s += Ramp(v.DeviceIdsThisIpHour, devThr, w.DeviceIdsPerIpPerUnit, w.DeviceIdsPerIpCap);
        // T3 block, capped:
        double t3 = 0;
        if (v.CookiesDisabled == true) t3 += w.CookiesDisabled;
        if (v.CanvasFpBlocked == true) t3 += w.CanvasFpBlocked;
        if (v.TimezoneIpMismatch == true) t3 += w.TimezoneIpMismatch;
        if (v.LanguageGeoMismatch == true) t3 += w.LanguageGeoMismatch;
        t3 += Math.Clamp(v.IpReputationBad, 0f, 1f) * w.IpReputationBadMax;
        if (v.PasteInIdentityFields == true) t3 += w.PasteInIdentityFields;
        if (v.ReferrerMissing) t3 += w.ReferrerMissing;
        if (v.ClockSkewBad == true) t3 += w.ClockSkewBad;
        s += Math.Min(t3, w.T3TotalCap);
        // Challenge outcome (CTX):
        if (v.ChallengeOutcome == ChallengeOutcome.Failed) s += w.ChallengeFailed;
        if (v.ChallengeOutcome == ChallengeOutcome.Passed) s -= w.NegChallengePassed;
        // Negative evidence:
        if (!float.IsNaN(v.TimeOnPageSec) && v.TimeOnPageSec > w.NegLongDwellThresholdSec) s -= w.NegLongDwell;
        if (!float.IsNaN(v.MousePathLinearity) && v.MousePathLinearity is >= 0.2f and <= 0.85f
            && !float.IsNaN(v.InputEventCount) && v.InputEventCount >= 20) s -= w.NegHumanMousePath;
        if (v.FormSubmitted == true && !float.IsNaN(v.FormFillTimeSec) && v.FormFillTimeSec >= 5) s -= w.NegPlausibleForm;

        var score = (int)Math.Clamp(Math.Round(s), 0, 100);
        return new ScoreResult(score, Array.Empty<string>(), ScorerVersion, FraudFeatureVector.FeatureSetVersion);

        static double Ramp(long value, double threshold, double perUnit, double cap)
            => value <= threshold ? 0 : Math.Min((value - threshold) * perUnit, cap);
    }
}
