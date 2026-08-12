---
id: RSK-06
title: "Heuristic scorer (MVP)"
phase: 1
workstream: risk
depends_on: [RSK-01]
size: M
spec_refs: [D18, "§6.3", "§7"]
detail_level: full
---

# RSK-06: Heuristic scorer (MVP)

## Objective

Implement `HeuristicScorer : IScorer` (`ScorerVersion = "heuristic-1"`) in `TelemetryGuard.RiskEngine`: the transparent weighted-contribution scorer that produces the 0–100 score at launch, because the MVP ships without a trained model (D18). Weighted contributions for T2 signals, small capped contributions for T3, negative-evidence reductions, clamp to 0–100. NaN/null contributes exactly 0. All weights live in a config-bound options class read via `IOptionsMonitor` so they are tunable without redeploy. Property-style tests verify bounds and monotonicity.

## Spec context (self-contained)

- D18 (cold start): no labeled data exists pre-launch, so the MVP scores with deterministic T1 rules + a **transparent weighted-heuristic scorer** behind `IScorer`; heuristic → LightGBM later is a config swap, not a rewrite. Every verdict is stamped with `scorer_version` so heuristic-era data is never confused with model-era data.
- §6.3 bands: 0–30 allow, 31–70 challenge, 71–100 block. The heuristic's job is to place suspicious-but-not-proven traffic into the challenge band and stack strong evidence into block; T1 rules (RSK-05) separately floor proven cases — the PIPELINE (RSK-07) combines `finalScore = Math.Max(heuristicScore, ruleFloor ?? 0)`. The scorer itself does NOT apply rule floors and returns empty `RuleHits`.
- §7 tiers: T2 = strong features (main weight), T3 = weak/supporting — capped small because privacy tools (Brave, Firefox, password managers) fire them on real humans; CTX = conditioning only (never scored directly, but conditions other contributions: `autofill_detected` conditions `form_fill_time_sec`; `asn_type` normalizes `device_ids_this_ip_hour` for carrier-grade NAT).
- **Missing ≠ zero**: NaN floats and null bools contribute 0 to the score — but `has_js_beacon == false` is itself a real T2 signal with a real weight (that is the encoded form of "no JS executed").
- Runs in-process, synchronous, inside the < 50 ms budget (D3).

## Prerequisites

RSK-01 completed: `TelemetryGuard.RiskEngine.Contracts` with `FraudFeatureVector` (member names below), `IScorer`, `ScoreResult`, `AsnType`, `ChallengeOutcome`. Lives in `TelemetryGuard.RiskEngine` (reference Contracts).

## Implementation steps

1. Create `TelemetryGuard.RiskEngine/Scoring/HeuristicWeights.cs` — config section `"Scoring:Heuristic"`, every field tunable without redeploy:

