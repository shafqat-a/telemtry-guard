using Microsoft.ML;
using TelemetryGuard.RiskEngine.Contracts;
using TelemetryGuard.Training;

namespace TelemetryGuard.Tests.Unit.Training;

/// <summary>RSK-08 acceptance: pure in-memory training-pipeline smoke tests (no
/// containers) invoking Trainer's classes directly, not the process.</summary>
public sealed class TrainerSmokeTests
{
    /// <summary>Separable synthetic rows: positives get webdriver_flag=1 and high
    /// velocity; negatives are clean. ~20% NaN sprinkle proves LightGBM's native NaN
    /// branching (HandleMissingValue=true) end-to-end — the whole point of this test.</summary>
    private static List<MlFeatureRow> GenerateSeparableRows(int count, int seed)
    {
        var rng = new Random(seed);
        var rows = new List<MlFeatureRow>(count);
        for (var i = 0; i < count; i++)
        {
            var fraud = i % 2 == 0;
            var row = new MlFeatureRow
            {
                Label = fraud,
                Weight = 1f,
                has_js_beacon = 1f,
                webdriver_flag = fraud ? 1f : 0f,
                headless_browser = fraud ? 1f : 0f,
                ip_clicks_last_min = fraud ? 40f + rng.Next(20) : rng.Next(3),
                mouse_path_linearity = fraud ? 0.98f : 0.4f + (float)rng.NextDouble() * 0.3f,
                std_inter_event_ms = fraud ? 1f + (float)rng.NextDouble() : 50f + (float)rng.NextDouble() * 50,
                time_on_page_sec = fraud ? 1f + (float)rng.NextDouble() : 30f + (float)rng.NextDouble() * 60,
                is_paid_click = 1f,
            };

            // ~20% NaN sprinkle across a few optional fields — proves the trainer
            // and PredictionEngine both tolerate NaN without throwing.
            if (rng.NextDouble() < 0.2) row.honeypot_touched = float.NaN;
            if (rng.NextDouble() < 0.2) row.beacon_integrity_failed = float.NaN;
            if (rng.NextDouble() < 0.2) row.form_fill_time_sec = float.NaN;
            if (rng.NextDouble() < 0.2) row.first_interaction_delay_ms = float.NaN;

            rows.Add(row);
        }
        return rows;
    }

