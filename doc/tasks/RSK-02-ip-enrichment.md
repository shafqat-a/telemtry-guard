---
id: RSK-02
title: "IP enrichment (GeoIP, ASN, proxy, Tor, Private Relay)"
phase: 1
workstream: risk
depends_on: [FND-01]
size: M
spec_refs: [D3, D13, "§7"]
detail_level: full
---

# RSK-02: IP enrichment (GeoIP, ASN, proxy, Tor, Private Relay)

## Objective

Implement `IIpEnrichmentService` in `TelemetryGuard.RiskEngine`: a synchronous, in-process, microsecond-latency lookup that turns an IP address into an `IpEnrichment` record (country, city, coordinates, timezone, ASN, `AsnType` classification, proxy/VPN/Tor/datacenter flags, Apple Private Relay flag). Backed by memory-mapped MaxMind GeoLite2 databases and the IP2Proxy LITE database, refreshed weekly by a background service with an atomic reader swap, and by an embedded Apple Private Relay egress-range list. Missing database files must degrade to null fields (NaN downstream), never crash the service.

## Spec context (self-contained)

- D3: enrichment runs **in-process** — `MaxMind.GeoIP2` memory-maps GeoLite2 City + ASN for microsecond lookups; IP2Location/IP2Proxy LITE PX supplies proxy/VPN flags; a weekly refresh job updates databases. No network calls in the request path; the whole scoring path has a < 50 ms budget.
- Spec §7 signals produced from this task's output: `ip_tor` (T1, paid-only rule), `ip_datacenter_asn` (T1, paid-only rule), `ip_proxy_or_vpn` (T2 — **Apple Private Relay carved out as CTX `is_private_relay`**, because Private Relay users are legitimate Safari users, not fraud), `asn_type` (CTX, one-hot; used to normalize `device_ids_this_ip_hour` — carrier-grade NAT means one mobile IP legitimately fronts many devices), `ip_geo_target_mismatch` / `timezone_ip_mismatch` / `language_geo_mismatch` (computed later in RSK-04 from the geo fields returned here).
- Degradation is a design principle (D13 precedent): a missing signal becomes NaN/null and the scorer tolerates it. A missing `.mmdb`/`.BIN` file at startup logs a warning and yields null enrichment fields — the process MUST still start and serve traffic.
- Databases live on disk, downloaded by an ops script using the `MAXMIND_LICENSE_KEY` environment variable (GeoLite2 requires a free MaxMind account/license key).

## Prerequisites

- FND-01 created `TelemetryGuard.sln` including `TelemetryGuard.RiskEngine` (classlib, `net8.0`) and `tests/TelemetryGuard.Tests.Unit`. If `TelemetryGuard.RiskEngine.csproj` is missing, create it and `dotnet sln add` it. Check the FND-01 task file for folder layout (root vs `src/`).
- RSK-01's `AsnType` enum is NOT required at build time here only if RSK-01 is unfinished — but this task's `depends_on` is only FND-01, so define nothing from RSK-01; instead reference `TelemetryGuard.RiskEngine.Contracts` **if it already exists** for `AsnType`. If `TelemetryGuard.RiskEngine.Contracts` with `AsnType` is not yet present, create the enum exactly as specified in the RSK-01 task file inside that Contracts project (do not duplicate it in RiskEngine). Add a `ProjectReference` from `TelemetryGuard.RiskEngine` to `TelemetryGuard.RiskEngine.Contracts`.

## Implementation steps

1. Add NuGet packages to `TelemetryGuard.RiskEngine.csproj`:
   - `MaxMind.GeoIP2` (5.x) — GeoLite2 City/ASN reader.
   - `IP2Proxy` (the official IP2Location .NET component) — reads `IP2PROXY-LITE-PXxx.BIN`. NOTE: member names below (`Component`, `GetAll`, `Proxy_Type`, `Usage_Type`) follow the package's documented API; verify against the installed version and adapt locally inside the wrapper class only.
   - `Microsoft.Extensions.Hosting.Abstractions`, `Microsoft.Extensions.Options.ConfigurationExtensions` (for `BackgroundService` + options binding) if not already transitively available.