```csharp
namespace TelemetryGuard.RiskEngine.Scoring;

/// <summary>All heuristic contributions. Positive = evidence of fraud; Negative* = evidence
/// of humanity. Values are DEFENSIBLE DEFAULTS to be tuned during the Phase-1.5
/// listen-only window (D18). Bound via IOptionsMonitor — a config change, no redeploy.</summary>
public sealed class HeuristicWeights
{
    // --- T2 boolean signals (added when the signal == true) ---
    public double NoJsBeacon { get; set; } = 15;            // HasJsBeacon == false
    public double EmulatorOrVm { get; set; } = 25;
    public double ScreenResAnomalous { get; set; } = 10;
    public double IpProxyOrVpn { get; set; } = 20;
    public double IpGeoTargetMismatch { get; set; } = 15;
    public double InputModalityMismatch { get; set; } = 15;

    // --- T2 scaled/threshold signals ---
    public double StorageAgeZeroRepeatPerHit { get; set; } = 5;    // (count-1) * this, cap below
    public double StorageAgeZeroRepeatCap { get; set; } = 25;
    public double ShortDwell { get; set; } = 10;            // TimeOnPageSec < ShortDwellThresholdSec
    public double ShortDwellThresholdSec { get; set; } = 2;
    public double FastForm { get; set; } = 20;              // FormFillTimeSec < FastFormThresholdSec && AutofillDetected != true
    public double FastFormThresholdSec { get; set; } = 3;
    public double InstantInteraction { get; set; } = 15;    // FirstInteractionDelayMs < InstantInteractionThresholdMs
    public double InstantInteractionThresholdMs { get; set; } = 100;

    // --- Velocity (T2), linear ramps capped ---
    public double IpClicksPerMinFull { get; set; } = 30;    // contribution at/beyond 30 clicks/min: min(v,30)/30 * this
    public double DeviceSessionsThreshold { get; set; } = 5;   // per unit above threshold:
    public double DeviceSessionsPerUnit { get; set; } = 3;
    public double DeviceSessionsCap { get; set; } = 20;
    public double IpDistinctUasThreshold { get; set; } = 3;
    public double IpDistinctUasPerUnit { get; set; } = 4;
    public double IpDistinctUasCap { get; set; } = 20;
    public double DeviceIdsPerIpThreshold { get; set; } = 5;      // NON-mobile ASN
    public double DeviceIdsPerIpThresholdMobile { get; set; } = 10; // AsnType.Mobile — carrier-grade NAT normalization
    public double DeviceIdsPerIpPerUnit { get; set; } = 3;
    public double DeviceIdsPerIpCap { get; set; } = 20;

    // --- T3, individually small AND jointly capped (privacy-tool FP protection) ---
    public double CookiesDisabled { get; set; } = 3;
    public double CanvasFpBlocked { get; set; } = 2;
    public double TimezoneIpMismatch { get; set; } = 4;
    public double LanguageGeoMismatch { get; set; } = 3;
    public double IpReputationBadMax { get; set; } = 0;     // IpReputationBad (0..1) * this.
                                                            // Default 0: NO task in any planned phase
                                                            // produces IpReputationBad (RSK-04 hardcodes 0f);
                                                            // raise only when a reputation store
                                                            // (backlog, suggested P2-06) exists.
    public double PasteInIdentityFields { get; set; } = 2;
    public double ReferrerMissing { get; set; } = 2;
    public double T3TotalCap { get; set; } = 15;            // hard cap on the summed T3 block

    // --- Negative evidence (subtracted; clamp keeps score >= 0) ---
    public double NegChallengePassed { get; set; } = 30;    // ChallengeOutcome.Passed
    public double NegLongDwell { get; set; } = 10;          // TimeOnPageSec > NegLongDwellThresholdSec
    public double NegLongDwellThresholdSec { get; set; } = 30;
    public double NegHumanMousePath { get; set; } = 10;     // 0.2 <= linearity <= 0.85 && InputEventCount >= 20
    public double NegPlausibleForm { get; set; } = 10;      // FormSubmitted == true && FormFillTimeSec >= 5

    // --- Challenge failure (strong positive; CTX-conditioned) ---
    public double ChallengeFailed { get; set; } = 40;       // ChallengeOutcome.Failed
}
```

2. Create `TelemetryGuard.RiskEngine/Scoring/HeuristicScorer.cs`:

```csharp
using Microsoft.Extensions.Options;
using TelemetryGuard.RiskEngine.Contracts;

namespace TelemetryGuard.RiskEngine.Scoring;

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
```

(The snippet above is normative for the contribution structure; keep it readable rather than micro-optimized — it is already allocation-free apart from the result record.)

3. DI: `services.Configure<HeuristicWeights>(config.GetSection("Scoring:Heuristic")); services.AddSingleton<IScorer, HeuristicScorer>();` in the risk-engine registration extension. Add the (empty-is-fine) `"Scoring": { "Heuristic": {} }` section to the API's `appsettings.json` with a comment that any field from `HeuristicWeights` can be overridden.

4. Doc comment on the class: the scorer NEVER sees rule floors; `RuleHits` is empty by contract (RSK-01) and the pipeline (RSK-07) computes `Math.Max(score, floor)`.

## Files to create or modify

- `TelemetryGuard.RiskEngine/Scoring/HeuristicWeights.cs`
- `TelemetryGuard.RiskEngine/Scoring/HeuristicScorer.cs`
- DI extension file (registrations)
- `tests/TelemetryGuard.Tests.Unit/Scoring/HeuristicScorerTests.cs`
- `tests/TelemetryGuard.Tests.Unit/Scoring/HeuristicScorerPropertyTests.cs`

## Acceptance criteria

