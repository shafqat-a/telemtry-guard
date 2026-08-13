using TelemetryGuard.RiskEngine.Contracts;
using TelemetryGuard.RiskEngine.Scoring;
using TelemetryGuard.Tests.Unit.TestSupport;

namespace TelemetryGuard.Tests.Unit.Scoring;

/// <summary>Deterministic example tests for the MVP heuristic scorer (RSK-06) under
/// DEFAULT weights unless a test stubs its own.</summary>
public class HeuristicScorerTests
{
    private static HeuristicScorer CreateScorer(HeuristicWeights? weights = null) =>
        new(new TestOptionsMonitor<HeuristicWeights>(weights ?? new HeuristicWeights()));

    // ---------------------------------------------------------------- identity/contract

    [Fact]
    public void ScorerVersion_is_heuristic_1()
    {
        Assert.Equal("heuristic-1", CreateScorer().ScorerVersion);
    }

    [Fact]
    public void Result_carries_feature_set_version_1_empty_rule_hits_and_scorer_version()
    {
        var result = CreateScorer().Score(new FraudFeatureVector { HasJsBeacon = true });

        Assert.Equal(FraudFeatureVector.FeatureSetVersion, result.FeatureSetVersion);
        Assert.Equal(1, result.FeatureSetVersion);
        Assert.Empty(result.RuleHits);
        Assert.Equal("heuristic-1", result.ScorerVersion);
    }

    // ---------------------------------------------------------------- baseline / defaults

    [Fact]
    public void Clean_beaconed_session_with_cold_velocity_scores_zero()
    {
        var result = CreateScorer().Score(new FraudFeatureVector { HasJsBeacon = true });

        Assert.Equal(0, result.Score);
    }

    [Fact]
    public void All_defaults_vector_scores_exactly_NoJsBeacon_weight()
    {
        // Default vector: HasJsBeacon == false (+15), every other float is NaN, every bool?
        // is null, velocity is cold — proves NaN/null contribute 0 across every guarded branch.
        var result = CreateScorer().Score(new FraudFeatureVector());

        Assert.Equal(15, result.Score);
    }

    // ---------------------------------------------------------------- deterministic examples

    [Fact]
    public void NoBeacon_proxy_and_full_click_ramp_scores_65_challenge_band()
    {
        var vector = new FraudFeatureVector
        {
            HasJsBeacon = false,   // +15
            IpProxyOrVpn = true,   // +20
            IpClicksLastMin = 30,  // min(30,30)/30 * 30 = +30
        };

        var result = CreateScorer().Score(vector);

        Assert.Equal(65, result.Score);
        Assert.Equal(VerdictBand.Challenge, BandMapper.ToBand(result.Score));
    }

    [Fact]
    public void Emulator_screen_anomaly_and_wiped_storage_scores_60()
    {
        var vector = new FraudFeatureVector
        {
            HasJsBeacon = true,
            EmulatorOrVm = true,        // +25
            ScreenResAnomalous = true,  // +10
            StorageAgeZeroRepeat = 6,   // min((6-1)*5, 25) = +25
        };

        Assert.Equal(60, CreateScorer().Score(vector).Score);
    }

    // ---------------------------------------------------------------- T3 joint cap

    private static FraudFeatureVector AllSevenT3Firing() => new()
    {
        HasJsBeacon = true,
        CookiesDisabled = true,       // 3
        CanvasFpBlocked = true,       // 2
        TimezoneIpMismatch = true,    // 4
        LanguageGeoMismatch = true,   // 3
        IpReputationBad = 1f,         // 1 * IpReputationBadMax (0 by default — no producer exists)
        PasteInIdentityFields = true, // 2
        ReferrerMissing = true,       // 2
    };

    [Fact]
    public void All_seven_T3_signals_cap_at_T3TotalCap_with_default_weights()
    {
        // Six non-reputation defaults sum to 16; IpReputationBadMax defaults to 0.
        // The joint cap binds: exactly 15, not 16.
        Assert.Equal(15, CreateScorer().Score(AllSevenT3Firing()).Score);
    }

    [Fact]
    public void All_seven_T3_signals_still_cap_at_15_when_reputation_weight_raised()
    {
        // With IpReputationBadMax = 5 the raw T3 sum is 21 — the cap binds either way.
        var scorer = CreateScorer(new HeuristicWeights { IpReputationBadMax = 5 });

        Assert.Equal(15, scorer.Score(AllSevenT3Firing()).Score);
    }

    // ---------------------------------------------------------------- carrier-NAT normalization

    [Fact]
    public void Eight_device_ids_on_mobile_asn_contribute_zero()
    {
        var vector = new FraudFeatureVector
        {
            HasJsBeacon = true,
            DeviceIdsThisIpHour = 8,
            AsnType = AsnType.Mobile, // threshold 10: 8 <= 10 → 0 (carrier-grade NAT expected)
        };

        Assert.Equal(0, CreateScorer().Score(vector).Score);
    }

    [Fact]
    public void Eight_device_ids_on_datacenter_asn_contribute_nine()
    {
        var vector = new FraudFeatureVector
        {
            HasJsBeacon = true,
            DeviceIdsThisIpHour = 8,
            AsnType = AsnType.Datacenter, // threshold 5: (8-5)*3 = 9, under cap 20
        };

        Assert.Equal(9, CreateScorer().Score(vector).Score);
    }

    // ---------------------------------------------------------------- challenge outcome (CTX)

    [Fact]
    public void Passed_challenge_outweighs_proxy_and_clamps_at_zero()
    {
        var vector = new FraudFeatureVector
        {
            HasJsBeacon = true,
            IpProxyOrVpn = true,                        // +20
            ChallengeOutcome = ChallengeOutcome.Passed, // -30 → -10 pre-clamp
        };

        Assert.Equal(0, CreateScorer().Score(vector).Score);
    }

    [Fact]
    public void Failed_challenge_alone_on_beaconed_session_scores_40()
    {
        var vector = new FraudFeatureVector
        {
            HasJsBeacon = true,
            ChallengeOutcome = ChallengeOutcome.Failed, // +40
        };

        Assert.Equal(40, CreateScorer().Score(vector).Score);
    }

    // ---------------------------------------------------------------- weights tunability

    [Fact]
    public void Stubbed_NoJsBeacon_weight_of_50_yields_50_for_no_beacon_vector()
    {
        var scorer = CreateScorer(new HeuristicWeights { NoJsBeacon = 50 });

        Assert.Equal(50, scorer.Score(new FraudFeatureVector()).Score);
    }

    [Fact]
    public void Weight_change_applies_to_existing_scorer_instance_without_reregistration()
    {
        // IOptionsMonitor.CurrentValue is read per Score call — a config edit retunes the
        // singleton scorer live, no rebuild/re-registration.
        var monitor = new TestOptionsMonitor<HeuristicWeights>(new HeuristicWeights());
        var scorer = new HeuristicScorer(monitor);
        var noBeacon = new FraudFeatureVector();

        Assert.Equal(15, scorer.Score(noBeacon).Score);

        monitor.CurrentValue = new HeuristicWeights { NoJsBeacon = 50 };

        Assert.Equal(50, scorer.Score(noBeacon).Score);
    }
}