2. Create `TelemetryGuard.RiskEngine/Enrichment/IpEnrichment.cs`:

```csharp
using TelemetryGuard.RiskEngine.Contracts;

namespace TelemetryGuard.RiskEngine.Enrichment;

/// <summary>Everything we know about an IP. Null members = the backing database was
/// unavailable or had no row — downstream this becomes NaN/null features, never 0/false.</summary>
public sealed record IpEnrichment
{
    public string? CountryCode { get; init; }      // ISO 3166-1 alpha-2, e.g. "DE"
    public string? City { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? TimeZone { get; init; }         // IANA, e.g. "Europe/Berlin"
    public long? AsnNumber { get; init; }
    public string? AsnOrganization { get; init; }
    public AsnType AsnType { get; init; } = AsnType.Unknown;
    public bool? IsProxyOrVpn { get; init; }       // null = PX db missing; RAW value — Private Relay carve-out applied in RSK-04
    public bool? IsTor { get; init; }              // null = PX db missing
    public bool? IsDatacenter { get; init; }       // null = both PX db and ASN unavailable
    public bool IsPrivateRelay { get; init; }      // embedded egress list — always known

    public static IpEnrichment Empty { get; } = new();
}

public interface IIpEnrichmentService
{
    /// <summary>Synchronous by design: memory-mapped in-process lookups (D3).
    /// Never throws for unparseable IPs — returns IpEnrichment.Empty.</summary>
    IpEnrichment Enrich(string ip);
}
```

3. Create `TelemetryGuard.RiskEngine/Enrichment/IpEnrichmentOptions.cs`, bound from config section `IpEnrichment`:

```csharp
public sealed class IpEnrichmentOptions
{
    public string DataDir { get; set; } = "./data/geo";
    public string CityDb { get; set; } = "GeoLite2-City.mmdb";
    public string AsnDb { get; set; } = "GeoLite2-ASN.mmdb";
    public string ProxyDb { get; set; } = "IP2PROXY-LITE-PX11.BIN";
    public string PrivateRelayCsv { get; set; } = "apple-private-relay.csv"; // optional refreshed copy
    public int RefreshCheckHours { get; set; } = 6;   // how often the refresh service polls file mtimes
}
```

Matching `appsettings.json` snippet (document in the file header of the service):

```json
"IpEnrichment": {
  "DataDir": "./data/geo",
  "CityDb": "GeoLite2-City.mmdb",
  "AsnDb": "GeoLite2-ASN.mmdb",
  "ProxyDb": "IP2PROXY-LITE-PX11.BIN",
  "PrivateRelayCsv": "apple-private-relay.csv",
  "RefreshCheckHours": 6
}
```

4. Create `TelemetryGuard.RiskEngine/Enrichment/ReaderSet.cs` — an immutable holder enabling atomic swap:

```csharp
internal sealed class ReaderSet : IDisposable
{
    public MaxMind.GeoIP2.DatabaseReader? City { get; init; }     // opened with MaxMind.Db.FileAccessMode.MemoryMapped
    public MaxMind.GeoIP2.DatabaseReader? Asn { get; init; }
    public IP2Proxy.Component? Proxy { get; init; }               // wrapper naming per installed package
    public DateTime CityMtimeUtc { get; init; }
    public DateTime AsnMtimeUtc { get; init; }
    public DateTime ProxyMtimeUtc { get; init; }
    public void Dispose() { City?.Dispose(); Asn?.Dispose(); /* Component.Close() per package API */ }
}
```