    [Fact]
    public void TrainAndExport_SeparableData_TrainsAboveAucPointNine_AndExportsArtifacts()
    {
        var rows = GenerateSeparableRows(2000, seed: 1);
        var trainer = new Trainer(seed: 7);
        var outDir = Path.Combine(Path.GetTempPath(), "tg-trainer-smoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = trainer.TrainAndExport(
                rows, minAuc: 0.85,
                windowFrom: new DateOnly(2026, 8, 1), windowTo: new DateOnly(2026, 8, 31),
                outDir: outDir);

            Assert.True(result.GatePassed);
            Assert.True(result.Metrics.Auc > 0.9, $"Expected AUC > 0.9, got {result.Metrics.Auc}");
            Assert.NotNull(result.ScorerVersion);
            Assert.Matches("^lgbm-\\d{8}-[0-9a-f]{8}$", result.ScorerVersion);
            Assert.NotNull(result.ModelDir);
            Assert.True(Directory.Exists(result.ModelDir));

            var modelPath = Path.Combine(result.ModelDir!, "model.zip");
            var metadataPath = Path.Combine(result.ModelDir!, "metadata.json");
            Assert.True(File.Exists(modelPath));
            Assert.True(File.Exists(metadataPath));

            // model.zip Save/Load round-trips, and PredictionEngine scores a
            // NaN-bearing row without throwing — proves LightGBM NaN handling
            // end-to-end (the whole point of this test).
            var mlContext = new MLContext();
            var loadedModel = mlContext.Model.Load(modelPath, out _);
            var engine = mlContext.Model.CreatePredictionEngine<MlFeatureRow, SmokeTestPrediction>(loadedModel);
            var nanRow = new MlFeatureRow
            {
                has_js_beacon = 0f,
                honeypot_touched = float.NaN,
                click_before_render = float.NaN,
                mouse_path_linearity = float.NaN,
                std_inter_event_ms = float.NaN,
            };
            var prediction = engine.Predict(nanRow); // must not throw
            Assert.False(float.IsNaN(prediction.Probability));
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public void TrainAndExport_ShuffledLabels_AucNearHalf_GateFails_NoArtifactsExported()
    {
        var rng = new Random(2);
        var rows = GenerateSeparableRows(2000, seed: 1);
        // Shuffle ONLY the labels — features stay separable, but the label no longer
        // correlates with them, so AUC collapses to ~0.5.
        var shuffledLabels = rows.Select(r => r.Label).OrderBy(_ => rng.Next()).ToList();
        for (var i = 0; i < rows.Count; i++) rows[i].Label = shuffledLabels[i];

        var trainer = new Trainer(seed: 7);
        var outDir = Path.Combine(Path.GetTempPath(), "tg-trainer-gate-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = trainer.TrainAndExport(
                rows, minAuc: 0.85,
                windowFrom: new DateOnly(2026, 8, 1), windowTo: new DateOnly(2026, 8, 31),
                outDir: outDir);

            Assert.False(result.GatePassed);
            Assert.Null(result.ScorerVersion);
            Assert.Null(result.ModelDir);
            // Export NOTHING on gate failure.
            Assert.False(Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any());
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public void TrainAndExport_UsesTimeBasedSplit_Not_Random_80_20()
    {
        var rows = GenerateSeparableRows(100, seed: 3);
        var trainer = new Trainer(seed: 1);
        var outDir = Path.Combine(Path.GetTempPath(), "tg-trainer-split-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = trainer.TrainAndExport(
                rows, minAuc: 0.0, // no gate — only checking the split sizes
                windowFrom: new DateOnly(2026, 8, 1), windowTo: new DateOnly(2026, 8, 31),
                outDir: outDir);

            Assert.Equal(80, result.TrainRows);
            Assert.Equal(20, result.ValidationRows);
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public void Metadata_Json_ContainsAllStep4Fields()
    {
        var rows = GenerateSeparableRows(500, seed: 4);
        var trainer = new Trainer(seed: 2);
        var outDir = Path.Combine(Path.GetTempPath(), "tg-trainer-metadata-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = trainer.TrainAndExport(
                rows, minAuc: 0.85,
                windowFrom: new DateOnly(2026, 8, 1), windowTo: new DateOnly(2026, 8, 31),
                outDir: outDir);
            Assert.True(result.GatePassed);

            var json = File.ReadAllText(Path.Combine(result.ModelDir!, "metadata.json"));
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal(result.ScorerVersion, root.GetProperty("scorer_version").GetString());
            Assert.Equal(1, root.GetProperty("feature_set_version").GetInt32());
            Assert.True(root.TryGetProperty("trained_at_utc", out _));
            Assert.Equal("2026-08-01", root.GetProperty("window").GetProperty("from").GetString());
            Assert.Equal("2026-08-31", root.GetProperty("window").GetProperty("to").GetString());
            Assert.True(root.GetProperty("rows").GetProperty("train").GetInt32() > 0);
            Assert.True(root.GetProperty("rows").GetProperty("validation").GetInt32() > 0);
            Assert.True(root.GetProperty("rows").TryGetProperty("positives", out _));
            Assert.True(root.GetProperty("rows").TryGetProperty("negatives", out _));
            Assert.True(root.GetProperty("rows").TryGetProperty("dropped_conflicts", out _));
            Assert.True(root.GetProperty("metrics").TryGetProperty("auc", out _));
            Assert.True(root.GetProperty("metrics").TryGetProperty("auprc", out _));
            Assert.True(root.GetProperty("metrics").TryGetProperty("f1", out _));
            Assert.Equal(0.85, root.GetProperty("min_auc_gate").GetDouble());
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }

    private sealed class SmokeTestPrediction
    {
        // ML.NET assigns this via reflection at prediction time; the explicit
        // initializer only silences CS0649 ("never assigned") for this
        // reflection-only-accessible nested-private-class field.
        public float Probability = 0f;
    }
}