- `dotnet build` passes; `HeuristicScorer.ScorerVersion == "heuristic-1"`; result always carries `FeatureSetVersion == 1` and empty `RuleHits`.
- Baseline: `Score(new FraudFeatureVector { HasJsBeacon = true })` == 0 (a clean beaconed session with cold velocity scores zero).
- Default (all-defaults vector — no beacon): score == `NoJsBeacon` default (15) and nothing else (proves NaN/null contribute 0 across every guarded branch).
- Deterministic example tests (with default weights):
  - `HasJsBeacon=false, IpProxyOrVpn=true, IpClicksLastMin=30` → 15+20+30 = 65 (challenge band).
  - `EmulatorOrVm=true, ScreenResAnomalous=true, StorageAgeZeroRepeat=6, HasJsBeacon=true` → 25+10+25 = 60.
  - T3 cap: all seven T3 signals firing (`IpReputationBad=1`) with `HasJsBeacon=true` → exactly 15 (`T3TotalCap`), not 16 (`IpReputationBadMax` defaults to 0 — no producer exists for that signal; the remaining six default weights sum to 16). A second assertion with a stubbed `IpReputationBadMax=5` yields the same 15 (cap binds either way).
  - Carrier-NAT normalization: `DeviceIdsThisIpHour=8, AsnType=Mobile` → 0 from that term; same value with `AsnType=Datacenter` → 9.
  - Negative evidence: `HasJsBeacon=true, IpProxyOrVpn=true (20), ChallengeOutcome=Passed (−30)` → 0 (clamped, never negative).
  - `ChallengeOutcome=Failed` alone (beaconed) → 40.
- Property tests (hand-rolled randomized, fixed seed, ≥ 1000 iterations each — no FsCheck dependency):
  - **Bounds**: for random vectors (random mix of NaN/null/values), `0 <= Score <= 100` always.
  - **Monotonicity (key signals)**: for any random vector v, `Score(v with { IpClicksLastMin = v.IpClicksLastMin + k }) >= Score(v)` for random k ≥ 0; `Score(v with { IpProxyOrVpn = true }) >= Score(v with { IpProxyOrVpn = null })`; `Score(v with { EmulatorOrVm = true }) >= Score(v with { EmulatorOrVm = null })`; `Score(v with { DeviceIdsThisIpHour = +k }) >= Score(v)`.
  - **NaN-neutrality**: replacing any single non-NaN float with NaN never RAISES the score.
- Weights tunability: a test with a stubbed `IOptionsMonitor` returning `NoJsBeacon = 50` yields 50 for the no-beacon vector — no rebuild/re-registration.
- `dotnet test --filter "FullyQualifiedName~Scoring"` passes.

## Testing

Unit only, pure/synchronous. Build random vectors with a seeded `Random` helper that, per property, chooses NaN/null with ~30 % probability. Use a tiny `TestOptionsMonitor<T>` stub (implements `IOptionsMonitor<T>` returning a fixed instance) — likely already created for RSK-05; share it under `tests/TelemetryGuard.Tests.Unit/TestSupport/`.

## Out of scope / guardrails

- The scorer must NOT apply T1 rule floors, emit rule hits, or map to verdict bands — pipeline concerns (RSK-07). Rules only raise scores and that Max-fold happens exactly once, in the pipeline.
- NaN/null contribute EXACTLY 0 — never treat missing as false/0-valued evidence and never let NaN leak into arithmetic (every float term needs an `IsNaN` guard; an unguarded NaN would poison the sum and clamp to 0, silently allowing fraud).
- T3 contributions stay individually small and jointly capped — T3 signals fire on privacy-conscious humans by design (spec §7); do not promote them toward rule-like weights.
- CTX fields (`AsnType`, `IsMobile`, `IsPrivateRelay`, `FormSubmitted`, `AutofillDetected`, `IsPaidClick`) condition other terms only — no direct score contribution from CTX (exception as specified: `ChallengeOutcome`, which §6.3 explicitly feeds into re-scoring).
- No ML.NET/LightGBM here — Phase 1 ships model-free (D18); the model scorer arrives in RSK-08 behind the same `IScorer`.
- Synchronous, no I/O, allocation-light (< 50 ms budget, D3). Weights via `IOptionsMonitor` only — no custom config reload machinery.
- No server-side Python/Node (D1).
