// iplegence provider (D24) — the default IP-intelligence dataset.
//
// One memory-mapped, MaxMind-compatible database (`Superior-IP.mmdb`, built daily by
// https://github.com/shafqat-a/iplegence) replaces the GeoLite2 City + GeoLite2 ASN +
// IP2Proxy PX trio: it merges IPinfo Lite, sapics, iptoasn, GeoLite2/DB-IP city data,
// OpenProxyDB, Tor exit lists, iCloud Private Relay ranges and the official AWS / GCP /
// Azure / Cloudflare ranges into a single file. Lookups stay in-process and
// memory-mapped, so the < 50 ms scoring budget (D3) is untouched.
//
// Matching appsettings.json snippet:
//
//   "IpEnrichment": {
//     "Provider": "Iplegence",
//     "DataDir": "./data/geo",
//     "IplegenceDb": "Superior-IP.mmdb",
//     "PrivateRelayCsv": "apple-private-relay.csv",
//     "RefreshCheckHours": 6
//   }
//
// The file is downloaded by scripts/update-iplegence.sh; ReloadIfChanged hot-swaps the
// reader when it changes on disk. A missing file degrades to null fields (D13).
//
// Field mapping (iplegence record → IpEnrichment), all deliberate:
//
//   country.iso_code                                   → CountryCode
//   city.names.en                                      → City
//   location.latitude/longitude/time_zone              → Latitude/Longitude/TimeZone
//   asn.autonomous_system_number/_organization         → AsnNumber/AsnOrganization
//   usage_type (residential/mobile/business/education/
//     government/hosting), else is_cdn → Cdn, else
//     is_hosting_provider → Datacenter, else the
//     datacenter-ASN seed                              → AsnType
//   AsnType is Datacenter or Cdn                       → IsDatacenter
//   is_anonymous_vpn | is_public_proxy |
//     is_tor_exit_node | is_anonymous                  → IsProxyOrVpn (RAW; the Private
//                                                        Relay carve-out is RSK-04's job)
//   is_tor_exit_node                                   → IsTor
//   is_relay | Apple egress list                       → IsPrivateRelay
//
// is_hosting_provider deliberately does NOT feed IsProxyOrVpn: hosting has its own
// feature (ip_datacenter_asn) and its own paid-click T1 rule, so folding it into the
// proxy signal too would double-count the same fact. (The MaxMind provider's IP2Proxy
// mapping does count "DCH" as a proxy type — that difference is intentional and
// documented in D24.)

using System.Collections.Frozen;
using System.Net;
using MaxMind.Db;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TelemetryGuard.RiskEngine.Contracts;

namespace TelemetryGuard.RiskEngine.Enrichment.Providers;

public sealed class IplegenceIpIntelligenceProvider : IIpIntelligenceProvider
{
    private readonly IpEnrichmentOptions _options;
    private readonly ILogger<IplegenceIpIntelligenceProvider> _logger;
    private readonly FrozenSet<long> _datacenterAsns;
    private readonly PrivateRelaySource _privateRelay;

    // Swapped atomically; read once per lookup via Volatile.Read.
    private DatabaseHandle _handle;
    private int _disposed;

    public IplegenceIpIntelligenceProvider(
        IOptions<IpEnrichmentOptions> options,
        ILogger<IplegenceIpIntelligenceProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
        _datacenterAsns = AsnClassifier.LoadDatacenterAsnSeed();
        _privateRelay = new PrivateRelaySource(
            Path.Combine(_options.DataDir, _options.PrivateRelayCsv), logger);
        _handle = OpenDatabase();
    }

    public string Name => "Iplegence";

    /// <summary>The active database handle (exposed for tests).</summary>
    internal DatabaseHandle CurrentHandle => Volatile.Read(ref _handle);

