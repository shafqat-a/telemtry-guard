using TelemetryGuard.Training;

namespace TelemetryGuard.Tests.Unit.Training;

/// <summary>P2-02 acceptance: PromotionGate is pure (no I/O) — every case here is a
/// plain in-memory record comparison.</summary>
public sealed class PromotionGateTests
{
    private static readonly TrainGateOptions DefaultTrainOptions =
        new(MaxAucRegression: 0.0, MaxAuprcRegression: 0.01, MaxScoreP99Ms: 5.0, MinPositives: 10, MinNegatives: 10);

    private static CandidateMetrics Candidate(
        double auc = 0.9, double auprc = 0.8, double f1 = 0.75, double? p99 = 1.0, int positives = 100, int negatives = 100)
        => new(auc, auprc, f1, p99, positives, negatives);

    [Fact]
    public void EvaluateCandidate_EqualToIncumbent_PassesAtZeroTolerance()
    {
        var incumbent = new IncumbentMetrics("lgbm-old", Auc: 0.9, Auprc: 0.8);
        var decision = PromotionGate.EvaluateCandidate(Candidate(auc: 0.9, auprc: 0.8), incumbent, DefaultTrainOptions);

        Assert.True(decision.Passed);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public void EvaluateCandidate_AucWorseThanIncumbent_FailsWithReasonNamingAuc()
    {
        var incumbent = new IncumbentMetrics("lgbm-old", Auc: 0.90, Auprc: 0.8);
        var decision = PromotionGate.EvaluateCandidate(Candidate(auc: 0.89, auprc: 0.8), incumbent, DefaultTrainOptions);

        Assert.False(decision.Passed);
        Assert.Contains(decision.Reasons, r => r.Contains("AUC", StringComparison.Ordinal));
    }

    [Fact]
    public void EvaluateCandidate_AuprcRegressionInsideTolerance_Passes()
    {
        // MaxAuprcRegression = 0.01; incumbent 0.80 -> candidate 0.795 is a 0.005 regression.
        var incumbent = new IncumbentMetrics("lgbm-old", Auc: 0.9, Auprc: 0.80);
        var decision = PromotionGate.EvaluateCandidate(
            Candidate(auc: 0.9, auprc: 0.795), incumbent, DefaultTrainOptions);

        Assert.True(decision.Passed);
    }

    [Fact]
    public void EvaluateCandidate_AuprcRegressionBeyondTolerance_FailsWithReasonNamingAuprc()
    {
        var incumbent = new IncumbentMetrics("lgbm-old", Auc: 0.9, Auprc: 0.80);
        var decision = PromotionGate.EvaluateCandidate(
            Candidate(auc: 0.9, auprc: 0.78), incumbent, DefaultTrainOptions);

        Assert.False(decision.Passed);
        Assert.Contains(decision.Reasons, r => r.Contains("AUPRC", StringComparison.Ordinal));
    }

    [Fact]
    public void EvaluateCandidate_ScoreP99AboveMax_Fails()
    {
        var decision = PromotionGate.EvaluateCandidate(
            Candidate(p99: 10.0), incumbent: null, DefaultTrainOptions);

        Assert.False(decision.Passed);
        Assert.Contains(decision.Reasons, r => r.Contains("ScoreP99Ms", StringComparison.Ordinal));
    }

    [Fact]
    public void EvaluateCandidate_TooFewPositivesOrNegatives_Fails()
    {
        var decision = PromotionGate.EvaluateCandidate(
            Candidate(positives: 1, negatives: 1), incumbent: null, DefaultTrainOptions);

        Assert.False(decision.Passed);
        Assert.Contains(decision.Reasons, r => r.Contains("positives", StringComparison.Ordinal));
        Assert.Contains(decision.Reasons, r => r.Contains("negatives", StringComparison.Ordinal));
    }

    [Fact]
    public void EvaluateCandidate_NoIncumbent_OnlyLabelCountAndLatencyChecksApply()
    {
        // Wildly "bad" AUC/AUPRC would fail an incumbent-regression check, but with
        // no incumbent there is nothing to regress against — only label counts and
        // latency apply, and both pass here.
        var decision = PromotionGate.EvaluateCandidate(
            Candidate(auc: 0.51, auprc: 0.1, p99: 1.0, positives: 50, negatives: 50),
            incumbent: null, DefaultTrainOptions);

        Assert.True(decision.Passed);
    }

    [Fact]
    public void EvaluateCandidate_MultipleFailures_ListsEveryReason()
    {
        var incumbent = new IncumbentMetrics("lgbm-old", Auc: 0.95, Auprc: 0.95);
        var decision = PromotionGate.EvaluateCandidate(
            Candidate(auc: 0.5, auprc: 0.5, p99: 100.0, positives: 1, negatives: 1),
            incumbent, DefaultTrainOptions);

        Assert.False(decision.Passed);
        Assert.True(decision.Reasons.Count >= 4, $"Expected >= 4 reasons, got {decision.Reasons.Count}: {string.Join(" | ", decision.Reasons)}");
    }

    // ---- EvaluateEnforcePromotion ----

    private static readonly EnforceGateOptions DefaultEnforceOptions =
        new(MinShadowHours: 336, MinShadowSessions: 1000, MinLabeledShadowSessions: 200, MinLiveAucAdvantage: 0.0);

    private static ModelRegistryRow ShadowCandidate(DateTime? shadowSinceUtc) => new(
        ModelId: Guid.NewGuid(), ScorerVersion: "lgbm-candidate", FeatureSetVersion: 1, Status: ModelStatuses.Shadow,
        TrainedUtc: DateTime.UtcNow.AddDays(-20), WindowFromUtc: DateTime.UtcNow.AddDays(-50),
        WindowToUtc: DateTime.UtcNow.AddDays(-20), Positives: 500, Negatives: 500,
        Auc: 0.9, Auprc: 0.8, F1: 0.7, ScoreP99Ms: 1.0, ArtifactPath: "/tmp/x", RejectReason: null, Notes: null,
        ShadowSinceUtc: shadowSinceUtc, ActiveSinceUtc: null);

    private static DivergenceSummary Divergence(int sessions, int labeledSessions, double shadowAuc, double enforcedAuc) =>
        new("lgbm-candidate", sessions, RuleFlooredSessions: 0, labeledSessions, BandAgreementRate: 0.9,
            BandMatrix: new int[9], MeanAbsDelta: 1.0, ShadowWouldBlockMore: 0, ShadowWouldBlockFewer: 0,
            EnforcedAuc: enforcedAuc, ShadowAuc: shadowAuc);

    [Fact]
    public void EvaluateEnforcePromotion_ShadowAgeBelowThreshold_Fails()
    {
        var now = DateTime.UtcNow;
        var candidate = ShadowCandidate(now.AddHours(-10)); // far below 336h
        var divergence = Divergence(sessions: 2000, labeledSessions: 500, shadowAuc: 0.95, enforcedAuc: 0.8);

        var decision = PromotionGate.EvaluateEnforcePromotion(
            candidate, now, divergence, hasActiveIncumbent: false, DefaultEnforceOptions);

        Assert.False(decision.Passed);
        Assert.Contains(decision.Reasons, r => r.Contains("shadow age", StringComparison.Ordinal));
    }

    [Fact]
    public void EvaluateEnforcePromotion_EnoughHoursButTooFewSessions_Fails()
    {
        var now = DateTime.UtcNow;
        var candidate = ShadowCandidate(now.AddHours(-400)); // above 336h
        var divergence = Divergence(sessions: 10, labeledSessions: 500, shadowAuc: 0.95, enforcedAuc: 0.8);

        var decision = PromotionGate.EvaluateEnforcePromotion(
            candidate, now, divergence, hasActiveIncumbent: false, DefaultEnforceOptions);

        Assert.False(decision.Passed);
        Assert.Contains(decision.Reasons, r => r.Contains("shadow-scored sessions", StringComparison.Ordinal));
    }

    [Fact]
    public void EvaluateEnforcePromotion_EnoughOfBoth_ButLiveAucBelowAdvantage_Fails()
    {
        var now = DateTime.UtcNow;
        var candidate = ShadowCandidate(now.AddHours(-400));
        var divergence = Divergence(sessions: 2000, labeledSessions: 500, shadowAuc: 0.79, enforcedAuc: 0.8);

        var decision = PromotionGate.EvaluateEnforcePromotion(
            candidate, now, divergence, hasActiveIncumbent: false, DefaultEnforceOptions);

        Assert.False(decision.Passed);
        Assert.Contains(decision.Reasons, r => r.Contains("live shadow AUC advantage", StringComparison.Ordinal));
    }

    [Fact]
    public void EvaluateEnforcePromotion_EverythingSatisfied_Passes()
    {
        var now = DateTime.UtcNow;
        var candidate = ShadowCandidate(now.AddHours(-400));
        var divergence = Divergence(sessions: 2000, labeledSessions: 500, shadowAuc: 0.85, enforcedAuc: 0.8);

        var decision = PromotionGate.EvaluateEnforcePromotion(
            candidate, now, divergence, hasActiveIncumbent: false, DefaultEnforceOptions);

        Assert.True(decision.Passed);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public void EvaluateEnforcePromotion_Generation2WithActiveIncumbent_PassesOnOfflineComparisonAlone()
    {
        var now = DateTime.UtcNow;
        // No ShadowSinceUtc, no divergence — would fail path (a) entirely, but
        // hasActiveIncumbent = true short-circuits to the offline-comparison branch.
        var candidate = ShadowCandidate(shadowSinceUtc: null);

        var decision = PromotionGate.EvaluateEnforcePromotion(
            candidate, now, divergence: null, hasActiveIncumbent: true, DefaultEnforceOptions);

        Assert.True(decision.Passed);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public void EvaluateEnforcePromotion_MultipleFailures_ListsEveryReason()
    {
        var now = DateTime.UtcNow;
        var candidate = ShadowCandidate(now.AddHours(-10)); // shadow age fails
        var divergence = Divergence(sessions: 10, labeledSessions: 5, shadowAuc: 0.5, enforcedAuc: 0.8); // sessions + labeled fail

        var decision = PromotionGate.EvaluateEnforcePromotion(
            candidate, now, divergence, hasActiveIncumbent: false, DefaultEnforceOptions);

        Assert.False(decision.Passed);
        Assert.True(decision.Reasons.Count >= 3, $"Expected >= 3 reasons, got {decision.Reasons.Count}: {string.Join(" | ", decision.Reasons)}");
    }
}
