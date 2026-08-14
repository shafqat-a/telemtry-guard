using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using TelemetryGuard.Analytics.Kusto;

namespace TelemetryGuard.Api.Workers;

/// <summary>
/// D20 per-row retention for the Kusto provider (P2-05 step 8): Kusto has no
/// per-row TTL, so <see cref="KustoRetentionSweeper"/>'s soft-delete sweep is run
/// on a schedule from here. Registered ONLY inside the "Kusto" branch of the D7
/// provider switch (TelemetryGuard.Api/Analytics/AnalyticsServiceCollectionExtensions.cs)
/// — this worker never runs when the provider is ClickHouse. Shape mirrors the
/// house BackgroundService precedent (GoogleAdsExclusionSyncService): a
/// PeriodicTimer, run once at startup after a short delay, one try/catch per
/// cycle so a failure never kills the host.
/// </summary>
public sealed class KustoRetentionService(
    KustoRetentionSweeper sweeper,
    IOptions<KustoAnalyticsOptions> options,
    ILogger<KustoRetentionService> log) : BackgroundService
{
    private static readonly Meter Meter = new("TelemetryGuard.Analytics.Kusto");
    private static readonly Counter<long> Sweeps = Meter.CreateCounter<long>("tg.kusto.retention_sweeps");
    private static readonly Counter<long> SweepFailures = Meter.CreateCounter<long>("tg.kusto.retention_sweep_failures");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.RetentionSweepEnabled)
        {
            log.LogInformation("KustoRetentionService: RetentionSweepEnabled is false; sweep disabled");
            return;
        }

        try
        {
            // Run once at startup, after a short delay so the host finishes wiring up.
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // shut down before the first tick — nothing to do
        }

        var interval = TimeSpan.FromHours(Math.Max(0.1, opts.RetentionSweepIntervalHours));
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                var ok = await sweeper.SweepAsync(stoppingToken).ConfigureAwait(false);
                if (ok) Sweeps.Add(1);
                else SweepFailures.Add(1);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw; // shutdown — propagate so the host loop stops cleanly
            }
            catch (Exception ex)
            {
                SweepFailures.Add(1);
                log.LogError(ex, "KustoRetentionService: sweep cycle failed; the next tick retries");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