    public IpEnrichment Lookup(IPAddress address)
    {
        var handle = Volatile.Read(ref _handle);
        var isPrivateRelay = _privateRelay.Contains(address);

        if (handle.Reader is not { } reader)
        {
            // No database at all: every dataset-derived field is unknown, not false (D13).
            return isPrivateRelay ? PrivateRelayOnly : IpEnrichment.Empty;
        }

        IplegenceRecord? record;
        try
        {
            record = reader.Find<IplegenceRecord>(address);
        }
        catch
        {
            return isPrivateRelay ? PrivateRelayOnly : IpEnrichment.Empty; // degraded lookup (D13)
        }

        if (record is null)
        {
            // Address not covered by the build — unknown, which is not the same as clean.
            return isPrivateRelay ? PrivateRelayOnly : IpEnrichment.Empty;
        }

        // A found row means the flags ARE known: iplegence omits false traits, so an
        // absent traits map is "no source asserted anything", i.e. all false.
        var traits = record.Traits ?? IplegenceTraits.None;
        var asnNumber = record.Asn?.Number;
        var usageType = Trimmed(traits.UsageType);
        var asnType = AsnClassifier.FromTraits(
            usageType, traits.IsCdn, traits.IsHostingProvider, asnNumber, _datacenterAsns);

        return new IpEnrichment
        {
            CountryCode = Trimmed(record.Country?.IsoCode),
            City = record.City?.EnglishName,
            Latitude = record.Location?.Latitude,
            Longitude = record.Location?.Longitude,
            TimeZone = Trimmed(record.Location?.TimeZone),
            AsnNumber = asnNumber,
            AsnOrganization = Trimmed(record.Asn?.Organization),
            AsnType = asnType,
            IsProxyOrVpn = traits.IsAnonymousVpn || traits.IsPublicProxy || traits.IsTorExitNode || traits.IsAnonymous,
            IsTor = traits.IsTorExitNode,
            // Derived from the classification rather than recomputed, so the two can
            // never disagree. It follows every precedence rule in AsnClassifier for free:
            // usage_type "hosting" promotes a row the prefix flags missed, and a row the
            // dataset typed residential/mobile/education is NOT a datacenter even when its
            // ASN appears in the hand-maintained seed list (the weakest signal we hold).
            IsDatacenter = asnType is AsnType.Datacenter or AsnType.Cdn,
            IsPrivateRelay = traits.IsRelay || isPrivateRelay,
        };
    }

    public bool ReloadIfChanged()
    {
        var reloaded = false;

        try
        {
            var current = CurrentHandle;
            if (File.GetLastWriteTimeUtc(DatabasePath) != current.MtimeUtc)
            {
                var next = OpenDatabase();
                var old = Interlocked.Exchange(ref _handle, next);
                GraceDisposal.DisposeLater(old.Reader);
                _logger.LogInformation(
                    "iplegence database changed on disk at {Path} — swapped in a fresh reader", DatabasePath);
                reloaded = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "iplegence database refresh failed — keeping the current reader");
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return; // idempotent: both the DI container and IpEnrichmentService dispose us
        Volatile.Read(ref _handle).Reader?.Dispose();
    }

    private string DatabasePath => Path.Combine(_options.DataDir, _options.IplegenceDb);

    /// <summary>Opens the database if it exists; a missing or unreadable file logs one
    /// warning and yields a null reader — never throws (degradation, D13).</summary>
    private DatabaseHandle OpenDatabase()
    {
        var path = DatabasePath;
        var mtime = File.GetLastWriteTimeUtc(path);

        if (!File.Exists(path))
        {
            _logger.LogWarning(
                "iplegence database {Path} not found — enrichment fields will be null (scores degrade gracefully). "
                + "Run scripts/update-iplegence.sh to fetch it.", path);
            return new DatabaseHandle(null, mtime);
        }

        try
        {
            var reader = new Reader(path, FileAccessMode.MemoryMapped);
            _logger.LogInformation(
                "iplegence database opened: {Path} (type {DatabaseType}, built {BuildDate:u})",
                path, reader.Metadata.DatabaseType, reader.Metadata.BuildDate);
            return new DatabaseHandle(reader, mtime);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "iplegence database {Path} could not be opened — enrichment fields will be null (scores degrade gracefully)",
                path);
            return new DatabaseHandle(null, mtime);
        }
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Everything unknown except the Private Relay flag, which comes from the
    /// embedded/on-disk Apple egress list and is therefore known even with no database.</summary>
    private static readonly IpEnrichment PrivateRelayOnly = new() { IsPrivateRelay = true };

    /// <summary>The open reader plus the file mtime it was opened from, swapped as one unit.</summary>
    internal sealed record DatabaseHandle(Reader? Reader, DateTime MtimeUtc);
}
