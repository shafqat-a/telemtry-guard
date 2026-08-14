using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ML;
using Microsoft.ML.Trainers.LightGbm;
using TelemetryGuard.RiskEngine.Contracts;
using TelemetryGuard.RiskEngine.Pipeline;
using TelemetryGuard.RiskEngine.Scoring;
using TelemetryGuard.RiskEngine.Velocity;

namespace TelemetryGuard.Tests.Unit.Pipeline;

/// <summary>RSK-08 DI-wiring acceptance: Scoring:Scorer/Scoring:Mode/Scoring:ModelPath
/// (a config change only, D18) resolve to the right IScorer/IShadowScorer, with
/// scorer_version read from metadata.json — never hard-coded. Trains a tiny real
/// model into a temp dir (a "fake pool" in the sense that it exists only for this
/// test, not that PredictionEnginePool itself is mocked — AddPredictionEnginePool's
/// real extension method loads it).</summary>
public sealed class MlNetScoringWiringTests : IDisposable
{
    private readonly string _modelDir = Path.Combine(Path.GetTempPath(), "tg-mlnet-wiring-" + Guid.NewGuid().ToString("N"));

    public MlNetScoringWiringTests() => Directory.CreateDirectory(_modelDir);

    public void Dispose()
    {
        try { Directory.Delete(_modelDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private void TrainTinyModelAndWriteMetadata(string scorerVersion)
    {
        var mlContext = new MLContext(seed: 1);
        var rng = new Random(1);
        var rows = Enumerable.Range(0, 60).Select(i =>
        {
            var fraud = i % 2 == 0;
            var row = new MlFeatureRow { Label = fraud, Weight = 1f };
            row.webdriver_flag = fraud ? 1f : 0f;
            row.ip_clicks_last_min = fraud ? 40f + rng.Next(10) : rng.Next(3);
            row.has_js_beacon = 1f;
            return row;
        }).ToList();

        var dataView = mlContext.Data.LoadFromEnumerable(rows);
        var pipeline = mlContext.Transforms.Concatenate("Features", MlFeatureRow.FeatureFieldNames)
            .Append(mlContext.BinaryClassification.Trainers.LightGbm(new LightGbmBinaryTrainer.Options
            {
                LabelColumnName = "Label",
                ExampleWeightColumnName = "Weight",
                FeatureColumnName = "Features",
                NumberOfLeaves = 4,
                NumberOfIterations = 5,
                MinimumExampleCountPerLeaf = 1,
                HandleMissingValue = true,
            }));

        var model = pipeline.Fit(dataView);
        mlContext.Model.Save(model, dataView.Schema, Path.Combine(_modelDir, "model.zip"));
        File.WriteAllText(Path.Combine(_modelDir, "metadata.json"),
            $$"""{"scorer_version":"{{scorerVersion}}","feature_set_version":1}""");
    }

    private static IServiceCollection BaseServices(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Fakes for every Redis-backed piece — no multiplexer needed (mirrors
        // ScoringPipelineTests' AddScoringPipeline_ResolvesWithFakesForRedisBackedPieces).
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
    public void EnforceMode_ScorerMlNet_ResolvesMlNetScorer_WithVersionFromMetadata()
    {
        TrainTinyModelAndWriteMetadata("lgbm-20260915-a1b2c3d4");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Scoring:Scorer"] = "MlNet",
            ["Scoring:Mode"] = "Enforce",
            ["Scoring:ModelPath"] = _modelDir,
        }).Build();

        using var provider = BaseServices(config).BuildServiceProvider();
        using var scope = provider.CreateScope();
        var scorer = scope.ServiceProvider.GetRequiredService<IScorer>();

        Assert.IsType<MlNetScorer>(scorer);
        Assert.Equal("lgbm-20260915-a1b2c3d4", scorer.ScorerVersion);
        // Enforce mode never registers a shadow scorer.
        Assert.Null(scope.ServiceProvider.GetService<IShadowScorer>());
    }

    [Fact]
    public void ListenOnlyMode_ScorerHeuristic_EnforcingStaysHeuristic_ModelRunsAsShadow()
    {
        TrainTinyModelAndWriteMetadata("lgbm-20260915-deadbeef");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Scoring:Scorer"] = "Heuristic",
            ["Scoring:Mode"] = "ListenOnly",
            ["Scoring:ModelPath"] = _modelDir,
        }).Build();

        using var provider = BaseServices(config).BuildServiceProvider();
        using var scope = provider.CreateScope();
        var scorer = scope.ServiceProvider.GetRequiredService<IScorer>();
        var shadow = scope.ServiceProvider.GetService<IShadowScorer>();

        Assert.Equal("heuristic-1", scorer.ScorerVersion); // enforcing scorer is UNCHANGED
        Assert.NotNull(shadow);
        var shadowResult = shadow!.Score(new FraudFeatureVector());
        Assert.Equal("lgbm-20260915-deadbeef", shadowResult.ScorerVersion);
    }

    [Fact]
    public void NoModelPath_NeitherMlNetScorerNorShadowScorer_Registered()
    {
        var config = new ConfigurationBuilder().Build(); // defaults: Heuristic/Enforce, no ModelPath

        using var provider = BaseServices(config).BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Equal("heuristic-1", scope.ServiceProvider.GetRequiredService<IScorer>().ScorerVersion);
        Assert.Null(scope.ServiceProvider.GetService<IShadowScorer>());
        Assert.Null(scope.ServiceProvider.GetService<MlNetScorer>());
    }

    [Fact]
    public void ScorerMlNet_WithoutModelPath_ThrowsAtRegistration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Scoring:Scorer"] = "MlNet",
        }).Build();

        Assert.Throws<InvalidOperationException>(() => BaseServices(config));
    }
}
