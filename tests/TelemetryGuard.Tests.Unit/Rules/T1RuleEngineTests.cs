using Microsoft.Extensions.Options;
using TelemetryGuard.RiskEngine.Contracts;
using TelemetryGuard.RiskEngine.Rules;

namespace TelemetryGuard.Tests.Unit.Rules;

public class T1RuleEngineTests
{
    /// <summary>Known floors for every rule under DEFAULT options — used to assert the
    /// max-fold invariant (Floor == max of fired rules' floors).</summary>
    private static readonly IReadOnlyDictionary<string, int> DefaultFloors = new Dictionary<string, int>
    {
        ["honeypot_touched"] = 95,
        ["click_before_render"] = 90,
        ["pointer_untrusted"] = 90,
        ["webdriver_flag"] = 85,
        ["headless_browser"] = 85,
        ["beacon_integrity_failed"] = 85,
        ["ip_tor_paid"] = 80,
        ["tls_ua_mismatch"] = 80,
        ["ip_datacenter_paid"] = 70,
        ["ip_click_flood"] = 75,
        ["ua_os_mismatch"] = 71,
        ["linear_mouse_path"] = 85,
        ["robotic_cadence"] = 85,
        ["click_id_invalid"] = 71,
    };

    private static T1RuleEngine CreateEngine(RulesOptions? options = null) =>
        new(new StaticOptionsMonitor(options ?? new RulesOptions()));

    // ---------------------------------------------------------------- per-rule (14 rules)

    public static TheoryData<string, FraudFeatureVector, int> SingleRuleCases() => new()
    {
        { "honeypot_touched", new FraudFeatureVector { HoneypotTouched = true }, 95 },
        { "click_before_render", new FraudFeatureVector { ClickBeforeRender = true }, 90 },
        { "pointer_untrusted", new FraudFeatureVector { PointerUntrusted = true }, 90 },
        { "webdriver_flag", new FraudFeatureVector { WebdriverFlag = true }, 85 },
        { "headless_browser", new FraudFeatureVector { HeadlessBrowser = true }, 85 },
        { "beacon_integrity_failed", new FraudFeatureVector { BeaconIntegrityOk = false }, 85 },
        { "ip_tor_paid", new FraudFeatureVector { IpTor = true, IsPaidClick = true }, 80 },
        { "tls_ua_mismatch", new FraudFeatureVector { TlsUaMismatch = true }, 80 },
        { "ip_datacenter_paid", new FraudFeatureVector { IpDatacenterAsn = true, IsPaidClick = true }, 70 },
        { "ip_click_flood", new FraudFeatureVector { IpClicksLastMin = 30 }, 75 },
        { "ua_os_mismatch", new FraudFeatureVector { UaOsMismatch = true }, 71 },
        { "linear_mouse_path", new FraudFeatureVector { MousePathLinearity = 1.0f, InputEventCount = 20f }, 85 },
        { "robotic_cadence", new FraudFeatureVector { StdInterEventMs = 0.5f, InputEventCount = 10f }, 85 },
        { "click_id_invalid", new FraudFeatureVector { ClickIdInvalid = true }, 71 },
    };

    [Theory]
    [MemberData(nameof(SingleRuleCases))]
    public void Each_rule_fires_alone_with_its_floor(string expectedHit, FraudFeatureVector vector, int expectedFloor)
    {
        var result = CreateEngine().Evaluate(vector);

        Assert.Equal(new[] { expectedHit }, result.Hits);
        Assert.Equal(expectedFloor, result.Floor);
    }

    // ---------------------------------------------------------------- absence never fires

    [Fact]
    public void Default_vector_all_signals_absent_fires_nothing()
    {
        // The single most important test: NaN floats, null bools, zero velocity — no rule fires.
        var result = CreateEngine().Evaluate(new FraudFeatureVector());

        Assert.Null(result.Floor);
        Assert.Empty(result.Hits);
        Assert.Same(T1Result.None, result);
    }

    // ---------------------------------------------------------------- paid-only gating

    [Fact]
    public void IpTor_without_paid_click_does_not_fire()
    {
        var result = CreateEngine().Evaluate(new FraudFeatureVector { IpTor = true, IsPaidClick = false });

        Assert.Null(result.Floor);
        Assert.Empty(result.Hits);
    }

    [Fact]
    public void IpDatacenterAsn_without_paid_click_does_not_fire()
    {
        var result = CreateEngine().Evaluate(new FraudFeatureVector { IpDatacenterAsn = true, IsPaidClick = false });

        Assert.Null(result.Floor);
        Assert.Empty(result.Hits);
    }

    // ---------------------------------------------------------------- beacon integrity semantics

    [Fact]
    public void BeaconIntegrityOk_null_no_beacon_does_not_fire()
    {
        var result = CreateEngine().Evaluate(new FraudFeatureVector { BeaconIntegrityOk = null });

        Assert.Null(result.Floor);
        Assert.Empty(result.Hits);
    }

    [Fact]
    public void BeaconIntegrityOk_true_does_not_fire()
    {
        var result = CreateEngine().Evaluate(new FraudFeatureVector { BeaconIntegrityOk = true });

        Assert.Null(result.Floor);
        Assert.Empty(result.Hits);
    }