5. Create `TelemetryGuard.RiskEngine/Enrichment/IpEnrichmentService.cs` implementing `IIpEnrichmentService`:
   - Singleton. Holds `private volatile ReaderSet _readers;` — lookups read the field once into a local; the refresh service swaps it via a `SwapReaders(ReaderSet next)` internal method using `Interlocked.Exchange`, then disposes the OLD set after a 30-second `Task.Delay` (in-flight lookups on the old memory-mapped readers finish long before that).
   - `LoadReaders()` (also used at startup): for each configured file, if `File.Exists` open the reader (`new DatabaseReader(path, MaxMind.Db.FileAccessMode.MemoryMapped)`); if missing, log ONE warning per file (`"GeoIP database {Path} not found — enrichment fields will be null (scores degrade gracefully)"`) and leave that reader null. NEVER throw from construction.
   - `Enrich(string ip)`:
     - `IPAddress.TryParse` — failure → return `IpEnrichment.Empty`.
     - Private/loopback ranges (`IPAddress.IsLoopback`, RFC1918, link-local) → `Empty` (avoids noisy lookups in dev).
     - City: `readers.City?.TryCity(addr, out var city)` → `CountryCode = city.Country.IsoCode`, `City = city.City?.Name`, `Latitude/Longitude = city.Location...`, `TimeZone = city.Location.TimeZone`.
     - ASN: `readers.Asn?.TryAsn(addr, out var asn)` → `AsnNumber = asn.AutonomousSystemNumber`, `AsnOrganization = asn.AutonomousSystemOrganization`.
     - Proxy: when the PX reader is present call `GetAll(ip)`; map: `IsTor = proxyType == "TOR"`; `IsProxyOrVpn = isProxy && proxyType is "VPN" or "PUB" or "WEB" or "TOR" or "DCH" or "SES"` (raw value — RSK-04 applies the Private Relay carve-out); PX reader absent → all three flags null.
     - `AsnType` classification (precedence order):
       1. PX `Usage_Type` when present: `ISP`→`Residential`, `MOB`→`Mobile`, `COM`/`ORG`→`Business`, `DCH`→`Datacenter`, `EDU`/`LIB`→`Education`, `GOV`/`MIL`→`Government`, `CDN`→`Cdn`, else fall through.
       2. Else if `AsnNumber` is in the embedded datacenter seed list → `Datacenter`.
       3. Else `Unknown`.
     - `IsDatacenter = (usageType == "DCH") || seedList.Contains(AsnNumber)`; null only when PX missing AND AsnNumber null.
     - `IsPrivateRelay = _privateRelayMatcher.Contains(addr)` (always computable — embedded list).

6. Embedded datacenter ASN seed list — create `TelemetryGuard.RiskEngine/Enrichment/Data/datacenter-asns.txt` as an `<EmbeddedResource>`, one ASN per line with comment:

```
16509   # Amazon AWS
14618   # Amazon EC2 legacy
8075    # Microsoft Azure
15169   # Google
396982  # Google Cloud
14061   # DigitalOcean
16276   # OVH
24940   # Hetzner
63949   # Akamai/Linode
20473   # Vultr (Choopa)
51167   # Contabo
45102   # Alibaba Cloud
132203  # Tencent Cloud
13335   # Cloudflare
54113   # Fastly
```

Loader: read the embedded resource at construction into a `FrozenSet<long>` (`System.Collections.Frozen`), parsing the number before `#`.

7. Apple Private Relay ranges — create `TelemetryGuard.RiskEngine/Enrichment/Data/apple-private-relay-seed.csv` as `<EmbeddedResource>`. Format is Apple's published egress file (`https://mask-api.icloud.com/egress-ip-ranges.csv`): `CIDR,country,region,city` per line. Embed a snapshot of at least the well-known blocks (download once while implementing; if offline, seed with `172.224.224.0/20,` style lines and note the refresh script keeps it current). Create `PrivateRelayMatcher`:

```csharp
internal sealed class PrivateRelayMatcher
{
    // Parse CIDRs into (byte[] network, int prefix) buckets split by address family;
    // Contains(IPAddress) does prefix compare on address bytes. O(n) linear scan over a
    // few thousand ranges is fine (< 10 µs); no external packages.
    public static PrivateRelayMatcher LoadEmbedded();
    public static PrivateRelayMatcher LoadCsv(string path);   // refreshed copy from DataDir when present
    public bool Contains(System.Net.IPAddress ip);
}
```

Prefer the on-disk refreshed copy (`{DataDir}/apple-private-relay.csv`) when it exists, else the embedded seed.

