using TelemetryGuard.RiskEngine.Contracts;
using TelemetryGuard.RiskEngine.Scoring;
using TelemetryGuard.Tests.Unit.TestSupport;

namespace TelemetryGuard.Tests.Unit.Scoring;

/// <summary>
/// Hand-rolled randomized property tests (fixed seeds, ≥ 1000 iterations, no FsCheck):
/// bounds, monotonicity of key fraud signals, and NaN-neutrality (NaN contributes
/// EXACTLY 0 — missing ≠ zero).
/// </summary>
public class HeuristicScorerPropertyTests
{
    private const int Iterations = 1000;

    private static HeuristicScorer CreateScorer() =>
        new(new TestOptionsMonitor<HeuristicWeights>(new HeuristicWeights()));

    // ---------------------------------------------------------------- random vector generator

    /// <summary>Per-field ~30 % probability of NaN/null; otherwise plausible values.
    /// IpReputationBad is a plain 0-when-cold float by contract (never NaN), like the
    /// velocity counters.</summary>
    private static FraudFeatureVector RandomVector(Random rng) => new()
    {
        // T1 bools (unused by the heuristic but part of the vector — randomized anyway):
        HoneypotTouched = NullableBool(rng),
        ClickBeforeRender = NullableBool(rng),
        PointerUntrusted = NullableBool(rng),
        WebdriverFlag = NullableBool(rng),
        HeadlessBrowser = NullableBool(rng),
        BeaconIntegrityOk = NullableBool(rng),
        IpTor = NullableBool(rng),
        TlsUaMismatch = NullableBool(rng),
        IpDatacenterAsn = NullableBool(rng),
        UaOsMismatch = NullableBool(rng),
        ClickIdInvalid = NullableBool(rng),
        MousePathLinearity = MaybeNaN(rng, 0f, 1f),
        StdInterEventMs = MaybeNaN(rng, 0f, 500f),
        // T2:
        HasJsBeacon = rng.NextDouble() < 0.5,
        EmulatorOrVm = NullableBool(rng),
        ScreenResAnomalous = NullableBool(rng),
        StorageAgeZeroRepeat = MaybeNaN(rng, 0f, 20f),
        IpProxyOrVpn = NullableBool(rng),
        IpGeoTargetMismatch = NullableBool(rng),
        TimeOnPageSec = MaybeNaN(rng, 0f, 120f),
        FormFillTimeSec = MaybeNaN(rng, 0f, 60f),
        FirstInteractionDelayMs = MaybeNaN(rng, 0f, 5000f),
        InputModalityMismatch = NullableBool(rng),
        MeanInterEventMs = MaybeNaN(rng, 0f, 500f),
        IpClicksLastMin = rng.Next(0, 101),
        DeviceSessionsLastHour = rng.Next(0, 51),
        IpDistinctUasLastHour = rng.Next(0, 21),
        DeviceIdsThisIpHour = rng.Next(0, 51),
        // T3:
        CookiesDisabled = NullableBool(rng),
        CanvasFpBlocked = NullableBool(rng),
        TimezoneIpMismatch = NullableBool(rng),
        LanguageGeoMismatch = NullableBool(rng),
        IpReputationBad = (float)rng.NextDouble(),
        PasteInIdentityFields = NullableBool(rng),
        ReferrerMissing = rng.NextDouble() < 0.5,
        InputEventCount = MaybeNaN(rng, 0f, 200f),
        ScrollEvents = MaybeNaN(rng, 0f, 50f),
        PagesViewed = MaybeNaN(rng, 0f, 20f),
        // CTX:
        IsMobile = NullableBool(rng),
        AsnType = (AsnType)rng.Next(0, 8),
        IsPrivateRelay = rng.NextDouble() < 0.5,
        FormSubmitted = NullableBool(rng),
        AutofillDetected = NullableBool(rng),
        IsPaidClick = rng.NextDouble() < 0.5,
        ChallengeOutcome = (ChallengeOutcome)rng.Next(0, 3),
    };

