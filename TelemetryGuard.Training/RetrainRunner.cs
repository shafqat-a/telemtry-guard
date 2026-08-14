using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.ML;
using Microsoft.ML.Data;
using TelemetryGuard.RiskEngine.Contracts;

namespace TelemetryGuard.Training;

/// <summary>
/// P2-02 `retrain` command: rebuilds labels, retrains over the newest window, measures
/// serving latency, compares the candidate against the incumbent, and registers the
/// outcome — `candidate` on pass, `rejected` (never servable) on fail. NEVER promotes
/// (not even to `shadow`) — see this file's guardrail note above InsertRunAsync calls.
///
/// Public (not internal): TelemetryGuard.Training.csproj deliberately carries no
/// InternalsVisibleTo (would expose this project's top-level-statement Program type to
/// the test assemblies, colliding with TelemetryGuard.Api's
/// WebApplicationFactory&lt;Program&gt; — CS0433). RetrainCycleTests therefore calls
/// <see cref="RunAsync"/> in-process, never by shelling out to `dotnet run`.
/// </summary>
public static class RetrainRunner
{
    public static async Task<int> RunAsync(string[] args, IConfiguration config, CancellationToken ct)
    {
        var (fromArg, toArg, outDirArg, skipLabelBuild, force) = ParseArgs(args);

        var clickHouseCs = RequireConnectionString(config, "ClickHouse");
        var sqlCs = RequireConnectionString(config, "Main");
        var minAuc = config.GetValue<double?>("Training:MinAuc") ?? 0.85;
        var t1Weight = config.GetValue<float?>("Training:T1PositiveWeight") ?? 0.6f;
        var artifactRoot = outDirArg ?? config["Training:ArtifactRoot"] ?? "artifacts/models";

        var windowDays = config.GetValue<int?>("Training:Retrain:WindowDays") ?? 30;
        var minPositives = config.GetValue<int?>("Training:Retrain:MinPositives") ?? 500;
        var minNegatives = config.GetValue<int?>("Training:Retrain:MinNegatives") ?? 500;
        var minIntervalHours = config.GetValue<int?>("Training:Retrain:MinIntervalHours") ?? 144;

        var promotionOptions = new TrainGateOptions(
            MaxAucRegression: config.GetValue<double?>("Training:Promotion:MaxAucRegression") ?? 0.0,
            MaxAuprcRegression: config.GetValue<double?>("Training:Promotion:MaxAuprcRegression") ?? 0.01,
            MaxScoreP99Ms: config.GetValue<double?>("Training:Promotion:MaxScoreP99Ms") ?? 5.0,
            MinPositives: minPositives,
            MinNegatives: minNegatives);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = fromArg ?? today.AddDays(-windowDays);
        var to = toArg ?? today;

        var registry = new ModelRegistryClient(sqlCs);
        var labelBuilder = new LabelBuilder(clickHouseCs, sqlCs, msg => Console.WriteLine(msg));

        // ---- step 1: cadence guard ----
        var newest = await registry.GetNewestRunAsync(ct).ConfigureAwait(false);
        if (!force && newest is not null
            && newest.TrainedUtc > DateTime.UtcNow.AddHours(-minIntervalHours))
        {
            Console.WriteLine(
                $"last run {newest.TrainedUtc:O} is younger than MinIntervalHours ({minIntervalHours}h); nothing to do");
            return 0;
        }

        var tenantIds = await labelBuilder.ListActiveTenantIdsAsync(ct).ConfigureAwait(false);

        // ---- step 2: labels ----
        if (!skipLabelBuild)
        {
            var labelResult = await labelBuilder.RunAsync(from, to, ct).ConfigureAwait(false);
            Console.WriteLine(
                $"labels: t1_rule inserted={labelResult.TotalT1Positives} " +
                $"synthetic_bot counted={labelResult.TotalSyntheticBotCounted} " +
                $"review_screen backfilled={labelResult.TotalReviewScreenBackfilled}");
        }
        else
        {
            Console.WriteLine("labels: skipped (--skip-label-build)");
        }

        // ---- step 3: feature-set boundary guard ----
        var histogram = await Trainer.ReadFeatureSetVersionHistogramAsync(clickHouseCs, tenantIds, from, to, ct)
            .ConfigureAwait(false);
        var offBoundary = histogram.Keys.Where(v => v != FraudFeatureVector.FeatureSetVersion).ToList();
        if (offBoundary.Count > 0)
        {
            Console.Error.WriteLine(
                $"the window straddles a feature-set boundary; narrow --from/--to past it. " +
                $"Running feature_set_version={FraudFeatureVector.FeatureSetVersion}. Histogram: " +
                string.Join(", ", histogram.OrderBy(kv => kv.Key).Select(kv => $"v{kv.Key}={kv.Value}")));
            return 3;
        }

        // ---- step 4: load ----
        var trainer = new Trainer();
        var set = await trainer.LoadTrainingSetAsync(clickHouseCs, tenantIds, from, to, t1Weight, ct)
            .ConfigureAwait(false);
        var positives = set.OrderedRows.Count(r => r.Label);
        var negatives = set.OrderedRows.Count - positives;
        Console.WriteLine($"load: rows={set.OrderedRows.Count} positives={positives} negatives={negatives} " +
                           $"dropped_conflicts={set.DroppedConflicts}");
        if (positives < minPositives || negatives < minNegatives)
        {
            Console.Error.WriteLine(
                $"not enough labels: positives={positives} (need {minPositives}), " +
                $"negatives={negatives} (need {minNegatives}); nothing registered.");
            return 3;
        }

        // ---- step 5: train ----
        var trainedUtc = DateTime.UtcNow;
        var result = trainer.TrainAndExport(set.OrderedRows, minAuc, from, to, artifactRoot, set.DroppedConflicts);
        Console.WriteLine(
            $"train: auc={result.Metrics.Auc:F4} auprc={result.Metrics.Auprc:F4} f1={result.Metrics.F1:F4} " +
            $"train_rows={result.TrainRows} validation_rows={result.ValidationRows}");

        if (!result.GatePassed)
        {
            var rejectReason = $"validation AUC {result.Metrics.Auc:F4} < Training:MinAuc {minAuc:F4}";
            await registry.InsertRunAsync(new NewModelRun(
                ScorerVersion: null, FeatureSetVersion: FraudFeatureVector.FeatureSetVersion,
                Status: ModelStatuses.Rejected, TrainedUtc: trainedUtc, WindowFrom: from, WindowTo: to,
                TrainRows: result.TrainRows, ValidationRows: result.ValidationRows,
                Positives: result.Positives, Negatives: result.Negatives, DroppedConflicts: result.DroppedConflicts,
                Auc: result.Metrics.Auc, Auprc: result.Metrics.Auprc, F1: result.Metrics.F1, MinAucGate: minAuc,
                ScoreP99Ms: null, ArtifactPath: null, ArtifactSha256: null, MetadataJson: null, GateJson: null,
                RejectReason: rejectReason), ct).ConfigureAwait(false);
            Console.Error.WriteLine($"GATE FAILED — {rejectReason}. Registered as 'rejected'.");
            return 2;
        }

        // ---- step 6: latency measurement (D3 gate) ----
        var scoreP99Ms = MeasureScoreP99Ms(result, set.OrderedRows);
        Console.WriteLine($"latency: score_p99_ms={scoreP99Ms:F3}");

        // ---- step 7: incumbent comparison ----
        var incumbent = await registry.GetIncumbentAsync(ct).ConfigureAwait(false);
        var incumbentMetrics = incumbent is null
            ? null
            : new IncumbentMetrics(incumbent.ScorerVersion ?? incumbent.ModelId.ToString(), incumbent.Auc, incumbent.Auprc);
        var candidateMetrics = new CandidateMetrics(
            result.Metrics.Auc, result.Metrics.Auprc, result.Metrics.F1, scoreP99Ms, result.Positives, result.Negatives);
        var gate = PromotionGate.EvaluateCandidate(candidateMetrics, incumbentMetrics, promotionOptions);

        var artifactPath = Path.GetFullPath(result.ModelDir!);
        var artifactSha256 = SHA256.HashData(File.OpenRead(Path.Combine(artifactPath, "model.zip")));
        var metadataJson = File.ReadAllText(Path.Combine(artifactPath, "metadata.json"));
        var gateJson = JsonSerializer.Serialize(gate, new JsonSerializerOptions { WriteIndented = true });

        // ---- step 8: register ----
        var status = gate.Passed ? ModelStatuses.Candidate : ModelStatuses.Rejected;
        var rejectReasonFinal = gate.Passed ? null : string.Join("; ", gate.Reasons);
        await registry.InsertRunAsync(new NewModelRun(
            ScorerVersion: result.ScorerVersion, FeatureSetVersion: FraudFeatureVector.FeatureSetVersion,
            Status: status, TrainedUtc: trainedUtc, WindowFrom: from, WindowTo: to,
            TrainRows: result.TrainRows, ValidationRows: result.ValidationRows,
            Positives: result.Positives, Negatives: result.Negatives, DroppedConflicts: result.DroppedConflicts,
            Auc: result.Metrics.Auc, Auprc: result.Metrics.Auprc, F1: result.Metrics.F1, MinAucGate: minAuc,
            ScoreP99Ms: scoreP99Ms, ArtifactPath: artifactPath, ArtifactSha256: artifactSha256,
            MetadataJson: metadataJson, GateJson: gateJson, RejectReason: rejectReasonFinal), ct).ConfigureAwait(false);

        if (!gate.Passed)
        {
            Console.Error.WriteLine(
                $"INCUMBENT GATE FAILED — {rejectReasonFinal}. Registered {result.ScorerVersion} as 'rejected'.");
            return 2;
        }

        // ---- step 9: print the next step verbatim; the loop never takes it itself ----
        Console.WriteLine(
            $"Registered candidate {result.ScorerVersion}. Nothing is serving it. " +
            $"Next: dotnet run --project TelemetryGuard.Training -- promote --model {result.ScorerVersion} --status shadow");
        return 0;
    }