8. Create `TelemetryGuard.RiskEngine/Enrichment/GeoDataRefreshService.cs : BackgroundService`:
   - Loop: `await Task.Delay(TimeSpan.FromHours(opts.RefreshCheckHours), stoppingToken)`; compare `File.GetLastWriteTimeUtc` of each configured DB against the mtimes recorded in the current `ReaderSet`; when any changed (the weekly cron/script replaced a file), build a full new `ReaderSet` via `LoadReaders()` and call `SwapReaders` (atomic). Also reload the Private Relay CSV. Log at Information on swap, Warning on load failure (keep serving the old set on failure).

9. Create `scripts/update-geoip.sh` (mark executable):

```bash
#!/usr/bin/env bash
# Downloads GeoLite2 City+ASN (requires MAXMIND_LICENSE_KEY), IP2Proxy LITE PX11
# (requires IP2LOCATION_TOKEN, optional), and Apple Private Relay egress ranges.
# Run weekly (cron/systemd timer); the app hot-swaps readers when files change.
set -euo pipefail
: "${MAXMIND_LICENSE_KEY:?Set MAXMIND_LICENSE_KEY (free GeoLite2 account)}"
DATA_DIR="${1:-./data/geo}"
mkdir -p "$DATA_DIR"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT

for ED in GeoLite2-City GeoLite2-ASN; do
  curl -fsSL "https://download.maxmind.com/app/geoip_download?edition_id=${ED}&license_key=${MAXMIND_LICENSE_KEY}&suffix=tar.gz" -o "$TMP/${ED}.tar.gz"
  tar -xzf "$TMP/${ED}.tar.gz" -C "$TMP"
  MMDB="$(find "$TMP" -name "${ED}.mmdb" | head -1)"
  cp "$MMDB" "$DATA_DIR/${ED}.mmdb.tmp" && mv "$DATA_DIR/${ED}.mmdb.tmp" "$DATA_DIR/${ED}.mmdb"   # atomic within one fs
done

if [ -n "${IP2LOCATION_TOKEN:-}" ]; then
  curl -fsSL "https://www.ip2location.com/download/?token=${IP2LOCATION_TOKEN}&file=PX11LITEBIN" -o "$TMP/px.zip"
  unzip -o "$TMP/px.zip" -d "$TMP/px" >/dev/null
  BIN="$(find "$TMP/px" -name '*.BIN' | head -1)"
  cp "$BIN" "$DATA_DIR/IP2PROXY-LITE-PX11.BIN.tmp" && mv "$DATA_DIR/IP2PROXY-LITE-PX11.BIN.tmp" "$DATA_DIR/IP2PROXY-LITE-PX11.BIN"
fi

curl -fsSL "https://mask-api.icloud.com/egress-ip-ranges.csv" -o "$DATA_DIR/apple-private-relay.csv.tmp" \
  && mv "$DATA_DIR/apple-private-relay.csv.tmp" "$DATA_DIR/apple-private-relay.csv"
echo "geo data updated in $DATA_DIR"
```

10. Create `TelemetryGuard.RiskEngine/Enrichment/EnrichmentServiceCollectionExtensions.cs`:

```csharp
public static IServiceCollection AddIpEnrichment(this IServiceCollection services, IConfiguration config)
{
    services.Configure<IpEnrichmentOptions>(config.GetSection("IpEnrichment"));
    services.AddSingleton<IpEnrichmentService>();
    services.AddSingleton<IIpEnrichmentService>(sp => sp.GetRequiredService<IpEnrichmentService>());
    services.AddHostedService<GeoDataRefreshService>();
    return services;
}
```

11. Add `./data/geo/` to `.gitignore` (databases are licensed downloads, never committed).

## Files to create or modify

