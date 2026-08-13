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
    public double StdInterEventMsThreshold { get; set; } = 2.0;
    public int RoboticCadenceMinEvents { get; set; } = 10;    // evidence gate
    public int RoboticCadenceFloor { get; set; } = 85;
    public int ClickIdInvalidFloor { get; set; } = 71;        // missing/replayed gclid on paid traffic
}
