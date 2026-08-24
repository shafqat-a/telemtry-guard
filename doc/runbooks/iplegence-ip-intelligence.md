# Runbook: iplegence IP intelligence (D24)

IP enrichment reads **one** memory-mapped database, `Superior-IP.mmdb`, built daily by
[iplegence](https://github.com/shafqat-a/iplegence) — a private Go pipeline that merges
IPinfo Lite, sapics, iptoasn, GeoLite2/DB-IP city data, OpenProxyDB, Tor exit lists,
iCloud Private Relay and the official AWS/GCP/Azure/Cloudflare ranges into a
MaxMind-compatible file (~130 MB).

iplegence is a **build-time data producer**. Nothing Go runs in production, and the API
never calls its HTTP API — a network hop inside the < 50 ms scoring budget (D3) would
force a fail-open/fail-closed decision on every timeout.

## Install / refresh the data

```bash
./scripts/update-iplegence.sh                  # → ./data/geo/Superior-IP.mmdb
./scripts/update-iplegence.sh /srv/tg/geo      # or an explicit directory
```

Sources are tried in this order; the first usable one wins:

| Env var | Use |
|---|---|
| `IPLEGENCE_MMDB_URL` | any HTTPS URL — release asset, Azure Blob SAS, CDN. A sibling `.sha256` is used when present |
| `IPLEGENCE_DIST_DIR` | a local iplegence checkout's `dist/` (dev machines) |
| *(neither set)* | `gh release download` from `$IPLEGENCE_REPO` (default `shafqat-a/iplegence`) — **private repo, needs `gh auth login`** |

The script verifies the published sha256, then moves the file into place atomically, so
the API never maps a half-written database. It also copies the build's `ATTRIBUTION.md`
to `IPLEGENCE-ATTRIBUTION.md` — **source credits ship with the data; never strip them.**
This is a merged free/open dataset: do not describe it as commercial-grade accuracy.

Run it daily (cron/systemd timer). iplegence publishes `vYYYY.MM.DD` at 01:00 UTC.

## How the app picks it up

`GeoDataRefreshService` polls file mtimes every `IpEnrichment:RefreshCheckHours` (6 by
default) and asks the provider to reload. A changed file is opened into a fresh reader
and swapped in atomically; the old reader is disposed after a 30 s grace so in-flight
lookups finish. **No restart, no redeploy.** If the new file fails to open, the previous
one keeps serving.

In Docker, `./data/geo` is bind-mounted read-only at `/app/data/geo`
(`docker-compose.app.yml`). Mount the *directory*, never the file — the atomic `mv`
replaces the inode, and a file mount would pin the container to the old one.

Local `dotnet run` uses the project directory as its working directory, so
`appsettings.Development.json` sets `IpEnrichment:DataDir` to `../data/geo`.

## Configuration

```json
"IpEnrichment": {
  "Provider": "Iplegence",        // or "MaxMind"; anything else aborts startup
  "DataDir": "./data/geo",
  "IplegenceDb": "Superior-IP.mmdb",
  "PrivateRelayCsv": "apple-private-relay.csv",
  "RefreshCheckHours": 6
}
```

**Every file is optional.** A missing database logs one warning and yields null
enrichment fields — the process always starts and keeps serving (D13). Nulls become NaN
features, which the scorer tolerates; they are *not* treated as clean.

## Rolling back to MaxMind

A bad daily build is a one-line change — set `IpEnrichment:Provider` to `MaxMind`
(env var `IpEnrichment__Provider=MaxMind`) and restart. That path needs
`scripts/update-geoip.sh` to have populated GeoLite2 City + ASN (and optionally
IP2PROXY-LITE-PX11). Alternatively, keep the provider and restore a known-good mmdb:

```bash
IPLEGENCE_MMDB_URL=https://…/v2026.08.13/Superior-IP.mmdb ./scripts/update-iplegence.sh
```

## Verifying a build before it goes anywhere near traffic

```bash
IPLEGENCE_MMDB="$PWD/data/geo/Superior-IP.mmdb" RUN_PERF_TESTS=1 \
  dotnet test tests/TelemetryGuard.Tests.Unit --filter FullyQualifiedName~RealIplegence
```

Use an absolute path — the test process runs from its own output directory.

That asserts the record layout still decodes, that known rows resolve (Google → US /
AS15169, Cloudflare → `AsnType.Cdn`, Comcast → `Residential`, T-Mobile → `Mobile`,
Berkeley → `Education`, and neither consumer network reported as a datacenter), and that
lookups stay in the microseconds. Run it in isolation — the timing half is meaningless
alongside the full parallel suite.

The record layout itself is pinned by a 3 KB fixture in
`tests/TelemetryGuard.Tests.Unit/Enrichment/Data/` (see the README there).

## What this dataset can and cannot tell you

Gained over the previous GeoLite2 + IP2Proxy setup: native `is_relay` (iCloud Private
Relay), `is_cdn`, Tor-list-backed exit nodes, and the real cloud provider ranges —
far better than the 15-ASN embedded datacenter seed.

**`AsnType` comes from `traits.usage_type`** (added in the 2026-08-15 build), which maps
1:1 onto the enum:

| `usage_type` | `AsnType` |
|---|---|
| `residential` | `Residential` |
| `mobile` | `Mobile` |
| `business` | `Business` |
| `education` | `Education` |
| `government` | `Government` |
| `hosting` | `Datacenter`, or `Cdn` when `is_cdn` is also set |
| *(absent)* | falls through to `is_cdn` / `is_hosting_provider` / the ASN seed, else `Unknown` |

`IsDatacenter` is derived from the resolved `AsnType`, so the two never disagree.

Two limits worth remembering:

- It is **inferred, not commercial**. `usage_type_source` says which rule fired:
  `peeringdb` (self-declared network type), `asn_name` (keyword match on the ASN
  organization/domain) or `prefix_flag` (from `is_hosting_provider`/`is_cdn`).
- Coverage is partial — about **45% of routable IPv4 space** carries a type (300k-address
  sample: residential 24%, hosting 10%, education 5%, business 3%, mobile 2%,
  government 1%). The rest stays `Unknown`, which is a normal outcome, not a failure.

If the inferred type ever proves unreliable against live data, the fallback is a
composite provider layering IP2Proxy's `usage_type` over iplegence —
`IIpIntelligenceProvider` allows it without touching any caller. See D24.

Also note `is_public_proxy` is broad in the merged data — it fires on some CDN and
hosting ranges — and it feeds `IsProxyOrVpn`, worth +20 in the heuristic scorer. If false
positives climb after a switch, that mapping in
`IplegenceIpIntelligenceProvider.Lookup` is the first place to look.
