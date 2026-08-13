// IP enrichment lookup service (spec D3, §7). Synchronous, in-process, microsecond
// latency: MaxMind GeoLite2 City/ASN are memory-mapped, IP2Proxy LITE PX is memory-
// mapped, Apple Private Relay ranges are an embedded/in-memory list. Missing database
// files degrade to null fields (NaN downstream) — the process always starts (D13).
//
// Matching appsettings.json snippet:
//
//   "IpEnrichment": {
//     "DataDir": "./data/geo",
//     "CityDb": "GeoLite2-City.mmdb",
//     "AsnDb": "GeoLite2-ASN.mmdb",
//     "ProxyDb": "IP2PROXY-LITE-PX11.BIN",
//     "PrivateRelayCsv": "apple-private-relay.csv",
//     "RefreshCheckHours": 6
//   }
//
// Databases are downloaded by scripts/update-geoip.sh (MAXMIND_LICENSE_KEY env var);
// GeoDataRefreshService hot-swaps readers when files change on disk.

using System.Collections.Frozen;
using System.Net;
using MaxMind.Db;
using MaxMind.GeoIP2;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TelemetryGuard.RiskEngine.Contracts;

namespace TelemetryGuard.RiskEngine.Enrichment;

public sealed class IpEnrichmentService : IIpEnrichmentService, IDisposable
{
    internal const string DatacenterAsnResourceName =
        "TelemetryGuard.RiskEngine.Enrichment.Data.datacenter-asns.txt";

    private static readonly TimeSpan DisposeGrace = TimeSpan.FromSeconds(30);

    private readonly IpEnrichmentOptions _options;
    private readonly ILogger<IpEnrichmentService> _logger;
    private readonly FrozenSet<long> _datacenterAsns;

    // Swapped atomically; read once per lookup via Volatile.Read. (The task sketch says
    // `volatile` — Volatile.Read/Interlocked.Exchange is the equivalent pattern without
    // the CS0420 by-ref-to-volatile warning.)
    private ReaderSet _readers;
    private PrivateRelayMatcher _privateRelay;

    public IpEnrichmentService(IOptions<IpEnrichmentOptions> options, ILogger<IpEnrichmentService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _datacenterAsns = LoadDatacenterAsnSeed();
        _privateRelay = LoadPrivateRelayMatcher();
        _readers = LoadReaders();
    }

    /// <summary>The active reader set (exposed for the refresh service's mtime checks).</summary>
    internal ReaderSet CurrentReaders => Volatile.Read(ref _readers);

