using System.Security.Cryptography;
using System.Text.Json;
using ClickHouse.Client.ADO;
using ClickHouse.Client.Utility;
using Microsoft.ML;
using Microsoft.ML.Trainers.LightGbm;
using TelemetryGuard.RiskEngine.Contracts;

namespace TelemetryGuard.Training;

public sealed record TrainMetrics(double Auc, double Auprc, double F1);

/// <summary>The loaded, ordered training rows plus the dropped-conflict count — the
/// output of <see cref="Trainer.LoadTrainingSetAsync"/>, ready for
/// <see cref="Trainer.TrainAndExport"/> (P2-02's `retrain` command needs the loaded set
/// itself, not just the exit code <see cref="Trainer.RunFromDatabaseAsync"/> returns).</summary>
public sealed record TrainingSet(IReadOnlyList<MlFeatureRow> OrderedRows, int DroppedConflicts);

/// <summary>Result of one TrainAndExport call. ModelDir/ScorerVersion are null exactly
/// when GatePassed is false (RSK-08's AUC gate — export NOTHING on failure).</summary>
public sealed record TrainRunResult(
    bool GatePassed,
    TrainMetrics Metrics,
    int TrainRows,
    int ValidationRows,
    int Positives,
    int Negatives,
    int DroppedConflicts,
    string? ScorerVersion,
    string? ModelDir);

/// <summary>
/// RSK-08 step 4: joins deduped/resolved labels (<see cref="LabelResolver"/>) with
/// stored per-session feature vectors into an ML.NET LightGBM binary classifier, time-
/// split (never random — fraud drifts), gated on validation AUC. NaN-for-missing
/// (spec §7) is exactly why <c>HandleMissingValue = true</c> is mandatory — LightGBM
/// branches on NaN natively; never impute here.
/// </summary>
public sealed class Trainer
{
    private readonly MLContext _ml;

    public Trainer(int seed = 42) => _ml = new MLContext(seed);

    /// <summary>
    /// Pure, in-memory training core — no DB access, callable directly from tests
    /// (TrainerSmokeTests, AUC-gate tests). <paramref name="orderedRows"/> MUST already
    /// be ordered ascending by event timestamp: first 80% = train, last 20% =
    /// validation (never random). Exports model.zip + metadata.json under
    /// <paramref name="outDir"/>/{scorer_version}/ ONLY when
    /// AUC &gt;= <paramref name="minAuc"/>; on gate failure nothing is written.
    /// </summary>
    public TrainRunResult TrainAndExport(
        IReadOnlyList<MlFeatureRow> orderedRows,
        double minAuc,
        DateOnly windowFrom,
        DateOnly windowTo,
        string outDir,
        int droppedConflicts = 0)
    {
        ArgumentNullException.ThrowIfNull(orderedRows);
        if (orderedRows.Count < 2)
        {
            throw new ArgumentException(
                "Need at least 2 rows to form a time-based train/validation split.", nameof(orderedRows));
        }

        var splitIndex = Math.Clamp((int)(orderedRows.Count * 0.8), 1, orderedRows.Count - 1);
        var trainRows = orderedRows.Take(splitIndex).ToList();
        var validationRows = orderedRows.Skip(splitIndex).ToList();

        var trainData = _ml.Data.LoadFromEnumerable(trainRows);
        var validationData = _ml.Data.LoadFromEnumerable(validationRows);

        var pipeline = _ml.Transforms.Concatenate("Features", MlFeatureRow.FeatureFieldNames)
            .Append(_ml.BinaryClassification.Trainers.LightGbm(new LightGbmBinaryTrainer.Options
            {
                LabelColumnName = "Label",
                ExampleWeightColumnName = "Weight",
                FeatureColumnName = "Features",
                NumberOfLeaves = 31,
                NumberOfIterations = 200,
                LearningRate = 0.05,
                MinimumExampleCountPerLeaf = 20,
                HandleMissingValue = true, // MANDATORY: the NaN-branching behavior the whole missing≠zero design relies on.
                UnbalancedSets = true,
            }));

        var model = pipeline.Fit(trainData);
        var scored = model.Transform(validationData);
        var eval = _ml.BinaryClassification.Evaluate(scored, labelColumnName: "Label");
        var metrics = new TrainMetrics(eval.AreaUnderRocCurve, eval.AreaUnderPrecisionRecallCurve, eval.F1Score);

        var positives = orderedRows.Count(r => r.Label);
        var negatives = orderedRows.Count - positives;

        if (metrics.Auc < minAuc)
        {
            // Gate: export NOTHING on failure.
            return new TrainRunResult(
                false, metrics, trainRows.Count, validationRows.Count, positives, negatives, droppedConflicts,
                ScorerVersion: null, ModelDir: null);
        }

        Directory.CreateDirectory(outDir);
        var tempZip = Path.Combine(outDir, $".tmp-{Guid.NewGuid():N}.zip");
        _ml.Model.Save(model, trainData.Schema, tempZip);

        string hash8;
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(tempZip))
        {
            hash8 = Convert.ToHexString(sha.ComputeHash(stream))[..8].ToLowerInvariant();
        }