    /// <summary>
    /// Loads the just-exported model.zip fresh (mirrors what the API host does) and
    /// times up to 2 000 validation-window rows (the same tail split TrainAndExport
    /// uses internally), discarding 3 warm-up passes, returning the p99 per-call
    /// elapsed milliseconds — the measurement <see cref="PromotionGate.EvaluateCandidate"/>
    /// gates against Training:Promotion:MaxScoreP99Ms (D3: a model too slow to serve is
    /// not a model).
    /// </summary>
    internal static double MeasureScoreP99Ms(TrainRunResult result, IReadOnlyList<MlFeatureRow> orderedRows)
    {
        var splitIndex = Math.Clamp((int)(orderedRows.Count * 0.8), 1, orderedRows.Count - 1);
        var validationRows = orderedRows.Skip(splitIndex).Take(2000).ToList();
        if (validationRows.Count == 0)
        {
            validationRows = orderedRows.Take(Math.Min(orderedRows.Count, 2000)).ToList();
        }

        var mlContext = new MLContext();
        var loadedModel = mlContext.Model.Load(Path.Combine(result.ModelDir!, "model.zip"), out _);
        var engine = mlContext.Model.CreatePredictionEngine<MlFeatureRow, MlPredictionRow>(loadedModel);

        // 3 warm-up passes — JIT/pool warmup, discarded.
        for (var warmup = 0; warmup < 3; warmup++)
        {
            foreach (var row in validationRows) engine.Predict(row);
        }

        var elapsedMs = new List<double>(validationRows.Count);
        foreach (var row in validationRows)
        {
            var sw = Stopwatch.StartNew();
            engine.Predict(row);
            sw.Stop();
            elapsedMs.Add(sw.Elapsed.TotalMilliseconds);
        }

        elapsedMs.Sort();
        var p99Index = Math.Clamp((int)Math.Ceiling(0.99 * elapsedMs.Count) - 1, 0, elapsedMs.Count - 1);
        return elapsedMs[p99Index];
    }