    public IpEnrichment Enrich(string ip)
    {
        if (string.IsNullOrEmpty(ip) || !IPAddress.TryParse(ip, out var addr))
            return IpEnrichment.Empty;

        if (addr.IsIPv4MappedToIPv6)
            addr = addr.MapToIPv4();

        if (IsPrivateOrLocal(addr))
            return IpEnrichment.Empty; // avoids noisy lookups in dev

        var readers = Volatile.Read(ref _readers);
        var privateRelay = Volatile.Read(ref _privateRelay);

        string? countryCode = null, cityName = null, timeZone = null, asnOrganization = null;
        double? latitude = null, longitude = null;
        long? asnNumber = null;
        string? usageType = null; // stays null iff the PX database is unavailable
        bool? isProxyOrVpn = null, isTor = null;

        if (readers.City is { } cityReader)
        {
            try
            {
                if (cityReader.TryCity(addr, out var city) && city is not null)
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
                if (asnReader.TryAsn(addr, out var asn) && asn is not null)
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
                var px = proxyReader.GetAll(ip);
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

        var asnType = ClassifyAsnType(usageType, asnNumber, _datacenterAsns);

        bool? isDatacenter;
        if (usageType is null && asnNumber is null)
            isDatacenter = null; // PX db missing AND ASN unavailable
        else
            isDatacenter = string.Equals(usageType, "DCH", StringComparison.Ordinal)
                || (asnNumber is { } number && _datacenterAsns.Contains(number));

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
            IsPrivateRelay = privateRelay.Contains(addr),
        };
    }

    /// <summary>Maps IP2Proxy usage_type to AsnType, falling back to the embedded
    /// datacenter ASN seed list, then Unknown. Precedence per RSK-02 step 5.</summary>
    internal static AsnType ClassifyAsnType(string? usageType, long? asnNumber, FrozenSet<long> datacenterAsns)
    {
        if (usageType is not null)
        {
            switch (usageType)
            {
                case "ISP": return AsnType.Residential;
                case "MOB": return AsnType.Mobile;
                case "COM":
                case "ORG": return AsnType.Business;
                case "DCH": return AsnType.Datacenter;
                case "EDU":
                case "LIB": return AsnType.Education;
                case "GOV":
                case "MIL": return AsnType.Government;
                case "CDN": return AsnType.Cdn;
                // anything else ("SES", "RSV", "-", "") falls through to the seed list
            }
        }

        if (asnNumber is { } number && datacenterAsns.Contains(number))
            return AsnType.Datacenter;

        return AsnType.Unknown;
    }

    /// <summary>Opens whichever database files exist on disk; missing/corrupt files log
    /// one warning each and yield a null reader — never throws (degradation, D13).</summary>
    internal ReaderSet LoadReaders()
    {
        var cityPath = Path.Combine(_options.DataDir, _options.CityDb);
        var asnPath = Path.Combine(_options.DataDir, _options.AsnDb);
        var proxyPath = Path.Combine(_options.DataDir, _options.ProxyDb);

        return new ReaderSet
        {
            City = OpenMaxMind(cityPath),
            Asn = OpenMaxMind(asnPath),
            Proxy = OpenProxy(proxyPath),
            CityMtimeUtc = File.GetLastWriteTimeUtc(cityPath),
            AsnMtimeUtc = File.GetLastWriteTimeUtc(asnPath),
            ProxyMtimeUtc = File.GetLastWriteTimeUtc(proxyPath),
        };
    }

    /// <summary>Atomically publishes a new reader set; the old one is disposed after a
    /// grace period so in-flight lookups on the old memory-mapped readers can finish.</summary>
    internal void SwapReaders(ReaderSet next)
    {
        var old = Interlocked.Exchange(ref _readers, next);
        if (ReferenceEquals(old, next))
            return;
        _ = DisposeAfterGraceAsync(old);
    }

    /// <summary>Reloads the Private Relay list, preferring the refreshed on-disk copy.</summary>
    internal void ReloadPrivateRelay() =>
        Volatile.Write(ref _privateRelay, LoadPrivateRelayMatcher());

    internal static FrozenSet<long> LoadDatacenterAsnSeed()
    {
        using var stream = typeof(IpEnrichmentService).Assembly.GetManifestResourceStream(DatacenterAsnResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{DatacenterAsnResourceName}' is missing from the assembly.");
        using var reader = new StreamReader(stream);

        var asns = new HashSet<long>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var hash = line.IndexOf('#');
            var span = (hash >= 0 ? line.AsSpan(0, hash) : line.AsSpan()).Trim();
            if (span.Length == 0)
                continue;
            if (long.TryParse(span, out var asn))
                asns.Add(asn);
        }
        return asns.ToFrozenSet();
    }

    public void Dispose() => Volatile.Read(ref _readers).Dispose();

    private static async Task DisposeAfterGraceAsync(ReaderSet old)
    {
        try
        {
            await Task.Delay(DisposeGrace).ConfigureAwait(false);
            old.Dispose();
        }
        catch
        {
            // cleanup must never propagate
        }
    }

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

    private PrivateRelayMatcher LoadPrivateRelayMatcher()
    {
        var path = Path.Combine(_options.DataDir, _options.PrivateRelayCsv);
        if (File.Exists(path))
        {
            try
            {
                return PrivateRelayMatcher.LoadCsv(path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Private Relay CSV {Path} could not be loaded — falling back to the embedded seed", path);
            }
        }
        return PrivateRelayMatcher.LoadEmbedded();
    }

    /// <summary>Loopback, RFC1918, link-local, unique-local and unspecified addresses —
    /// never worth a database lookup.</summary>
    private static bool IsPrivateOrLocal(IPAddress addr)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!addr.TryWriteBytes(bytes, out var len))
            return true; // unrepresentable — treat as non-routable

        if (len == 4)
        {
            return bytes[0] == 127                              // loopback
                || bytes[0] == 10                               // RFC1918 10.0.0.0/8
                || (bytes[0] == 172 && (bytes[1] & 0xF0) == 16) // RFC1918 172.16.0.0/12
                || (bytes[0] == 192 && bytes[1] == 168)         // RFC1918 192.168.0.0/16
                || (bytes[0] == 169 && bytes[1] == 254)         // link-local 169.254.0.0/16
                || bytes[0] == 0;                               // 0.0.0.0/8
        }

        if (IPAddress.IsLoopback(addr))                         // ::1
            return true;
        if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)      // fe80::/10 link-local
            return true;
        if ((bytes[0] & 0xFE) == 0xFC)                          // fc00::/7 unique local
            return true;

        for (var i = 0; i < 16; i++)                            // :: unspecified
        {
            if (bytes[i] != 0)
                return false;
        }
        return true;
    }
}
