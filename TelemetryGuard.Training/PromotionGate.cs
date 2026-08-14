namespace TelemetryGuard.Training;

/// <summary>Candidate metrics as measured at registration time (`retrain`).</summary>
public sealed record CandidateMetrics(double Auc, double Auprc, double F1, double? ScoreP99Ms, int Positives, int Negatives);

/// <summary>The incumbent (currently active, else shadow) model's metrics, read from
/// <see cref="ModelRegistryClient.GetIncumbentAsync"/>.</summary>
public sealed record IncumbentMetrics(string ScorerVersion, double Auc, double Auprc);

public sealed record TrainGateOptions(
    double MaxAucRegression, double MaxAuprcRegression, double MaxScoreP99Ms, int MinPositives, int MinNegatives);

public sealed record EnforceGateOptions(
    int MinShadowHours, int MinShadowSessions, int MinLabeledShadowSessions, double MinLiveAucAdvantage);

public sealed record GateDecision(bool Passed, IReadOnlyList<string> Reasons);

/// <summary>
/// P2-02: pure, no-I/O promotion decisions. Two independent gates:
///  - <see cref="EvaluateCandidate"/> runs at registration time (`retrain`): a failure
///    registers the run as 'rejected' — it can never be promoted afterwards.
///  - <see cref="EvaluateEnforcePromotion"/> runs at `promote --status active`: it is
///    the ONLY code path that may let a model start enforcing (D18 — promotion is
///    always a human decision via the CLI, never automatic).
/// Every reason is listed, not just the first unmet one, so the CLI can print the
/// complete picture in one shot.
/// </summary>
public static class PromotionGate
{
    /// <summary>Run at REGISTRATION time (`retrain`). A failure registers the run as
    /// 'rejected' — it can never be promoted afterwards. Checks: label counts per class;
    /// AUC/AUPRC vs the incumbent within the configured regression tolerance (no
    /// incumbent = only the absolute Training:MinAuc gate, already applied by
    /// Trainer.TrainAndExport); measured single-row Predict p99 under
    /// Training:Promotion:MaxScoreP99Ms (D3 — a model too slow to serve is not a model).</summary>
    public static GateDecision EvaluateCandidate(
        CandidateMetrics candidate, IncumbentMetrics? incumbent, TrainGateOptions options)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(options);

        var reasons = new List<string>();

        if (candidate.Positives < options.MinPositives)
        {
            reasons.Add($"positives {candidate.Positives} < Training:Retrain:MinPositives {options.MinPositives}.");
        }
        if (candidate.Negatives < options.MinNegatives)
        {
            reasons.Add($"negatives {candidate.Negatives} < Training:Retrain:MinNegatives {options.MinNegatives}.");
        }

        if (candidate.ScoreP99Ms is { } p99 && p99 > options.MaxScoreP99Ms)
        {
            reasons.Add($"ScoreP99Ms {p99:F3} > Training:Promotion:MaxScoreP99Ms {options.MaxScoreP99Ms:F3} (D3).");
        }

        if (incumbent is not null)
        {
            var aucRegression = incumbent.Auc - candidate.Auc;
            if (aucRegression > options.MaxAucRegression)
            {
                reasons.Add(
                    $"AUC {candidate.Auc:F4} is {aucRegression:F4} below incumbent {incumbent.ScorerVersion}'s " +
                    $"{incumbent.Auc:F4} — exceeds Training:Promotion:MaxAucRegression {options.MaxAucRegression:F4}.");
            }

            var auprcRegression = incumbent.Auprc - candidate.Auprc;
            if (auprcRegression > options.MaxAuprcRegression)
            {
                reasons.Add(
                    $"AUPRC {candidate.Auprc:F4} is {auprcRegression:F4} below incumbent {incumbent.ScorerVersion}'s " +
                    $"{incumbent.Auprc:F4} — exceeds Training:Promotion:MaxAuprcRegression {options.MaxAuprcRegression:F4}.");
            }
        }

        return new GateDecision(reasons.Count == 0, reasons);
    }

    /// <summary>Run at `promote --status active`. Passes when EITHER
    /// (a) the model has been 'shadow' for &gt;= MinShadowHours with &gt;= MinShadowSessions
    ///     shadow-scored sessions AND, on the labeled subset (&gt;= MinLabeledShadowSessions),
    ///     its live rank-AUC beats the enforced heuristic's by &gt;= MinLiveAucAdvantage
    ///     (the §11.4 listen-only pilot path); OR
    /// (b) an ACTIVE model incumbent already exists and this candidate beat it on the
    ///     offline holdout at registration time (generation 2+, where live shadow
    ///     evaluation is impossible — only one model loads per process).
    /// Reasons always list every unmet condition so the CLI can print them verbatim.</summary>
    public static GateDecision EvaluateEnforcePromotion(
        ModelRegistryRow candidate, DateTime nowUtc, DivergenceSummary? divergence,
        bool hasActiveIncumbent, EnforceGateOptions options)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(options);

        // (b) generation 2+: this candidate already beat the active incumbent on the
        // offline holdout at registration time (EvaluateCandidate) — live shadow
        // evaluation is impossible while another model is enforcing (one model per
        // process, P2-02 step 4), so the offline comparison stands alone.
        if (hasActiveIncumbent)
        {
            return new GateDecision(true, []);
        }

        // (a) §11.4 listen-only pilot path.
        var reasons = new List<string>();

        if (candidate.ShadowSinceUtc is not { } shadowSince)
        {
            reasons.Add("model has never been promoted to 'shadow' (no ShadowSinceUtc) — nothing to evaluate live.");
        }
        else
        {
            var shadowHours = (nowUtc - shadowSince).TotalHours;
            if (shadowHours < options.MinShadowHours)
            {
                reasons.Add(
                    $"shadow age {shadowHours:F1}h < Training:Promotion:MinShadowHours {options.MinShadowHours}.");
            }
        }

        if (divergence is null)
        {
            reasons.Add("no divergence summary available — run `divergence` for this model before promoting to active.");
        }
        else
        {
            if (divergence.Sessions < options.MinShadowSessions)
            {
                reasons.Add(
                    $"shadow-scored sessions {divergence.Sessions} < Training:Promotion:MinShadowSessions {options.MinShadowSessions}.");
            }

            if (divergence.LabeledSessions < options.MinLabeledShadowSessions)
            {
                reasons.Add(
                    $"labeled shadow sessions {divergence.LabeledSessions} < " +
                    $"Training:Promotion:MinLabeledShadowSessions {options.MinLabeledShadowSessions}.");
            }
            else
            {
                var advantage = divergence.ShadowAuc - divergence.EnforcedAuc;
                if (double.IsNaN(advantage) || advantage < options.MinLiveAucAdvantage)
                {
                    reasons.Add(
                        $"live shadow AUC advantage {advantage:F4} < Training:Promotion:MinLiveAucAdvantage " +
                        $"{options.MinLiveAucAdvantage:F4} (shadow AUC {divergence.ShadowAuc:F4}, enforced AUC " +
                        $"{divergence.EnforcedAuc:F4}).");
                }
            }
        }

        return new GateDecision(reasons.Count == 0, reasons);
    }
}