        var scorerVersion = $"lgbm-{DateTime.UtcNow:yyyyMMdd}-{hash8}";
        var modelDir = Path.Combine(outDir, scorerVersion);
        Directory.CreateDirectory(modelDir);
        var finalZipPath = Path.Combine(modelDir, "model.zip");
        File.Move(tempZip, finalZipPath, overwrite: true);

        var metadata = new
        {
            scorer_version = scorerVersion,
            feature_set_version = FraudFeatureVector.FeatureSetVersion,
            trained_at_utc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            window = new { from = windowFrom.ToString("yyyy-MM-dd"), to = windowTo.ToString("yyyy-MM-dd") },
            rows = new
            {
                train = trainRows.Count, validation = validationRows.Count,
                positives, negatives, dropped_conflicts = droppedConflicts,
            },
            metrics = new { auc = metrics.Auc, auprc = metrics.Auprc, f1 = metrics.F1 },
            min_auc_gate = minAuc,
        };
        File.WriteAllText(
            Path.Combine(modelDir, "metadata.json"),
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));

        return new TrainRunResult(
            true, metrics, trainRows.Count, validationRows.Count, positives, negatives, droppedConflicts,
            scorerVersion, modelDir);
    }

    /// <summary>
    /// The `train` CLI command's DB-driven entry point: per tenant (D11 — never an
    /// unscoped cross-tenant ClickHouse query), reads tg_labels for the window,
    /// resolves/dedupes via <see cref="LabelResolver"/>, joins matching tg_events rows
    /// on (tenant_id, session_id), deserializes `features` back into
    /// FraudFeatureVector (same FraudFeatureVectorJson options the writer used), maps
    /// via MlFeatureMapper, orders the combined set by event timestamp ascending
    /// across ALL tenants, then delegates to <see cref="TrainAndExport"/>.
    /// Returns the process exit code: 0 = gate passed, 2 = AUC gate failed
    /// (no artifacts written), 1 = no training rows found in the window.
    /// </summary>
    public async Task<int> RunFromDatabaseAsync(
        string clickHouseConnectionString,
        IReadOnlyList<Guid> tenantIds,
        DateOnly from,
        DateOnly to,
        double minAuc,
        float t1PositiveWeight,
        string outDir,
        CancellationToken ct = default)
    {
        var set = await LoadTrainingSetAsync(clickHouseConnectionString, tenantIds, from, to, t1PositiveWeight, ct)
            .ConfigureAwait(false);

        if (set.OrderedRows.Count == 0)
        {
            Console.Error.WriteLine("No training rows found in the requested window.");
            return 1;
        }

        var result = TrainAndExport(set.OrderedRows, minAuc, from, to, outDir, set.DroppedConflicts);
        PrintMetrics(result);
        return result.GatePassed ? 0 : 2;
    }

    /// <summary>
    /// The ClickHouse half of RunFromDatabaseAsync, extracted for P2-02's `retrain`
    /// command (which needs the loaded rows AND the TrainRunResult, not just an exit
    /// code): per tenant (D11), reads tg_labels for the window, resolves/dedupes via
    /// <see cref="LabelResolver"/>, joins matching tg_events rows on
    /// (tenant_id, session_id), deserializes `features` with
    /// <see cref="FraudFeatureVectorJson"/>'s options, maps via
    /// <see cref="MlFeatureMapper"/>, then orders the combined set by event timestamp
    /// ascending across ALL tenants (never random — fraud drifts). Behavior is IDENTICAL
    /// to what <see cref="RunFromDatabaseAsync"/> used to do inline — RSK-08's tests
    /// must keep passing untouched.
    /// </summary>
    public async Task<TrainingSet> LoadTrainingSetAsync(
        string clickHouseConnectionString,
        IReadOnlyList<Guid> tenantIds,
        DateOnly from,
        DateOnly to,
        float t1PositiveWeight,
        CancellationToken ct = default)
    {
        var combined = new List<(DateTime Timestamp, MlFeatureRow Row)>();
        var droppedConflicts = 0;

        foreach (var tenantId in tenantIds)
        {
            var labelRows = await ReadLabelsAsync(clickHouseConnectionString, tenantId, from, to, ct).ConfigureAwait(false);
            var resolved = LabelResolver.Resolve(labelRows, t1PositiveWeight);
            droppedConflicts += resolved.DroppedConflicts;
            if (resolved.Resolved.Count == 0) continue;

            var bySession = resolved.Resolved.ToDictionary(r => r.SessionId, StringComparer.Ordinal);
            var eventRows = await ReadEventFeaturesAsync(
                clickHouseConnectionString, tenantId, from, to, bySession.Keys, ct).ConfigureAwait(false);

            foreach (var (sessionId, featuresJson, timestamp) in eventRows)
            {
                if (!bySession.TryGetValue(sessionId, out var label)) continue;
                if (string.IsNullOrWhiteSpace(featuresJson)) continue; // no vector captured — cannot train on it

                FraudFeatureVector vector;
                try
                {
                    vector = JsonSerializer.Deserialize<FraudFeatureVector>(featuresJson, FraudFeatureVectorJson.Options)
                        ?? throw new JsonException("null FraudFeatureVector");
                }
                catch (JsonException)
                {
                    continue; // malformed/stale features payload — skip rather than crash the whole run
                }

                var row = MlFeatureMapper.ToRow(vector);
                row.Label = label.Fraud;
                row.Weight = label.Weight;
                combined.Add((timestamp, row));
            }
        }

        var orderedRows = combined.OrderBy(x => x.Timestamp).Select(x => x.Row).ToList();
        return new TrainingSet(orderedRows, droppedConflicts);
    }

    /// <summary>
    /// P2-02 `retrain` step 3 (feature-set boundary guard): histogram of
    /// feature_set_version values present in tg_events verdict rows that carry a
    /// captured feature vector, across every given tenant, within [from, to). Any value
    /// other than the running FraudFeatureVector.FeatureSetVersion means the window
    /// straddles a feature-set boundary — retrain must NEVER train across one silently.
    /// </summary>
    public static async Task<Dictionary<int, int>> ReadFeatureSetVersionHistogramAsync(
        string clickHouseConnectionString,
        IReadOnlyList<Guid> tenantIds,
        DateOnly from,
        DateOnly to,
        CancellationToken ct = default)
    {
        var histogram = new Dictionary<int, int>();
        foreach (var tenantId in tenantIds)
        {
            await using var conn = new ClickHouseConnection(clickHouseConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                SELECT feature_set_version, count()
                FROM tg_events
                WHERE tenant_id = {t:UUID} AND kind = 'verdict' AND features != ''
                  AND timestamp >= {f:DateTime64(3,'UTC')} AND timestamp < {to:DateTime64(3,'UTC')}
                GROUP BY feature_set_version
                """;
            cmd.AddParameter("t", tenantId);
            cmd.AddParameter("f", from.ToDateTime(TimeOnly.MinValue));
            cmd.AddParameter("to", to.ToDateTime(TimeOnly.MinValue));

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (reader.IsDBNull(0)) continue; // no feature_set_version stamped — nothing to compare
                var version = Convert.ToInt32(reader.GetValue(0));
                var count = Convert.ToInt32(reader.GetValue(1));
                histogram[version] = histogram.GetValueOrDefault(version) + count;
            }
        }
        return histogram;
    }

    private static void PrintMetrics(TrainRunResult result)
    {
        Console.WriteLine($"train_rows={result.TrainRows} validation_rows={result.ValidationRows} " +
            $"positives={result.Positives} negatives={result.Negatives} dropped_conflicts={result.DroppedConflicts}");
        Console.WriteLine($"auc={result.Metrics.Auc:F4} auprc={result.Metrics.Auprc:F4} f1={result.Metrics.F1:F4}");
        if (result.GatePassed)
        {
            Console.WriteLine($"GATE PASSED — exported {result.ScorerVersion} to {result.ModelDir}");
        }
        else
        {
            Console.WriteLine("GATE FAILED — AUC below Training:MinAuc; no artifacts exported.");
        }
    }

    // ---- ClickHouse reads (D11: tenant_id filters every query — never unscoped) ----

    private static async Task<List<RawLabelRow>> ReadLabelsAsync(
        string connectionString, Guid tenantId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var conn = new ClickHouseConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT session_id, label, label_source, weight, created_at
            FROM tg_labels
            WHERE tenant_id = {t:UUID}
              AND created_at >= {f:DateTime64(3,'UTC')} AND created_at < {to:DateTime64(3,'UTC')}
            """;
        cmd.AddParameter("t", tenantId);
        cmd.AddParameter("f", from.ToDateTime(TimeOnly.MinValue));
        cmd.AddParameter("to", to.ToDateTime(TimeOnly.MinValue));

        var result = new List<RawLabelRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new RawLabelRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetFloat(3), reader.GetDateTime(4)));
        }
        return result;
    }

    private static async Task<List<(string SessionId, string Features, DateTime Timestamp)>> ReadEventFeaturesAsync(
        string connectionString, Guid tenantId, DateOnly from, DateOnly to,
        IReadOnlyCollection<string> sessionIds, CancellationToken ct)
    {
        var result = new List<(string, string, DateTime)>();
        if (sessionIds.Count == 0) return result;

        await using var conn = new ClickHouseConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT session_id, features, timestamp
            FROM tg_events
            WHERE tenant_id = {t:UUID}
              AND kind = 'verdict'
              AND timestamp >= {f:DateTime64(3,'UTC')} AND timestamp < {to:DateTime64(3,'UTC')}
              AND session_id IN {ids:Array(String)}
            """;
        cmd.AddParameter("t", tenantId);
        cmd.AddParameter("f", from.ToDateTime(TimeOnly.MinValue));
        cmd.AddParameter("to", to.ToDateTime(TimeOnly.MinValue));
        cmd.AddParameter("ids", sessionIds.ToArray());

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add((reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1), reader.GetDateTime(2)));
        }
        return result;
    }
}
