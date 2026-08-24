// MaxMind/IP2Location provider (RSK-02's original implementation, now behind the D24
// seam). Memory-mapped GeoLite2 City + ASN, memory-mapped IP2Proxy LITE PX, plus the
// Apple Private Relay egress list. Missing database files degrade to null fields (D13).
//
// Matching appsettings.json snippet:
//
//   "IpEnrichment": {
//     "Provider": "MaxMind",
//     "DataDir": "./data/geo",
//     "CityDb": "GeoLite2-City.mmdb",
//     "AsnDb": "GeoLite2-ASN.mmdb",
//     "ProxyDb": "IP2PROXY-LITE-PX11.BIN",
//     "PrivateRelayCsv": "apple-private-relay.csv",
//     "RefreshCheckHours": 6
//   }
//
// Databases are downloaded by scripts/update-geoip.sh (MAXMIND_LICENSE_KEY env var);
// ReloadIfChanged hot-swaps readers when files change on disk.

using System.Collections.Frozen;
using System.Net;
using MaxMind.Db;
using MaxMind.GeoIP2;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TelemetryGuard.RiskEngine.Contracts;

namespace TelemetryGuard.RiskEngine.Enrichment.Providers;

public sealed class MaxMindIpIntelligenceProvider : IIpIntelligenceProvider
{
    private readonly IpEnrichmentOptions _options;
    private readonly ILogger<MaxMindIpIntelligenceProvider> _logger;
    private readonly FrozenSet<long> _datacenterAsns;
    private readonly FrozenSet<long> _cdnAsns;
    private readonly PrivateRelaySource _privateRelay;

    // Swapped atomically; read once per lookup via Volatile.Read. (The task sketch says
    // `volatile` — Volatile.Read/Interlocked.Exchange is the equivalent pattern without
    // the CS0420 by-ref-to-volatile warning.)
    private ReaderSet _readers;
    private int _disposed;

    public MaxMindIpIntelligenceProvider(
        IOptions<IpEnrichmentOptions> options,
        ILogger<MaxMindIpIntelligenceProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
        _datacenterAsns = AsnClassifier.LoadDatacenterAsnSeed();
        _cdnAsns = AsnClassifier.LoadCdnAsnSeed();
        _privateRelay = new PrivateRelaySource(
            Path.Combine(_options.DataDir, _options.PrivateRelayCsv), logger);
        _readers = LoadReaders();
    }

    public string Name => "MaxMind";

    /// <summary>The active reader set (exposed for refresh-driven mtime checks and tests).</summary>
    internal ReaderSet CurrentReaders => Volatile.Read(ref _readers);

    public IpEnrichment Lookup(IPAddress address)
    {
        var readers = Volatile.Read(ref _readers);

        string? countryCode = null, cityName = null, timeZone = null, asnOrganization = null;
        double? latitude = null, longitude = null;
        long? asnNumber = null;
        string? usageType = null; // stays null iff the PX database is unavailable
        bool? isProxyOrVpn = null, isTor = null;

        if (readers.City is { } cityReader)
        {
            try
            {
                if (cityReader.TryCity(address, out var city) && city is not null)
                {
                    countryCode = city.Country.IsoCode;
                    cityName = city.City.Name;
                    latitude = city.Location.Latitude;
                    longitude = city.Location.Longitude;
                    timeZone = city.Location.TimeZone;
                }
            }
            catch
            {
                // degraded lookup — leave fields null (D13)
            }
        }

        if (readers.Asn is { } asnReader)
        {
            try
            {
                if (asnReader.TryAsn(address, out var asn) && asn is not null)
                {
                    asnNumber = asn.AutonomousSystemNumber;
                    asnOrganization = asn.AutonomousSystemOrganization;
                }
            }
            catch
            {
                // degraded lookup — leave fields null (D13)
            }
        }

        if (readers.Proxy is { } proxyReader)
        {
            try
            {
                var px = proxyReader.GetAll(address.ToString());
                usageType = px.Usage_Type ?? string.Empty;
                var proxyType = px.Proxy_Type;
                var isProxy = px.Is_Proxy > 0;
                isTor = string.Equals(proxyType, "TOR", StringComparison.Ordinal);
                // RAW value — the Apple Private Relay carve-out is applied downstream (RSK-04).
                isProxyOrVpn = isProxy && proxyType is "VPN" or "PUB" or "WEB" or "TOR" or "DCH" or "SES";
            }
            catch
            {
                // degraded lookup — leave flags null (D13)
            }
        }

        var asnType = AsnClassifier.FromUsageType(usageType, asnNumber, _datacenterAsns, _cdnAsns);

        // Derived from the classification (same rule as the iplegence provider, D24) so
        // the flag and the type can never disagree; null only when nothing was looked up.
        bool? isDatacenter = usageType is null && asnNumber is null
            ? null // PX db missing AND ASN unavailable
            : asnType is AsnType.Datacenter or AsnType.Cdn;

        return new IpEnrichment
        {
            CountryCode = countryCode,
            City = cityName,
            Latitude = latitude,
            Longitude = longitude,
            TimeZone = timeZone,
            AsnNumber = asnNumber,
            AsnOrganization = asnOrganization,
            AsnType = asnType,
            IsProxyOrVpn = isProxyOrVpn,
            IsTor = isTor,
            IsDatacenter = isDatacenter,
            IsPrivateRelay = _privateRelay.Contains(address),
        };
    }

