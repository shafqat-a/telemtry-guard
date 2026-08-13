using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TelemetryGuard.RiskEngine.Enrichment;

/// <summary>Polls the geo database file mtimes every RefreshCheckHours; when the weekly
/// scripts/update-geoip.sh has replaced a file, builds a fresh ReaderSet and swaps it in
/// atomically. On load failure the old set keeps serving — refresh never degrades a
/// working service.</summary>
public sealed class GeoDataRefreshService : BackgroundService
{
    private readonly IpEnrichmentService _enrichment;
    private readonly IpEnrichmentOptions _options;
    private readonly ILogger<GeoDataRefreshService> _logger;
    private DateTime _privateRelayMtimeUtc;

    public GeoDataRefreshService(
        IpEnrichmentService enrichment,
        IOptions<IpEnrichmentOptions> options,
        ILogger<GeoDataRefreshService> logger)
    {
        _enrichment = enrichment;
        _options = options.Value;
        _logger = logger;
        _privateRelayMtimeUtc = File.GetLastWriteTimeUtc(PrivateRelayPath);
    }

    private string PrivateRelayPath => Path.Combine(_options.DataDir, _options.PrivateRelayCsv);

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
                RefreshDatabasesIfChanged();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Geo database refresh failed — keeping the current readers");
            }

            try
            {
                RefreshPrivateRelayIfChanged();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Private Relay list refresh failed — keeping the current list");
            }
        }
    }

    private void RefreshDatabasesIfChanged()
    {
        var current = _enrichment.CurrentReaders;
        var cityPath = Path.Combine(_options.DataDir, _options.CityDb);
        var asnPath = Path.Combine(_options.DataDir, _options.AsnDb);
        var proxyPath = Path.Combine(_options.DataDir, _options.ProxyDb);

        var changed = File.GetLastWriteTimeUtc(cityPath) != current.CityMtimeUtc
            || File.GetLastWriteTimeUtc(asnPath) != current.AsnMtimeUtc
            || File.GetLastWriteTimeUtc(proxyPath) != current.ProxyMtimeUtc;
        if (!changed)
            return;

        var next = _enrichment.LoadReaders();
        _enrichment.SwapReaders(next);
        _logger.LogInformation("Geo databases changed on disk under {DataDir} — swapped in fresh readers", _options.DataDir);
    }

    private void RefreshPrivateRelayIfChanged()
    {
        var mtime = File.GetLastWriteTimeUtc(PrivateRelayPath);
        if (mtime == _privateRelayMtimeUtc)
            return;

        _privateRelayMtimeUtc = mtime;
        _enrichment.ReloadPrivateRelay();
        _logger.LogInformation("Apple Private Relay ranges reloaded from {Path}", PrivateRelayPath);
    }
}