    private static bool? NullableBool(Random rng) =>
        rng.NextDouble() < 0.3 ? null : rng.NextDouble() < 0.5;

    private static float MaybeNaN(Random rng, float min, float max) =>
        rng.NextDouble() < 0.3 ? float.NaN : min + (float)rng.NextDouble() * (max - min);

    // ---------------------------------------------------------------- bounds

    [Fact]
    public void Score_is_always_within_0_and_100_with_empty_rule_hits()
    {
        var scorer = CreateScorer();
        var rng = new Random(42);

        for (var i = 0; i < Iterations; i++)
        {
            var result = scorer.Score(RandomVector(rng));

            Assert.InRange(result.Score, 0, 100);
            Assert.Empty(result.RuleHits);
        }
    }

    // ---------------------------------------------------------------- monotonicity

    [Fact]
    public void Increasing_IpClicksLastMin_never_lowers_the_score()
    {
        var scorer = CreateScorer();
        var rng = new Random(1001);

        for (var i = 0; i < Iterations; i++)
        {
            var v = RandomVector(rng);
            var k = rng.Next(0, 101);

            Assert.True(scorer.Score(v with { IpClicksLastMin = v.IpClicksLastMin + k }).Score
                        >= scorer.Score(v).Score);
        }
    }

    [Fact]
    public void IpProxyOrVpn_true_never_scores_below_null()
    {
        var scorer = CreateScorer();
        var rng = new Random(1002);

        for (var i = 0; i < Iterations; i++)
        {
            var v = RandomVector(rng);

            Assert.True(scorer.Score(v with { IpProxyOrVpn = true }).Score
                        >= scorer.Score(v with { IpProxyOrVpn = null }).Score);
        }
    }

    [Fact]
    public void EmulatorOrVm_true_never_scores_below_null()
    {
        var scorer = CreateScorer();
        var rng = new Random(1003);

        for (var i = 0; i < Iterations; i++)
        {
            var v = RandomVector(rng);

            Assert.True(scorer.Score(v with { EmulatorOrVm = true }).Score
                        >= scorer.Score(v with { EmulatorOrVm = null }).Score);
        }
    }

    [Fact]
    public void Increasing_DeviceIdsThisIpHour_never_lowers_the_score()
    {
        var scorer = CreateScorer();
        var rng = new Random(1004);

        for (var i = 0; i < Iterations; i++)
        {
            var v = RandomVector(rng);
            var k = rng.Next(0, 101);

            Assert.True(scorer.Score(v with { DeviceIdsThisIpHour = v.DeviceIdsThisIpHour + k }).Score
                        >= scorer.Score(v).Score);
        }
    }

    // ---------------------------------------------------------------- NaN-neutrality

    /// <summary>Floats whose contribution is only ever POSITIVE evidence (or unused): dropping
    /// them to NaN must never RAISE the score. Floats that feed negative evidence
    /// (TimeOnPageSec, FormFillTimeSec, MousePathLinearity, InputEventCount) are excluded
    /// here BY DESIGN: losing humanity credit legitimately raises the score (a no-beacon
    /// session gets no long-dwell/human-mouse credit); they are covered exactly by the
    /// neutral-value equivalence property below and the inverse check after that.</summary>
    private static readonly (string Name, Func<FraudFeatureVector, FraudFeatureVector> ToNaN)[]
        PositiveOnlyFloats =
        [
            ("StorageAgeZeroRepeat", v => v with { StorageAgeZeroRepeat = float.NaN }),
            ("FirstInteractionDelayMs", v => v with { FirstInteractionDelayMs = float.NaN }),
            ("StdInterEventMs", v => v with { StdInterEventMs = float.NaN }),
            ("MeanInterEventMs", v => v with { MeanInterEventMs = float.NaN }),
            ("ScrollEvents", v => v with { ScrollEvents = float.NaN }),
            ("PagesViewed", v => v with { PagesViewed = float.NaN }),
        ];

