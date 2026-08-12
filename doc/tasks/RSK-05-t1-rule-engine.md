---
id: RSK-05
title: "T1 rule engine"
phase: 1
workstream: risk
depends_on: [RSK-01]
size: M
spec_refs: ["§6.3", "§7", D18]
detail_level: full
---

# RSK-05: T1 rule engine

## Objective

Implement `IT1RuleEngine.Evaluate(in FraudFeatureVector) -> T1Result(int? Floor, IReadOnlyList<string> Hits)` in `TelemetryGuard.RiskEngine`: the deterministic tier-1 rule set from spec §7 that establishes a score FLOOR (rules only ever raise scores, folded with `Math.Max`; they never lower). NaN/null signals never fire a rule. Spec-pinned floors are hard-coded constants; the handful of rules the spec lists without explicit floors get defensible defaults in a `RulesOptions` config class, each marked tune-during-listen-only.

## Spec context (self-contained)

- §6.3: "Deterministic T1 rules may **raise** a score floor pre-model; rules never lower a score." Bands: 0–30 allow, 31–70 challenge, 71–100 block — so a floor ≥ 71 forces at least the block band, ≥ 31 at least challenge.
- §7 T1 tier definition: near-deterministic **when it fires**; absence proves nothing — hence NaN/null must NEVER fire a rule (a no-beacon session must not trip behavioral rules; a missing proxy DB must not trip IP rules).
- §7 rule list with spec-pinned floors:
  - `honeypot_touched` ≥ **95**
  - `click_before_render` / `pointer_untrusted` ≥ **90**
  - `webdriver_flag` / `headless_browser` ≥ **85**
  - `beacon_integrity_ok = false` ≥ **85** (fires on explicit false — a tampered beacon — never on null/no-beacon)
  - `ip_tor` **paid-only** ≥ **80**
  - `tls_ua_mismatch` ≥ **80**
  - `ip_datacenter_asn` **on paid click** ≥ **70** (challenge band — datacenter egress on paid traffic is suspicious but VPN-adjacent, so it lands at the challenge/block boundary)
  - Listed WITHOUT pinned floors (defaults are ours, config-tunable): high `ip_clicks_last_min`, `ua_os_mismatch`, near-1.0 `mouse_path_linearity`, near-0 `std_inter_event_ms`, `click_id_invalid` (missing/replayed gclid/fbclid).
- T3 signals are NEVER rule-eligible (privacy-tool false positives) — the rule engine must not reference any T3 member.
- D18: MVP is rules + heuristic; unpinned thresholds/floors are tuned during the Phase-1.5 listen-only window — hence `IOptionsMonitor` so tuning is a config change, not a redeploy.
- Runs in-process inside the < 50 ms scoring budget (D3): synchronous, allocation-light.

## Prerequisites

RSK-01 completed: `TelemetryGuard.RiskEngine.Contracts` provides `FraudFeatureVector` (members referenced below by exact name), `ChallengeOutcome`, `AsnType`. This task lives in `TelemetryGuard.RiskEngine` (project exists; add `ProjectReference` to Contracts if not present).

## Implementation steps

1. Create `TelemetryGuard.RiskEngine/Rules/T1Result.cs`:

```csharp
namespace TelemetryGuard.RiskEngine.Rules;

/// <summary>Floor == null when no rule fired. Hits lists fired rule names (snake_case,
/// matching spec §7 signal names) in evaluation order.</summary>
public sealed record T1Result(int? Floor, IReadOnlyList<string> Hits)
{
    public static T1Result None { get; } = new(null, Array.Empty<string>());
}

public interface IT1RuleEngine
{
    T1Result Evaluate(in TelemetryGuard.RiskEngine.Contracts.FraudFeatureVector v);
}
```

2. Create `TelemetryGuard.RiskEngine/Rules/RulesOptions.cs` — ONLY the spec-unpinned values (config section `"Rules"`):

```csharp
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
```