    [Fact]
    public void BeaconIntegrityOk_explicit_false_fires_at_85()
    {
        var result = CreateEngine().Evaluate(new FraudFeatureVector { BeaconIntegrityOk = false });

        Assert.Equal(new[] { "beacon_integrity_failed" }, result.Hits);
        Assert.Equal(85, result.Floor);
    }

    // ---------------------------------------------------------------- evidence gates

    [Fact]
    public void Linear_mouse_path_below_min_events_does_not_fire()
    {
        var result = CreateEngine().Evaluate(
            new FraudFeatureVector { MousePathLinearity = 1.0f, InputEventCount = 19f });

        Assert.Null(result.Floor);
        Assert.Empty(result.Hits);
    }

    [Fact]
    public void Linear_mouse_path_at_min_events_fires()
    {
        var result = CreateEngine().Evaluate(
            new FraudFeatureVector { MousePathLinearity = 1.0f, InputEventCount = 20f });

        Assert.Equal(new[] { "linear_mouse_path" }, result.Hits);
        Assert.Equal(85, result.Floor);
    }

    [Fact]
    public void Robotic_cadence_below_min_events_does_not_fire()
    {
        var result = CreateEngine().Evaluate(
            new FraudFeatureVector { StdInterEventMs = 0.5f, InputEventCount = 9f });

        Assert.Null(result.Floor);
        Assert.Empty(result.Hits);
    }

    [Fact]
    public void Robotic_cadence_at_min_events_fires()
    {
        var result = CreateEngine().Evaluate(
            new FraudFeatureVector { StdInterEventMs = 0.5f, InputEventCount = 10f });

        Assert.Equal(new[] { "robotic_cadence" }, result.Hits);
        Assert.Equal(85, result.Floor);
    }

    [Fact]
    public void NaN_linearity_with_high_event_count_does_not_fire()
    {
        // NaN metric + plenty of events: absence of the derived signal must not fire.
        var result = CreateEngine().Evaluate(new FraudFeatureVector { InputEventCount = 100f });

        Assert.Null(result.Floor);
        Assert.Empty(result.Hits);
    }

    // ---------------------------------------------------------------- max-fold

    [Fact]
    public void Multiple_hits_fold_floor_with_max()
    {
        var vector = new FraudFeatureVector
        {
            HoneypotTouched = true,
            IpDatacenterAsn = true,
            IsPaidClick = true,
        };

        var result = CreateEngine().Evaluate(vector);

        Assert.Equal(95, result.Floor);
        Assert.Contains("honeypot_touched", result.Hits);
        Assert.Contains("ip_datacenter_paid", result.Hits);
        Assert.Equal(2, result.Hits.Count);
    }

    [Fact]
    public void Floor_always_equals_max_of_fired_rules_floors()
    {
        // Rules only raise: fold is Math.Max over fired floors, never anything lower.
        var vector = new FraudFeatureVector
        {
            IpDatacenterAsn = true,     // 70
            IsPaidClick = true,
            UaOsMismatch = true,        // 71
            TlsUaMismatch = true,       // 80
            IpClicksLastMin = 30,       // 75
            ClickIdInvalid = true,      // 71
        };

        var result = CreateEngine().Evaluate(vector);

        Assert.NotEmpty(result.Hits);
        Assert.Equal(result.Hits.Select(h => DefaultFloors[h]).Max(), result.Floor);
        Assert.Equal(80, result.Floor);
    }

    [Fact]
    public void Hits_are_reported_in_evaluation_order()
    {
        var vector = new FraudFeatureVector
        {
            HoneypotTouched = true,
            IpDatacenterAsn = true,
            IsPaidClick = true,
            ClickIdInvalid = true,
        };

        var result = CreateEngine().Evaluate(vector);

        Assert.Equal(new[] { "honeypot_touched", "ip_datacenter_paid", "click_id_invalid" }, result.Hits);
    }

    // ---------------------------------------------------------------- options respected

    [Fact]
    public void Overridden_flood_threshold_is_respected()
    {
        var engine = CreateEngine(new RulesOptions { IpClicksLastMinThreshold = 5 });

        var result = engine.Evaluate(new FraudFeatureVector { IpClicksLastMin = 5 });

        Assert.Equal(new[] { "ip_click_flood" }, result.Hits);
        Assert.Equal(75, result.Floor);
    }

    [Fact]
    public void Default_flood_threshold_does_not_fire_below_30()
    {
        var result = CreateEngine().Evaluate(new FraudFeatureVector { IpClicksLastMin = 29 });

        Assert.Null(result.Floor);
        Assert.Empty(result.Hits);
    }

    [Fact]
    public void Overridden_floor_is_respected()
    {
        var engine = CreateEngine(new RulesOptions { ClickIdInvalidFloor = 80 });

        var result = engine.Evaluate(new FraudFeatureVector { ClickIdInvalid = true });

        Assert.Equal(new[] { "click_id_invalid" }, result.Hits);
        Assert.Equal(80, result.Floor);
    }

    // ---------------------------------------------------------------- test double

    private sealed class StaticOptionsMonitor : IOptionsMonitor<RulesOptions>
    {
        public StaticOptionsMonitor(RulesOptions value) => CurrentValue = value;

        public RulesOptions CurrentValue { get; }

        public RulesOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<RulesOptions, string?> listener) => null;
    }
}