    public bool ReloadIfChanged()
    {
        var reloaded = false;

        try
        {
            var current = CurrentReaders;
            var changed = File.GetLastWriteTimeUtc(CityPath) != current.CityMtimeUtc
                || File.GetLastWriteTimeUtc(AsnPath) != current.AsnMtimeUtc
                || File.GetLastWriteTimeUtc(ProxyPath) != current.ProxyMtimeUtc;
            if (changed)
            {
                SwapReaders(LoadReaders());
                _logger.LogInformation(
                    "GeoLite2/IP2Proxy databases changed on disk under {DataDir} — swapped in fresh readers",
                    _options.DataDir);
                reloaded = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Geo database refresh failed — keeping the current readers");
        }

        try
        {
            reloaded |= _privateRelay.ReloadIfChanged();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Private Relay list refresh failed — keeping the current list");
        }

        return reloaded;
    }

    /// <summary>Opens whichever database files exist on disk; missing/corrupt files log
    /// one warning each and yield a null reader — never throws (degradation, D13).</summary>
    internal ReaderSet LoadReaders() =>
        new()
        {
            City = OpenMaxMind(CityPath),
            Asn = OpenMaxMind(AsnPath),
            Proxy = OpenProxy(ProxyPath),
            CityMtimeUtc = File.GetLastWriteTimeUtc(CityPath),
            AsnMtimeUtc = File.GetLastWriteTimeUtc(AsnPath),
            ProxyMtimeUtc = File.GetLastWriteTimeUtc(ProxyPath),
        };

    /// <summary>Atomically publishes a new reader set; the old one is disposed after a
    /// grace period so in-flight lookups on the old memory-mapped readers can finish.</summary>
    internal void SwapReaders(ReaderSet next)
    {
        var old = Interlocked.Exchange(ref _readers, next);
        if (ReferenceEquals(old, next))
            return;
        GraceDisposal.DisposeLater(old);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return; // idempotent: both the DI container and IpEnrichmentService dispose us
        Volatile.Read(ref _readers).Dispose();
    }

    private string CityPath => Path.Combine(_options.DataDir, _options.CityDb);
    private string AsnPath => Path.Combine(_options.DataDir, _options.AsnDb);
    private string ProxyPath => Path.Combine(_options.DataDir, _options.ProxyDb);

    private DatabaseReader? OpenMaxMind(string path)
    {
        if (!File.Exists(path))
        {
            _logger.LogWarning(
                "GeoIP database {Path} not found — enrichment fields will be null (scores degrade gracefully)", path);
            return null;
        }

        try
        {
            return new DatabaseReader(path, FileAccessMode.MemoryMapped);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "GeoIP database {Path} could not be opened — enrichment fields will be null (scores degrade gracefully)", path);
            return null;
        }
    }

    private IP2Proxy.Component? OpenProxy(string path)
    {
        if (!File.Exists(path))
        {
            _logger.LogWarning(
                "GeoIP database {Path} not found — enrichment fields will be null (scores degrade gracefully)", path);
            return null;
        }

        try
        {
            var component = new IP2Proxy.Component();
            var result = component.Open(path, IP2Proxy.Component.IOModes.IP2PROXY_MEMORY_MAPPED);
            if (result != 0)
            {
                component.Close();
                _logger.LogWarning(
                    "IP2Proxy database {Path} failed to open (code {Code}) — proxy flags will be null (scores degrade gracefully)",
                    path, result);
                return null;
            }
            return component;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "IP2Proxy database {Path} could not be opened — proxy flags will be null (scores degrade gracefully)", path);
            return null;
        }
    }
}
