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
    public double ClockSkewBad { get; set; } = 2;           // sent_at far from receive time; ordinary on bad clocks
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
