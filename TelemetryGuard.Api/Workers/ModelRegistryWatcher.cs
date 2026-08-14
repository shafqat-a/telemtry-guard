using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using TelemetryGuard.Api.Options;
using TelemetryGuard.Api.Startup;
using TelemetryGuard.Data.Repositories;

namespace TelemetryGuard.Api.Workers;

/// <summary>
/// P2-02: observability for promotions — it NEVER applies one. Every
/// Scoring:Registry:PollMinutes it compares dbo.ModelRegistry's promoted row against
/// what THIS process loaded (ServingModelState) and logs a warning + increments
/// tg.model.promotion_pending when they differ. The enforcing scorer is fixed for the
/// lifetime of the process (D18 lineage: one process, one scorer_version stamp);
/// applying a promotion is a restart, performed by a human.
/// </summary>
public sealed class ModelRegistryWatcher(
    IModelRegistryRepository registry,
    ServingModelState loaded,
    IOptions<ModelRegistryOptions> options,
    IConfiguration configuration,
    ILogger<ModelRegistryWatcher> log) : BackgroundService
{
    private static readonly Meter Meter = new("TelemetryGuard.ModelRegistry");
    private static readonly Counter<long> PromotionPending = Meter.CreateCounter<long>("tg.model.promotion_pending");
    private static readonly Counter<long> ReadFailures = Meter.CreateCounter<long>("tg.model.registry_read_failures");

    private string? _lastReportedVersion = loaded.ScorerVersion;

    /// <summary>Scoring:ModelSource must be "Registry" for the comparison to be
    /// meaningful — under RSK-08 pilot mode (Scoring:ModelSource=Config) the registry
    /// is not what this process loaded, so comparing them would just produce false
    /// alarms every poll.</summary>
    private bool RegistryDriven => string.Equals(
        configuration["Scoring:ModelSource"], "Registry", StringComparison.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!RegistryDriven)
        {
            log.LogDebug("ModelRegistryWatcher: no-op (Scoring:ModelSource != \"Registry\" — RSK-08 pilot mode).");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.Value.PollMinutes)));
        do { await RunOnceAsync(stoppingToken); }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One comparison pass. Public for tests. Never throws except on
    /// shutdown cancellation — a registry read failure is logged/counted and swallowed
    /// so the host keeps running with whatever it already loaded.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            var serving = await registry.GetServingModelAsync(ct).ConfigureAwait(false);
            var registryVersion = serving?.ScorerVersion;
            var registryStatus = serving?.Status;

            if (string.Equals(registryVersion, loaded.ScorerVersion, StringComparison.Ordinal))
            {
                return; // registry agrees with what this process loaded — nothing pending
            }

            if (string.Equals(registryVersion, _lastReportedVersion, StringComparison.Ordinal))
            {
                return; // already warned about this exact pending version — avoid log spam
            }

            _lastReportedVersion = registryVersion;
            PromotionPending.Add(1);
            log.LogWarning(
                "Model promotion pending: registry serves {RegistryVersion} as '{RegistryStatus}', " +
                "this process serves {LoadedVersion} — restart the host to apply.",
                registryVersion ?? "(none)", registryStatus ?? "(none)", loaded.ScorerVersion ?? "(none — heuristic)");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // shutdown — propagate so the host loop stops cleanly
        }
        catch (Exception ex)
        {
            ReadFailures.Add(1);
            log.LogError(ex, "ModelRegistryWatcher: registry read failed; the host keeps running with the loaded model.");
        }
    }
}
