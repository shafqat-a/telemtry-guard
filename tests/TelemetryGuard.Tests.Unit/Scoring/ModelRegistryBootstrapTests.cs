using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TelemetryGuard.Api.Startup;
using TelemetryGuard.Data.Models;
using TelemetryGuard.RiskEngine.Contracts;
using TelemetryGuard.RiskEngine.Pipeline;
using TelemetryGuard.RiskEngine.Scoring;
using TelemetryGuard.RiskEngine.Velocity;
using TelemetryGuard.Training;
// Both TelemetryGuard.Data.Repositories and TelemetryGuard.Training declare a
// ModelStatuses type (P2-02 step 8's deliberate, test-proven duplication — see
// ModelRegistryClient's class doc) — alias the registry-read-side (Data) one the
// serving host actually uses so `ModelStatuses.Active` below is unambiguous.
using ModelStatuses = TelemetryGuard.Data.Repositories.ModelStatuses;

namespace TelemetryGuard.Tests.Unit.Scoring;

/// <summary>P2-02 acceptance: ModelRegistryBootstrap's fail-safe artifact verification
/// and its RSK-08 Scoring:* override translation.</summary>
public sealed class ModelRegistryBootstrapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tg-registry-bootstrap-" + Guid.NewGuid().ToString("N"));
    private readonly TrainRunResult _trained;

    public ModelRegistryBootstrapTests()
    {
        // Fixture model built via the shipped Trainer.TrainAndExport on ~200 synthetic
        // rows (seconds, in-memory) — never a committed model file.
        var rows = GenerateRows(200, seed: 11);
        var trainer = new Trainer(seed: 3);
        _trained = trainer.TrainAndExport(
            rows, minAuc: 0.0, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), _root);
        Assert.True(_trained.GatePassed);
        Assert.NotNull(_trained.ModelDir);
        Assert.NotNull(_trained.ScorerVersion);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static List<MlFeatureRow> GenerateRows(int count, int seed)
    {
        var rng = new Random(seed);
        var rows = new List<MlFeatureRow>(count);
        for (var i = 0; i < count; i++)
        {
            var fraud = i % 2 == 0;
            rows.Add(new MlFeatureRow
            {
                Label = fraud,
                Weight = 1f,
                has_js_beacon = 1f,
                webdriver_flag = fraud ? 1f : 0f,
                ip_clicks_last_min = fraud ? 40f + rng.Next(20) : rng.Next(3),
                mouse_path_linearity = fraud ? 0.98f : 0.4f + ((float)rng.NextDouble() * 0.3f),
                is_paid_click = 1f,
            });
        }
        return rows;
    }

    private string CopyModelDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tg-registry-bootstrap-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(_trained.ModelDir!, "model.zip"), Path.Combine(dir, "model.zip"));
        File.Copy(Path.Combine(_trained.ModelDir!, "metadata.json"), Path.Combine(dir, "metadata.json"));
        return dir;
    }

    // ---- VerifyArtifact ----

    [Fact]
    public void VerifyArtifact_ValidDirectory_ReturnsNull()
    {
        var dir = _trained.ModelDir!;
        var hash = SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, "model.zip")));
        var serving = new ServingModel(Guid.NewGuid(), _trained.ScorerVersion!, "active", dir, hash, FraudFeatureVector.FeatureSetVersion);

        var error = ModelRegistryBootstrap.VerifyArtifact(dir, serving, FraudFeatureVector.FeatureSetVersion);

        Assert.Null(error);
    }

    [Fact]
    public void VerifyArtifact_MissingModelZip_ReturnsDistinctMessage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tg-registry-bootstrap-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var serving = new ServingModel(Guid.NewGuid(), "lgbm-x", "active", dir, new byte[32], 1);
            var error = ModelRegistryBootstrap.VerifyArtifact(dir, serving, 1);

            Assert.NotNull(error);
            Assert.Contains("model.zip", error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void VerifyArtifact_MutatedModelZip_HashMismatch_ReturnsDistinctMessage()
    {
        var goodHash = SHA256.HashData(File.ReadAllBytes(Path.Combine(_trained.ModelDir!, "model.zip")));
        var dir = CopyModelDir();
        try
        {
            var zipPath = Path.Combine(dir, "model.zip");
            var bytes = File.ReadAllBytes(zipPath);
            bytes[0] ^= 0xFF; // flip a bit — same length, different content/hash
            File.WriteAllBytes(zipPath, bytes);

            var serving = new ServingModel(Guid.NewGuid(), _trained.ScorerVersion!, "active", dir, goodHash, FraudFeatureVector.FeatureSetVersion);
            var error = ModelRegistryBootstrap.VerifyArtifact(dir, serving, FraudFeatureVector.FeatureSetVersion);

            Assert.NotNull(error);
            Assert.Contains("SHA-256", error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void VerifyArtifact_MismatchedScorerVersion_ReturnsDistinctMessage()
    {
        var dir = _trained.ModelDir!;
        var hash = SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, "model.zip")));
        var serving = new ServingModel(Guid.NewGuid(), "lgbm-totally-different-version", "active", dir, hash, FraudFeatureVector.FeatureSetVersion);

        var error = ModelRegistryBootstrap.VerifyArtifact(dir, serving, FraudFeatureVector.FeatureSetVersion);

        Assert.NotNull(error);
        Assert.Contains("scorer_version", error, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyArtifact_MismatchedFeatureSetVersion_ReturnsDistinctMessage()
    {
        var dir = _trained.ModelDir!;
        var hash = SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, "model.zip")));
        var serving = new ServingModel(Guid.NewGuid(), _trained.ScorerVersion!, "active", dir, hash, FraudFeatureVector.FeatureSetVersion);

        var error = ModelRegistryBootstrap.VerifyArtifact(dir, serving, expectedFeatureSetVersion: 999);

        Assert.NotNull(error);
        Assert.Contains("feature_set_version", error, StringComparison.Ordinal);
    }

    // ---- ToScoringOverrides ----

    [Fact]
    public void ToScoringOverrides_Active_MapsToMlNetEnforce()
    {
        var state = new ServingModelState("lgbm-x", ModelStatuses.Active, "/models/lgbm-x", []);
        var overrides = ModelRegistryBootstrap.ToScoringOverrides(state);

        Assert.Equal("/models/lgbm-x", overrides["Scoring:ModelPath"]);
        Assert.Equal("MlNet", overrides["Scoring:Scorer"]);
        Assert.Equal("Enforce", overrides["Scoring:Mode"]);
    }

    [Fact]
    public void ToScoringOverrides_Shadow_MapsToHeuristicListenOnly()
    {
        var state = new ServingModelState("lgbm-x", ModelStatuses.Shadow, "/models/lgbm-x", []);
        var overrides = ModelRegistryBootstrap.ToScoringOverrides(state);

        Assert.Equal("/models/lgbm-x", overrides["Scoring:ModelPath"]);
        Assert.Equal("Heuristic", overrides["Scoring:Scorer"]);
        Assert.Equal("ListenOnly", overrides["Scoring:Mode"]);
    }

    [Fact]
    public void ToScoringOverrides_NothingPromoted_ClearsModelPath()
    {
        var state = new ServingModelState(null, null, null, []);
        var overrides = ModelRegistryBootstrap.ToScoringOverrides(state);

        Assert.Equal("", overrides["Scoring:ModelPath"]);
        Assert.Equal("Heuristic", overrides["Scoring:Scorer"]);
        Assert.Equal("Enforce", overrides["Scoring:Mode"]);
    }

    // ---- Composition (mirrors MlNetScoringWiringTests) ----

    private static IServiceCollection BaseServices(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISessionStateStore>(new FakeSessionStore());
        services.AddSingleton<IWhitelistCheck>(new FakeWhitelist());
        services.AddSingleton<IVelocityStore>(new FakeVelocity());
        services.AddScoringPipeline(config);
        return services;
    }

    private sealed class FakeSessionStore : ISessionStateStore
    {
        public Task<SessionState?> GetAsync(string sessionId, CancellationToken ct) => Task.FromResult<SessionState?>(null);
    }

    private sealed class FakeWhitelist : IWhitelistCheck
    {
        public Task<bool> IsWhitelistedAsync(string ip, string? visitorId, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class FakeVelocity : IVelocityStore
    {
        public Task<bool?> RecordClickAsync(string ip, string? ua, string? clickId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task RecordSessionAsync(string ip, string? ua, string? visitorId, string sessionId,
            bool storageAgeZero, CancellationToken ct) => throw new NotSupportedException();
        public Task<VelocitySnapshot> ReadAsync(string ip, string? visitorId, CancellationToken ct)
            => Task.FromResult(new VelocitySnapshot(0, 0, 0, 0, 0));
    }

    [Fact]
    public void Composition_ActiveOverrides_ResolvesMlNetScorer()
    {
        var state = new ServingModelState(_trained.ScorerVersion, ModelStatuses.Active, _trained.ModelDir, []);
        var overrides = ModelRegistryBootstrap.ToScoringOverrides(state);
        var config = new ConfigurationBuilder().AddInMemoryCollection(overrides).Build();

        using var provider = BaseServices(config).BuildServiceProvider();
        using var scope = provider.CreateScope();
        var scorer = scope.ServiceProvider.GetRequiredService<IScorer>();

        Assert.IsType<MlNetScorer>(scorer);
        Assert.Equal(_trained.ScorerVersion, scorer.ScorerVersion);
        Assert.Null(scope.ServiceProvider.GetService<IShadowScorer>());
    }

    [Fact]
    public void Composition_ShadowOverrides_HeuristicEnforces_ModelRunsAsShadow()
    {
        var state = new ServingModelState(_trained.ScorerVersion, ModelStatuses.Shadow, _trained.ModelDir, []);
        var overrides = ModelRegistryBootstrap.ToScoringOverrides(state);
        var config = new ConfigurationBuilder().AddInMemoryCollection(overrides).Build();

        using var provider = BaseServices(config).BuildServiceProvider();
        using var scope = provider.CreateScope();
        var scorer = scope.ServiceProvider.GetRequiredService<IScorer>();
        var shadow = scope.ServiceProvider.GetService<IShadowScorer>();

        Assert.Equal("heuristic-1", scorer.ScorerVersion); // enforcing scorer is UNCHANGED
        Assert.NotNull(shadow);
        var shadowResult = shadow!.Score(new FraudFeatureVector());
        Assert.Equal(_trained.ScorerVersion, shadowResult.ScorerVersion);
    }

    [Fact]
    public void Composition_NoModelOverrides_NoPredictionEnginePoolRegistered()
    {
        var state = new ServingModelState(null, null, null, []);
        var overrides = ModelRegistryBootstrap.ToScoringOverrides(state);
        var config = new ConfigurationBuilder().AddInMemoryCollection(overrides).Build();

        using var provider = BaseServices(config).BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Equal("heuristic-1", scope.ServiceProvider.GetRequiredService<IScorer>().ScorerVersion);
        Assert.Null(scope.ServiceProvider.GetService<IShadowScorer>());
        Assert.Null(scope.ServiceProvider.GetService<MlNetScorer>());
    }
}