3. Create `TelemetryGuard.RiskEngine/Rules/T1RuleEngine.cs`. Constructor `T1RuleEngine(IOptionsMonitor<RulesOptions> options)`; register as singleton. Spec-pinned floors as private consts. `Evaluate` walks the table below in order, collecting `(name, floor)` for each rule whose condition is true, then folds `Floor = hits.Max(floor)` (`Math.Max` accumulation; `null` when empty). Use a stack-friendly local `List<string>?` allocated only on first hit (`T1Result.None` fast path when nothing fires).

**Rule table (normative — implement exactly; conditions reference `FraudFeatureVector` members):**

| # | Rule name (hit string) | Condition (C#) | Floor |
|---|---|---|---|
| 1 | `honeypot_touched` | `v.HoneypotTouched == true` | 95 (const) |
| 2 | `click_before_render` | `v.ClickBeforeRender == true` | 90 (const) |
| 3 | `pointer_untrusted` | `v.PointerUntrusted == true` | 90 (const) |
| 4 | `webdriver_flag` | `v.WebdriverFlag == true` | 85 (const) |
| 5 | `headless_browser` | `v.HeadlessBrowser == true` | 85 (const) |
| 6 | `beacon_integrity_failed` | `v.BeaconIntegrityOk == false` | 85 (const) |
| 7 | `ip_tor_paid` | `v.IpTor == true && v.IsPaidClick` | 80 (const) |
| 8 | `tls_ua_mismatch` | `v.TlsUaMismatch == true` | 80 (const) |
| 9 | `ip_datacenter_paid` | `v.IpDatacenterAsn == true && v.IsPaidClick` | 70 (const) |
| 10 | `ip_click_flood` | `v.IpClicksLastMin >= opt.IpClicksLastMinThreshold` | `opt.IpClickFloodFloor` (75) |
| 11 | `ua_os_mismatch` | `v.UaOsMismatch == true` | `opt.UaOsMismatchFloor` (71) |
| 12 | `linear_mouse_path` | `!float.IsNaN(v.MousePathLinearity) && v.MousePathLinearity >= opt.MousePathLinearityThreshold && !float.IsNaN(v.InputEventCount) && v.InputEventCount >= opt.LinearMousePathMinEvents` | `opt.LinearMousePathFloor` (85) |
| 13 | `robotic_cadence` | `!float.IsNaN(v.StdInterEventMs) && v.StdInterEventMs < opt.StdInterEventMsThreshold && !float.IsNaN(v.InputEventCount) && v.InputEventCount >= opt.RoboticCadenceMinEvents` | `opt.RoboticCadenceFloor` (85) |
| 14 | `click_id_invalid` | `v.ClickIdInvalid == true` | `opt.ClickIdInvalidFloor` (71) |

Null-safety notes baked into the conditions: `bool? == true` is false for null (never fires on absent); rule 6 fires ONLY on explicit `false` (null = no beacon = no evidence); rules 12/13 carry explicit `IsNaN` guards AND minimum-event evidence gates so sparse input never fires them; rules 7/9 require `IsPaidClick` (Tor/datacenter on organic traffic is common privacy behavior, not click fraud).

4. DI: add `services.Configure<RulesOptions>(config.GetSection("Rules")); services.AddSingleton<IT1RuleEngine, T1RuleEngine>();` to the risk-engine registration extension (create `AddT1Rules` or fold into the existing `AddRiskEngine`-style extension established by earlier RSK tasks).

5. Document in the class XML comment: floors map to bands (≥71 → at least Block, ≥31 → at least Challenge per `BandMapper`), and the floor is combined with the scorer output by the PIPELINE (RSK-07) as `finalScore = Math.Max(scorerScore, floor ?? 0)` — the rule engine itself does not touch the scorer.

## Files to create or modify

