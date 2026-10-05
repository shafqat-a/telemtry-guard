using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TelemetryGuard.Client;

/// <summary>
/// Emits one finalized MarketIQ click per TG page visit after the SDK has gone quiet.
/// Redis leases make it safe across replicas and after worker restarts.
/// </summary>
public sealed class TelemetryGuardVisitRelayWorker(
    ITelemetryGuardVisitQueue visits,
    ITelemetryGuardRelay relay,
    ILogger<TelemetryGuardVisitRelayWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var due = await visits.ClaimDueAsync(100, TimeSpan.FromMinutes(1), stoppingToken)
                    .ConfigureAwait(false);
                foreach (var visit in due)
                {
                    try
                    {
                        await relay.RelayAsync(visit.Submission, stoppingToken).ConfigureAwait(false);
                        await visits.CompleteAsync(visit, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await visits.RetryAsync(visit, DateTimeOffset.UtcNow.AddSeconds(10), stoppingToken)
                            .ConfigureAwait(false);
                        log.LogWarning(ex, "Finalized TG visit {EventId} could not be relayed.",
                            visit.Submission.EventId);
                    }
                }

                await Task.Delay(due.Count == 0 ? TimeSpan.FromSeconds(1) : TimeSpan.Zero,
                    stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "TG finalized-visit worker failed; Redis leases preserve visits.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
