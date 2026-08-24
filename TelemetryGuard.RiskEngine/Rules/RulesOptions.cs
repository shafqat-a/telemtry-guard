namespace TelemetryGuard.RiskEngine.Rules;

/// <summary>Defaults for rules the spec lists without pinned floors.
/// ALL values here are TUNE-DURING-LISTEN-ONLY (D18 Phase 1.5): revisit against real
/// traffic before the first model ships. Bound via IOptionsMonitor — tunable without redeploy.</summary>
public sealed class RulesOptions
{
    public int IpClicksLastMinThreshold { get; set; } = 30;   // clicks/min from one IP considered a flood
    public int IpClickFloodFloor { get; set; } = 75;
    public int UaOsMismatchFloor { get; set; } = 71;          // just into block band; header spoof is deliberate
    public double MousePathLinearityThreshold { get; set; } = 0.99;
    public int LinearMousePathMinEvents { get; set; } = 20;   // evidence gate
    public int LinearMousePathFloor { get; set; } = 85;
    // robotic_cadence — OFF by default until real σ distributions have been observed in
    // listen-only (D18). What the server measures is the std of gaps between pointer
    // samples the SDK has ALREADY throttled to one per 50 ms (pointer.ts), rounded to
    // whole milliseconds: during continuous movement each gap is the first frame after
    // the 50 ms boundary, so σ ≈ frame/√12 — ~4.8 ms at 60 Hz but ~1.2 ms at 240 Hz —
    // and Firefox with privacy.resistFingerprinting (100 ms timer precision) yields
    // σ = 0 exactly. A threshold that catches scripted cadence also catches those
    // humans; the honest fix is cadence statistics over un-throttled events on the
    // client, which is a wire-contract change. Until then: disabled, and when enabled
    // it gates on the number of GAPS behind the σ (MouseMoveGaps), never on keystrokes
    // or clicks (InputEventCount), with a threshold below the 240 Hz figure.
    public bool RoboticCadenceEnabled { get; set; } = false;
    public double StdInterEventMsThreshold { get; set; } = 0.5;
    public int RoboticCadenceMinGaps { get; set; } = 20;      // evidence gate on MouseMoveGaps
    public int RoboticCadenceFloor { get; set; } = 85;
    public int ClickIdInvalidFloor { get; set; } = 71;        // missing/replayed gclid on paid traffic
}