    private static (DateOnly? From, DateOnly? To, string? Out, bool SkipLabelBuild, bool Force) ParseArgs(string[] args)
    {
        DateOnly? from = null, to = null;
        string? outDir = null;
        var skipLabelBuild = false;
        var force = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--from" when i + 1 < args.Length: from = DateOnly.Parse(args[++i]); break;
                case "--to" when i + 1 < args.Length: to = DateOnly.Parse(args[++i]); break;
                case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
                case "--skip-label-build": skipLabelBuild = true; break;
                case "--force": force = true; break;
            }
        }
        return (from, to, outDir, skipLabelBuild, force);
    }

    private static string RequireConnectionString(IConfiguration config, string name)
    {
        var value = config.GetConnectionString(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"ConnectionStrings:{name} is not configured.");
        }
        return value;
    }

    /// <summary>ML.NET's raw binary-classification output row — deliberately duplicated
    /// from TelemetryGuard.RiskEngine.Scoring.MlPrediction: Training must not reference
    /// TelemetryGuard.RiskEngine (dependency-surface rule), and MlPrediction cannot move
    /// into TelemetryGuard.RiskEngine.Contracts without breaking its zero-package-reference
    /// rule (ColumnName lives in Microsoft.ML.Data). Keep the two shapes in sync by hand.</summary>
    private sealed class MlPredictionRow
    {
        // ML.NET assigns these via reflection at prediction time; the explicit
        // initializers only silence CS0649 ("never assigned") for this
        // reflection-only-accessible nested-private-class type (mirrors
        // TrainerSmokeTests.SmokeTestPrediction's identical pattern).
        [ColumnName("PredictedLabel")]
        public bool PredictedLabel = false;

        public float Probability = 0f;

        public float Score = 0f;
    }
}