- `TelemetryGuard.RiskEngine/Rules/T1Result.cs`
- `TelemetryGuard.RiskEngine/Rules/RulesOptions.cs`
- `TelemetryGuard.RiskEngine/Rules/T1RuleEngine.cs`
- `TelemetryGuard.RiskEngine/TelemetryGuard.RiskEngine.csproj` (Options packages if missing)
- DI extension file (add registrations)
- `tests/TelemetryGuard.Tests.Unit/Rules/T1RuleEngineTests.cs`

## Acceptance criteria

- `dotnet build` passes.
- **One unit test per rule** (14 tests minimum): a vector with only that rule's trigger set fires exactly that hit with exactly its floor. Table-driven `[Theory]` is fine but each rule must appear.
- **NaN-never-fires test**: `new FraudFeatureVector()` (all defaults: NaN floats, null bools, zero velocity) → `Evaluate` returns `T1Result.None` (`Floor == null`, empty hits). This is the single most important test in the file.
- Paid-only gating: `IpTor = true, IsPaidClick = false` → no hit; same for `IpDatacenterAsn`. `BeaconIntegrityOk = null` → no hit; `= false` → `beacon_integrity_failed` at 85.
- Evidence gates: `MousePathLinearity = 1.0f, InputEventCount = 19` → no `linear_mouse_path` hit; `= 20` → hit. `StdInterEventMs = 0.5f, InputEventCount = 9` → no `robotic_cadence`; `= 10` → hit.
- Max-fold: vector with `HoneypotTouched = true` AND `IpDatacenterAsn = true, IsPaidClick = true` → `Floor == 95`, hits contains both `honeypot_touched` and `ip_datacenter_paid`.
- Rules only raise: no code path in `T1RuleEngine` can produce a floor lower than any fired rule's floor; there is no subtraction/decrement anywhere (review criterion), and a test asserts `Floor == hits.Select(floor).Max()`.
- Options respected: overriding `IpClicksLastMinThreshold` to 5 via options makes `IpClicksLastMin = 5` fire.
- No T3 member (`CookiesDisabled`, `CanvasFpBlocked`, `TimezoneIpMismatch`, `LanguageGeoMismatch`, `IpReputationBad`, `PasteInIdentityFields`, `ReferrerMissing`, `ScrollEvents`, `PagesViewed`) is referenced anywhere in `T1RuleEngine.cs` (grep criterion).
- `dotnet test --filter "FullyQualifiedName~Rules"` passes.

## Testing

`tests/TelemetryGuard.Tests.Unit/Rules/T1RuleEngineTests.cs` using the `RawSessionDataBuilder`-style pattern or direct `FraudFeatureVector` `with`-mutations from defaults. Include: per-rule tests, NaN-never-fires, paid-gating, evidence gates, max-fold, options override (use `OptionsMonitor` test double or `Options.Create` wrapped in a stub `IOptionsMonitor`). All pure/synchronous — no containers.

## Out of scope / guardrails

- Rules ONLY raise scores — `Math.Max` fold, `int?` floor; never emit anything that could lower the scorer's output (hard spec constraint §6.3).
- NaN/null NEVER fires a rule: `bool?` null and `float.NaN` are absence of evidence, not evidence (T1 "absence proves nothing"). No `?? false`-style coercions that would hide this; write conditions exactly as tabled.
- T3 signals are never rule-eligible — do not add rules on privacy-tool signals no matter how tempting during tuning.
- Do not combine with the scorer, map to bands, or short-circuit whitelists here — that is the pipeline (RSK-07). The rule engine is a pure function over the vector.
- Do not renumber/rename hit strings once shipped — they are stamped into verdicts (`RuleHits` in `ScoreResult`) and become training labels (RSK-08 uses T1 hits as weak positives).
- Spec-pinned floors (rules 1–9) are consts, NOT config — moving them requires a spec amendment (D24+), not an ops tweak. Unpinned defaults (rules 10–14) stay in `RulesOptions`, marked tune-during-listen-only.
- Synchronous, no I/O, no logging in the hot loop beyond what the pipeline does (D3 < 50 ms budget). No server-side Python/Node (D1).