    /// <summary>Floats used only as NEGATIVE evidence (or its gate): dropping them to NaN
    /// removes a subtraction and must never LOWER the score.</summary>
    private static readonly (string Name, Func<FraudFeatureVector, FraudFeatureVector> ToNaN)[]
        NegativeOnlyFloats =
        [
            ("MousePathLinearity", v => v with { MousePathLinearity = float.NaN }),
            ("InputEventCount", v => v with { InputEventCount = float.NaN }),
        ];

    /// <summary>For EVERY float the scorer reads: NaN must be indistinguishable from a value
    /// that fires no term at all (default weights) — i.e. NaN contributes EXACTLY 0.</summary>
    private static readonly (string Name,
        Func<FraudFeatureVector, FraudFeatureVector> ToNaN,
        Func<FraudFeatureVector, FraudFeatureVector> ToNeutral)[] AllScoredFloats =
        [
            ("StorageAgeZeroRepeat",
                v => v with { StorageAgeZeroRepeat = float.NaN },
                v => v with { StorageAgeZeroRepeat = 1f }),           // count 1 = innocent first visit
            ("TimeOnPageSec",
                v => v with { TimeOnPageSec = float.NaN },
                v => v with { TimeOnPageSec = 10f }),                 // not short (< 2), not long (> 30)
            ("FormFillTimeSec",
                v => v with { FormFillTimeSec = float.NaN },
                v => v with { FormFillTimeSec = 4f }),                // not fast (< 3), not plausible (>= 5)
            ("FirstInteractionDelayMs",
                v => v with { FirstInteractionDelayMs = float.NaN },
                v => v with { FirstInteractionDelayMs = 500f }),      // not instant (< 100)
            ("MousePathLinearity",
                v => v with { MousePathLinearity = float.NaN },
                v => v with { MousePathLinearity = 0.95f }),          // outside human band [0.2, 0.85]
            ("InputEventCount",
                v => v with { InputEventCount = float.NaN },
                v => v with { InputEventCount = 10f }),               // below the >= 20 evidence gate
        ];

    [Fact]
    public void NaN_never_raises_the_score_for_positive_evidence_floats()
    {
        var scorer = CreateScorer();
        var rng = new Random(2001);

        for (var i = 0; i < Iterations; i++)
        {
            var v = RandomVector(rng);
            var baseline = scorer.Score(v).Score;

            foreach (var (name, toNaN) in PositiveOnlyFloats)
            {
                var withNaN = scorer.Score(toNaN(v)).Score;
                Assert.True(withNaN <= baseline,
                    $"{name}=NaN raised score {baseline} -> {withNaN} (iteration {i})");
            }
        }
    }

    [Fact]
    public void NaN_never_lowers_the_score_for_negative_evidence_floats()
    {
        var scorer = CreateScorer();
        var rng = new Random(2002);

        for (var i = 0; i < Iterations; i++)
        {
            var v = RandomVector(rng);
            var baseline = scorer.Score(v).Score;

            foreach (var (name, toNaN) in NegativeOnlyFloats)
            {
                var withNaN = scorer.Score(toNaN(v)).Score;
                Assert.True(withNaN >= baseline,
                    $"{name}=NaN lowered score {baseline} -> {withNaN} (iteration {i})");
            }
        }
    }

    [Fact]
    public void NaN_is_equivalent_to_a_term_neutral_value_for_every_scored_float()
    {
        // The strongest form of NaN-neutrality: NaN contributes EXACTLY 0 — the same score
        // as a real value that triggers no positive and no negative term.
        var scorer = CreateScorer();
        var rng = new Random(2003);

        for (var i = 0; i < Iterations; i++)
        {
            var v = RandomVector(rng);

            foreach (var (name, toNaN, toNeutral) in AllScoredFloats)
            {
                var withNaN = scorer.Score(toNaN(v)).Score;
                var withNeutral = scorer.Score(toNeutral(v)).Score;
                Assert.True(withNaN == withNeutral,
                    $"{name}: NaN scored {withNaN} but neutral value scored {withNeutral} (iteration {i})");
            }
        }
    }
}
