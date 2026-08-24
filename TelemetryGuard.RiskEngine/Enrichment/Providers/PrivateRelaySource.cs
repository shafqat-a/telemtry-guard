using System.Net;
using Microsoft.Extensions.Logging;

namespace TelemetryGuard.RiskEngine.Enrichment.Providers;

/// <summary>Owns the Apple iCloud Private Relay egress list for a provider: prefers the
/// refreshed on-disk CSV (scripts/update-geoip.sh), falls back to the embedded seed, and
/// reloads when the file's mtime changes. Shared by every provider — iplegence carries a
/// native <c>is_relay</c> trait but the list still covers addresses its build missed, and
/// it is the only source of the flag when no database file is present at all.</summary>
internal sealed class PrivateRelaySource
{
    private readonly string _path;
    private readonly ILogger _logger;

    private PrivateRelayMatcher _matcher;
    private DateTime _mtimeUtc;

    public PrivateRelaySource(string path, ILogger logger)
    {
        _path = path;
        _logger = logger;
        _mtimeUtc = File.GetLastWriteTimeUtc(path);
        _matcher = Load();
    }

    public bool Contains(IPAddress address) => Volatile.Read(ref _matcher).Contains(address);

    /// <summary>Reloads the list when the CSV changed on disk. Returns true when reloaded.</summary>
    public bool ReloadIfChanged()
    {
        var mtime = File.GetLastWriteTimeUtc(_path);
        if (mtime == _mtimeUtc)
            return false;

        _mtimeUtc = mtime;
        Volatile.Write(ref _matcher, Load());
        _logger.LogInformation("Apple Private Relay ranges reloaded from {Path}", _path);
        return true;
    }

    private PrivateRelayMatcher Load()
    {
        if (File.Exists(_path))
        {
            try
            {
                return PrivateRelayMatcher.LoadCsv(_path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Private Relay CSV {Path} could not be loaded — falling back to the embedded seed", _path);
            }
        }
        return PrivateRelayMatcher.LoadEmbedded();
    }
}
