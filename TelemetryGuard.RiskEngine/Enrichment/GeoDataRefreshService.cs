using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TelemetryGuard.RiskEngine.Enrichment.Providers;

namespace TelemetryGuard.RiskEngine.Enrichment;

/// <summary>Asks the active IP-intelligence provider to reload every RefreshCheckHours;
/// when an ops refresh script (scripts/update-iplegence.sh, scripts/update-geoip.sh) has
/// replaced a file on disk, the provider builds fresh readers and swaps them in
/// atomically. On load failure the old data keeps serving — refresh never degrades a
/// working service. Which files are watched, and how, is the provider's business (D24).</summary>
public sealed class GeoDataRefreshService : BackgroundService
{
    private readonly IIpIntelligenceProvider _provider;
    private readonly IpEnrichmentOptions _options;
    private readonly ILogger<GeoDataRefreshService> _logger;

    public GeoDataRefreshService(
        IIpIntelligenceProvider provider,
        IOptions<IpEnrichmentOptions> options,
        ILogger<GeoDataRefreshService> logger)
    {
        _provider = provider;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(Math.Max(1, _options.RefreshCheckHours));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                _provider.ReloadIfChanged();
            }
            catch (Exception ex)
            {
                // Providers already swallow their own failures; this is the backstop.
                _logger.LogWarning(ex,
                    "{Provider} IP data refresh failed — keeping the current data", _provider.Name);
            }
        }
    }
}
