using Microsoft.Extensions.Options;
using TelemetryGuard.RiskEngine.Contracts;

namespace TelemetryGuard.RiskEngine.Rules;

/// <summary>
/// Deterministic tier-1 rule engine (spec §7). Evaluates the rule table in order and
/// folds every fired rule's floor with <see cref="Math.Max(int, int)"/> — rules only
/// ever RAISE scores, never lower them (spec §6.3).
///
/// Floors map to enforcement bands per <c>BandMapper</c>: a floor ≥ 71 forces at least
/// the Block band, ≥ 31 at least Challenge. The floor is combined with the scorer output
/// by the PIPELINE (RSK-07) as <c>finalScore = Math.Max(scorerScore, floor ?? 0)</c> —
/// the rule engine itself does not touch the scorer.
///
/// Null/NaN semantics: <c>bool?</c> null and <c>float.NaN</c> are absence of evidence,
/// not evidence — a missing signal NEVER fires a rule (T1 "absence proves nothing").
/// Rule 6 fires only on explicit <c>BeaconIntegrityOk == false</c> (tampered beacon),
/// never on null (no beacon). Rules 7/9 are gated on <c>IsPaidClick</c> (Tor/datacenter
/// egress on organic traffic is common privacy behavior, not click fraud). Rules 12/13
/// carry IsNaN guards plus minimum-event evidence gates so sparse input never fires them.
///
/// Spec-pinned floors (rules 1–9) are consts — moving them requires a spec amendment
/// (D24+), not an ops tweak. Unpinned floors/thresholds (rules 10–14) live in
/// <see cref="RulesOptions"/>, tune-during-listen-only (D18), bound via IOptionsMonitor.
///
/// T3 signals are never rule-eligible (privacy-tool false positives) — this class must
/// not reference any T3 member of <see cref="FraudFeatureVector"/>.
///
/// Pure, synchronous, allocation-light — runs in-process inside the &lt; 50 ms scoring
/// budget (D3). Register as singleton.
/// </summary>
public sealed class T1RuleEngine : IT1RuleEngine
{
    // Spec-pinned floors (§7) — constants by design; NOT config.
    private const int HoneypotTouchedFloor = 95;
    private const int ClickBeforeRenderFloor = 90;
    private const int PointerUntrustedFloor = 90;
    private const int WebdriverFlagFloor = 85;
    private const int HeadlessBrowserFloor = 85;
    private const int BeaconIntegrityFailedFloor = 85;
    private const int IpTorPaidFloor = 80;
    private const int TlsUaMismatchFloor = 80;
    private const int IpDatacenterPaidFloor = 70;

    private readonly IOptionsMonitor<RulesOptions> _options;

    public T1RuleEngine(IOptionsMonitor<RulesOptions> options)
    {
        _options = options;
    }

    public T1Result Evaluate(in FraudFeatureVector v)
    {
        var opt = _options.CurrentValue;

        List<string>? hits = null;
        var floor = 0;

        // 1–9: spec-pinned floors. `bool? == true` is false for null — absent never fires.
        if (v.HoneypotTouched == true)
            Hit(ref hits, ref floor, "honeypot_touched", HoneypotTouchedFloor);

        if (v.ClickBeforeRender == true)
            Hit(ref hits, ref floor, "click_before_render", ClickBeforeRenderFloor);

        if (v.PointerUntrusted == true)
            Hit(ref hits, ref floor, "pointer_untrusted", PointerUntrustedFloor);

        if (v.WebdriverFlag == true)
            Hit(ref hits, ref floor, "webdriver_flag", WebdriverFlagFloor);

        if (v.HeadlessBrowser == true)
            Hit(ref hits, ref floor, "headless_browser", HeadlessBrowserFloor);

        // Fires ONLY on explicit false (tampered beacon); null = no beacon = no evidence.
        if (v.BeaconIntegrityOk == false)
            Hit(ref hits, ref floor, "beacon_integrity_failed", BeaconIntegrityFailedFloor);

        if (v.IpTor == true && v.IsPaidClick)
            Hit(ref hits, ref floor, "ip_tor_paid", IpTorPaidFloor);

        if (v.TlsUaMismatch == true)
            Hit(ref hits, ref floor, "tls_ua_mismatch", TlsUaMismatchFloor);

        if (v.IpDatacenterAsn == true && v.IsPaidClick)
            Hit(ref hits, ref floor, "ip_datacenter_paid", IpDatacenterPaidFloor);

        // 10–14: spec-unpinned — floors/thresholds from RulesOptions (tune-during-listen-only).
        if (v.IpClicksLastMin >= opt.IpClicksLastMinThreshold)
            Hit(ref hits, ref floor, "ip_click_flood", opt.IpClickFloodFloor);

        if (v.UaOsMismatch == true)
            Hit(ref hits, ref floor, "ua_os_mismatch", opt.UaOsMismatchFloor);

        if (!float.IsNaN(v.MousePathLinearity) && v.MousePathLinearity >= opt.MousePathLinearityThreshold
            && !float.IsNaN(v.InputEventCount) && v.InputEventCount >= opt.LinearMousePathMinEvents)
            Hit(ref hits, ref floor, "linear_mouse_path", opt.LinearMousePathFloor);

        // Rule 13 (robotic_cadence): gated on the gap count the σ was computed from and
        // disabled by default — see RulesOptions for why the throttled SDK sampling makes
        // the current measurement unreliable on high-refresh displays and privacy browsers.
        if (opt.RoboticCadenceEnabled
            && !float.IsNaN(v.StdInterEventMs) && v.StdInterEventMs < opt.StdInterEventMsThreshold
            && !float.IsNaN(v.MouseMoveGaps) && v.MouseMoveGaps >= opt.RoboticCadenceMinGaps)
            Hit(ref hits, ref floor, "robotic_cadence", opt.RoboticCadenceFloor);

        if (v.ClickIdInvalid == true)
            Hit(ref hits, ref floor, "click_id_invalid", opt.ClickIdInvalidFloor);

        return hits is null ? T1Result.None : new T1Result(floor, hits);
    }

    /// <summary>Records a fired rule: appends the hit name (evaluation order) and raises
    /// the accumulated floor via Math.Max — floors only ever go up.</summary>
    private static void Hit(ref List<string>? hits, ref int floor, string name, int ruleFloor)
    {
        (hits ??= new List<string>(4)).Add(name);
        floor = Math.Max(floor, ruleFloor);
    }
}
