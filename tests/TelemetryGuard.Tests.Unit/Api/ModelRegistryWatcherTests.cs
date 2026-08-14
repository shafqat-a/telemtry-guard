using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TelemetryGuard.Api.Options;
using TelemetryGuard.Api.Startup;
using TelemetryGuard.Api.Workers;
using TelemetryGuard.Data.Models;
using TelemetryGuard.Data.Repositories;

namespace TelemetryGuard.Tests.Unit.Api;

/// <summary>P2-02 acceptance: ModelRegistryWatcher observes — it NEVER applies a
/// promotion, never throws on a registry read failure, and stays silent when
/// Scoring:ModelSource != "Registry" (RSK-08 pilot mode).</summary>
public sealed class ModelRegistryWatcherTests
{
    private sealed class FakeRegistry(ServingModel? serving) : IModelRegistryRepository
    {
        public Task<ServingModel?> GetServingModelAsync(CancellationToken ct) => Task.FromResult(serving);

        public Task<IReadOnlyList<ModelRegistryEntry>> ListRecentAsync(int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ModelRegistryEntry>>(Array.Empty<ModelRegistryEntry>());
    }

    private sealed class ThrowingRegistry : IModelRegistryRepository
    {
        public Task<ServingModel?> GetServingModelAsync(CancellationToken ct)
            => throw new InvalidOperationException("SQL down");

        public Task<IReadOnlyList<ModelRegistryEntry>> ListRecentAsync(int limit, CancellationToken ct)
            => throw new InvalidOperationException("SQL down");
    }

    private sealed class CapturingLogger : ILogger<ModelRegistryWatcher>
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private static IConfiguration RegistryDrivenConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Scoring:ModelSource"] = "Registry" }).Build();

    private static ServingModel Serving(string scorerVersion, string status) =>
        new(Guid.NewGuid(), scorerVersion, status, $"/models/{scorerVersion}", new byte[32], 1);

    [Fact]
    public async Task RunOnceAsync_RegistryServesDifferentVersion_LogsPendingPromotionWarning_LoadedStateUntouched()
    {
        var loaded = new ServingModelState("lgbm-A", "active", "/models/lgbm-A", []);
        var registry = new FakeRegistry(Serving("lgbm-B", "active"));
        var log = new CapturingLogger();
        var watcher = new ModelRegistryWatcher(
            registry, loaded, Options.Create(new ModelRegistryOptions()), RegistryDrivenConfig(), log);

        await watcher.RunOnceAsync(CancellationToken.None);

        Assert.Contains(log.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("promotion pending", StringComparison.OrdinalIgnoreCase));
        // Never applies the promotion — the process's loaded state is fixed for its lifetime.
        Assert.Equal("lgbm-A", loaded.ScorerVersion);
        Assert.Equal("active", loaded.Status);
    }

    [Fact]
    public async Task RunOnceAsync_RegistryAgreesWithLoaded_NoWarning()
    {
        var loaded = new ServingModelState("lgbm-A", "active", "/models/lgbm-A", []);
        var registry = new FakeRegistry(Serving("lgbm-A", "active"));
        var log = new CapturingLogger();
        var watcher = new ModelRegistryWatcher(
            registry, loaded, Options.Create(new ModelRegistryOptions()), RegistryDrivenConfig(), log);

        await watcher.RunOnceAsync(CancellationToken.None);

        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task RunOnceAsync_RegistryReadFails_SwallowedAndLogged_NeverThrows()
    {
        var loaded = new ServingModelState("lgbm-A", "active", "/models/lgbm-A", []);
        var log = new CapturingLogger();
        var watcher = new ModelRegistryWatcher(
            new ThrowingRegistry(), loaded, Options.Create(new ModelRegistryOptions()), RegistryDrivenConfig(), log);

        await watcher.RunOnceAsync(CancellationToken.None); // must not throw — the host keeps running

        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task RunOnceAsync_SamePendingVersionTwice_WarnsOnlyOnce()
    {
        var loaded = new ServingModelState("lgbm-A", "active", "/models/lgbm-A", []);
        var registry = new FakeRegistry(Serving("lgbm-B", "active"));
        var log = new CapturingLogger();
        var watcher = new ModelRegistryWatcher(
            registry, loaded, Options.Create(new ModelRegistryOptions()), RegistryDrivenConfig(), log);

        await watcher.RunOnceAsync(CancellationToken.None);
        await watcher.RunOnceAsync(CancellationToken.None);

        Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task ExecuteAsync_NotRegistryDriven_NoOps_NeverCallsRegistry()
    {
        var loaded = new ServingModelState(null, null, null, []);
        var registry = new ThrowingRegistry(); // would throw if ever called
        var log = new CapturingLogger();
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Scoring:ModelSource"] = "Config" }).Build();
        var watcher = new ModelRegistryWatcher(
            registry, loaded, Options.Create(new ModelRegistryOptions()), config, log);

        await watcher.StartAsync(CancellationToken.None);
        await Task.Delay(50); // ExecuteAsync returns immediately for the no-op path
        await watcher.StopAsync(CancellationToken.None);

        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Debug);
    }
}