- `TelemetryGuard.RiskEngine/TelemetryGuard.RiskEngine.csproj` (packages, embedded resources, project ref to Contracts)
- `TelemetryGuard.RiskEngine/Enrichment/IpEnrichment.cs`
- `TelemetryGuard.RiskEngine/Enrichment/IpEnrichmentOptions.cs`
- `TelemetryGuard.RiskEngine/Enrichment/ReaderSet.cs`
- `TelemetryGuard.RiskEngine/Enrichment/IpEnrichmentService.cs`
- `TelemetryGuard.RiskEngine/Enrichment/PrivateRelayMatcher.cs`
- `TelemetryGuard.RiskEngine/Enrichment/GeoDataRefreshService.cs`
- `TelemetryGuard.RiskEngine/Enrichment/EnrichmentServiceCollectionExtensions.cs`
- `TelemetryGuard.RiskEngine/Enrichment/Data/datacenter-asns.txt` (embedded resource)
- `TelemetryGuard.RiskEngine/Enrichment/Data/apple-private-relay-seed.csv` (embedded resource)
- `scripts/update-geoip.sh`
- `.gitignore` (add `data/geo/`)
- `tests/TelemetryGuard.Tests.Unit/Enrichment/*` (see Testing)

## Acceptance criteria

- `dotnet build` passes; service registers via `AddIpEnrichment` without any database files present and `Enrich("8.8.8.8")` returns a record with null geo/proxy fields, `AsnType.Unknown`, `IsPrivateRelay` computed — no exception, one warning log per missing file.
- `Enrich("not-an-ip")` and `Enrich("127.0.0.1")` return `IpEnrichment.Empty` without logging errors.
- AsnType mapping unit tests pass for every usage-type string listed in step 5 plus the seed-list fallback (e.g. ASN 24940 with no PX db → `Datacenter`).
- `PrivateRelayMatcher` unit tests: an IP inside an embedded CIDR → true; adjacent IP outside → false; works for both IPv4 and IPv6 rows.
- Reader swap test: calling `SwapReaders` from a second thread during a tight `Enrich` loop never throws and lookups always use a fully-formed set (no torn reads) — verified with a 10k-iteration stress unit test using null-reader sets.
- `bash scripts/update-geoip.sh` fails fast with a clear message when `MAXMIND_LICENSE_KEY` is unset (exit non-zero).
- `Enrich` allocates no more than the result record per call in the null-DB path (no LINQ in the hot path); a simple loop of 100k `Enrich` calls on `Empty` path completes < 1 s in Debug.

## Testing

- Unit tests in `tests/TelemetryGuard.Tests.Unit/Enrichment/`:
  - `AsnTypeClassificationTests` — table-driven over usage-type strings and seed ASNs.
  - `PrivateRelayMatcherTests` — CIDR membership incl. IPv6.
  - `MissingDatabaseTests` — construct service with an options `DataDir` pointing at an empty temp dir; assert null fields and no throw.
  - `ReaderSwapStressTests` — concurrency smoke per acceptance criteria.
- No Testcontainers here; MaxMind/IP2Proxy readers against real `.mmdb` files are exercised implicitly in later integration runs where ops have downloaded databases. Do not commit `.mmdb`/`.BIN` files to the repo.

## Out of scope / guardrails

- Do NOT block startup or throw when database files are missing — degrade to nulls (NaN downstream). This is the D13/D22 degradation principle; the model/heuristic tolerates missing signals.
- Do NOT make `Enrich` async or add any network I/O in the lookup path — in-process memory-mapped lookups only (D3, < 50 ms total scoring budget).
- Do NOT apply the Private Relay carve-out here — return RAW proxy flags plus `IsPrivateRelay`; RSK-04 (feature extraction) forces `ip_proxy_or_vpn=false` when `is_private_relay=true`. Keeping the raw value preserves information.
- Do NOT write enrichment results anywhere (no Redis, no SQL, no ClickHouse) — this is a pure lookup service; caching is RSK-07's concern.
- No cross-tenant anything: enrichment is tenant-independent reference data; do not take `ITenantContext` as a dependency.
- No server-side Python/Node (D1) — the refresh script is plain bash + curl; scheduled refresh logic lives in the .NET `BackgroundService`.
- Do not commit licensed database files or the MaxMind key; the key arrives only via the `MAXMIND_LICENSE_KEY` env var.
