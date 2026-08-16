# Agent handoff — TelemetryGuard ⇄ iplegence ⇄ Bangladesh University website

> **Superseded by [`handover.md`](handover.md)** — that file is the current state and covers the attribution work, the SDK change and the deployment, none of which existed when this was written. Kept for its narrower detail on the D24 provider model.

Written 2026-08-16 by the agent that did the work below. Everything here is verified
against the machine, not assumed. **No credentials are in this file** — see
[Credentials](#credentials-ask-the-owner).

If you are picking this up: read [Ground rules](#ground-rules-do-not-violate-these-silently)
and [Environment gotchas](#environment-gotchas-that-cost-me-time) first. They are the
things that will waste your time or break something quietly.

---

## 1. The three repositories

| Path | What it is | State |
|---|---|---|
| `/home/shafqat/git/shafqat/telemtry-guard` | **TelemetryGuard** — .NET 8 multi-tenant ad-fraud/bot-detection SaaS. The main project. | Heavily modified, **nothing committed** |
| `/home/shafqat/git/shafqat/buwebsite` | **Bangladesh University WordPress site** (WP 6.9 / PHP 8.5-FPM / nginx / MySQL 8.4). Serves `https://buweb-shafqat.cloudlabs.live`. | 1 new file + 2 edits, **nothing committed** |
| `/home/shafqat/git/iplegence` | **iplegence** — private Go pipeline producing a merged MaxMind-compatible IP-intelligence database. | Untouched by me. Head: `6a8e30d feat: infer usage_type from PeeringDB and ASN names` |

Each repo has its own `CLAUDE.md` with project instructions — read the one for whichever
repo you are editing. TelemetryGuard's `doc/spec.md` is the numbered decision record
(D1–D24) and is the source of truth for architecture.

---

## 2. What was done this session, in order

### 2.1 D24 — IP intelligence behind a provider model (TelemetryGuard)

The owner asked to adopt iplegence as the IP-intelligence source, with "a provider model,
one for maxmind and one for iplegence."

New seam: **`IIpIntelligenceProvider`** (`Lookup(IPAddress) → IpEnrichment`,
`ReloadIfChanged()`), selected by config `IpEnrichment:Provider` — `Iplegence` (default)
or `MaxMind`. Unknown value throws at composition time, mirroring the analytics provider
switch (D7). `IpEnrichmentService` now keeps only dataset-independent work (parse,
IPv4-in-IPv6 unmap, private/loopback short-circuit) and delegates the rest.

New files, all under `TelemetryGuard.RiskEngine/Enrichment/Providers/`:

| File | Role |
|---|---|
| `IIpIntelligenceProvider.cs` | the seam + its contract (never throws, synchronous, in-process) |
| `IplegenceIpIntelligenceProvider.cs` | default — memory-maps one `Superior-IP.mmdb` |
| `IplegenceRecord.cs` | MaxMind.Db decode DTOs for iplegence's record layout |
| `MaxMindIpIntelligenceProvider.cs` | RSK-02's original GeoLite2 + IP2Proxy path, moved behind the seam |
| `ReaderSet.cs` | moved here from `Enrichment/` (MaxMind provider's swappable readers) |
| `AsnClassifier.cs` | shared `AsnType` mapping + the embedded datacenter-ASN seed |
| `PrivateRelaySource.cs` | Apple iCloud Private Relay list + mtime reload, shared by both providers |
| `GraceDisposal.cs` | 30 s deferred dispose so in-flight lookups survive a reader swap |

**Why a file and not iplegence's HTTP API** (it ships a server and a Docker image): the
scoring path has a <50 ms budget (D3) and enrichment runs on every click and beacon. A
per-request network hop forces a fail-open/fail-closed decision on every timeout —
fail-open lets fraud through, fail-closed blocks real users. A memory-mapped file has no
such state. It also keeps Go out of the .NET-only backend (D1): **iplegence is a
build-time data producer, never a runtime dependency.**

### 2.2 PrivateRelayMatcher performance fix

`PrivateRelayMatcher` did a linear scan over Apple's egress ranges. The real downloaded
list is **287,798 ranges** (~246k of them IPv6), and this runs on *every* enrichment
lookup. Measured: **365 µs per IPv6 lookup**. Rewrote it as sorted, merged intervals with
binary search (`UInt128` for v6): **365 µs → 0.7 µs**. Same public API; added tests for
overlap/adjacency merging and the top-of-address-space overflow edge.

This was pre-existing (the embedded seed is small, so nobody had hit it) but became
load-bearing once the relay list joined the default path.

### 2.3 `traits.usage_type` — the `AsnType` gap, closed

The first iplegence build had no `usage_type`, so `AsnType` collapsed to
`Datacenter`/`Cdn`/`Unknown` — losing `Mobile`, which is what normalizes
`device_ids_this_ip_hour` for carrier-grade NAT. That was recorded as an accepted cost.

The owner then rebuilt iplegence with `traits.usage_type` +
`traits.usage_type_source`, inferred from PeeringDB network types, ASN-name keywords and
prefix flags. Wired in and verified against the real database:

| IP | org | usage_type / source | → `AsnType` |
|---|---|---|---|
| `73.162.0.1` | Comcast | residential / peeringdb | Residential |
| `208.54.4.1` | T-Mobile USA | mobile / asn_name | **Mobile** |
| `128.32.1.1` | UC Berkeley | education / asn_name | Education |
| `1.1.1.1` | Cloudflare | hosting / prefix_flag + `is_cdn` | Cdn |
| `45.32.1.1` | Vultr | hosting / prefix_flag | Datacenter |

**Coverage: 45.6%** of routable IPv4 space carries a type (measured over a 300k random
address sample: residential 24%, hosting 10%, education 5%, business 3%, mobile 2%,
government 1%). The rest is `Unknown` — a normal outcome, never guessed.

Caveats to carry forward: it is **inferred, not a commercial feed** (PeeringDB is
self-declared; ASN-name rules are keyword heuristics). `usage_type_source` is decoded and
available for a future confidence weighting; nothing scores on it today.

**Behavior change worth knowing:** `IsDatacenter` is now *derived from* the resolved
`AsnType` (`Datacenter` or `Cdn`) rather than recomputed. Before, a prefix typed
`education`/`residential` could still be flagged as a datacenter because its ASN appeared
in the hand-maintained 15-ASN seed — a contradiction that matters, since
`ip_datacenter_asn` raises a T1 floor to 80 on paid clicks.

### 2.4 `ForwardedHeaders:ForwardLimit` made configurable

`ForwardLimit` was hardcoded to `1` in `TelemetryGuard.Api/Program.cs`, which only unwinds
a single proxy. The BU deployment has **two** hops (platform edge → local nginx → Kestrel),
so every visitor would have been recorded as the edge's IP: uniform geo, ASN, `usage_type`
and velocity, with nothing appearing broken. Now config-driven, default still 1.

**Trust and limit must be raised together.** The limit only says how far the middleware may
walk; it still stops at the first address not in `TrustedProxyCidrs`, which is what makes a
client-supplied `X-Forwarded-For` unusable for spoofing. Three tests cover the two-hop
chain, the default-limit failure mode, and the untrusted-hop stop.

### 2.5 Bangladesh University website integration

`wp-content/mu-plugins/bu-telemetryguard.php` (new) injects the tag on `wp_head`, modelled
on the existing `bu-ga4.php`. Design points:

- **Site key by host map** inside the plugin, so one committed file serves every checkout.
  A host not in the map renders nothing.
- **Endpoint defaults to same-origin `/tg`.** Deliberate: no CORS, no extra DNS record, and
  no new hostname on the shared OPNsense SAN certificate (a manual re-signing step on that
  platform). This is why *nothing on the firewall had to change*.
- Host comes from `home_url()`, not `$_SERVER['HTTP_HOST']` (attacker-controlled).
- Silent for admin screens, AJAX/REST/cron, feeds, and logged-in users.
- `wp-config.php` gained `TG_ENDPOINT` / `TG_SITE_KEY` / `TG_ENABLED` (env-driven, same
  idiom as `GA_MEASUREMENT_ID`). `TG_ENABLED=0` is the kill switch, no deploy needed.

### 2.7 Two defects found by actually driving a browser (2026-08-16)

Neither was visible from status codes — both produced a page that *looked* correctly
instrumented while collecting nothing.

**a. The SDK drops the endpoint path.** `config.ts` derives the API base as
`new URL(src).origin`, so a path-mounted endpoint (`https://host/tg`) collapsed to
`https://host` and every call hit WordPress's 404. Fix: the mu-plugin now emits
`data-endpoint` alongside `data-site-key` (the SDK honors it verbatim). Anyone mounting
the API on a path rather than its own hostname needs this.

**b. The API and the SDK disagreed on where the site key travels.** The shipped SDK posts
`POST /i` with `k` **inside the envelope** — SDK-02, which the task files declare "the
canonical wire contract … that API-04 implements server-side". The API read `k` from the
**query string** only and returned a silent, success-shaped `204` when absent, so *every*
real beacon was discarded with no log line. Fixed in `BeaconEndpoints.HandleBeaconAsync`:
query param first (it is what lets the tenant middleware and the per-tenant rate-limit
partition resolve early), then the envelope's `k`. Three tests pin it.

**Why nobody noticed:** the SDK e2e suite runs against a *mock* `/i`, and
`BeaconEndpointTests` hand-builds `/i?k=…`. Neither side ever exercised the real pair, so
the MVP shipped with the JS beacon path non-functional against a real deployment. Logged
in `doc/plan.md` as a Phase-1.5 gap: CI needs a browser-drives-real-API smoke test.

### 2.6 Deployment on the cloudlabs box

```
visitor ─HTTPS─▶ OPNsense edge (192.168.9.1) ─HTTP─▶ nginx ^~ /tg/ ─▶ Kestrel 127.0.0.1:5120
```

- `telemetryguard-api.service` (systemd, enabled at boot), Release build published to
  `/opt/telemetryguard`, config + secrets in root-owned `0600 /etc/telemetryguard/tg.env`.
- `location ^~ /tg/` added to
  `/etc/nginx/sites-available/buweb-shafqat.cloudlabs.live.conf` (backup in `/root/`).
- Replaced a 2-day-old foreground `dotnet run` on port 5120 that was serving stale code.
- `tg-mssql`, `tg-redis`, `tg-clickhouse` given `--restart unless-stopped` (the API now
  boots at startup and would otherwise find no datastores).

---

## 3. Current live state

| Thing | Value |
|---|---|
| API service | `telemetryguard-api.service` — active, enabled, `127.0.0.1:5120` |
| App files | `/opt/telemetryguard` (root-owned snapshot of a Release publish) |
| Service config | `/etc/telemetryguard/tg.env` (root, `0600`) |
| Public entry | `https://buweb-shafqat.cloudlabs.live/tg/…` |
| Datastores | Docker: `tg-mssql` 1433, `tg-redis` 6379, `tg-clickhouse` 8123 |
| Geo data | `/home/shafqat/git/shafqat/telemtry-guard/data/geo/Superior-IP.mmdb` (128 MB, gitignored) |
| Tenant | `9dd11629-09ec-4d66-afa8-929cf1c00ac4` — "Bangladesh University", 90-day retention, `ApprovalQueue` |
| Site key — prod | `bu.edu.bd`, `www.bu.edu.bd` → `tg_sk_1uIiZzN3XsyoB3skHSvCyf` |
| Site key — dev | `buweb-shafqat.cloudlabs.live` → `tg_sk_1QkVuujgAWT899pAIKN6QD` |

Site keys are **public by design** — they ship in page HTML. The tenant API key
(`tg_ak_…`) is secret and is *not* in this file.

**Verified end to end on 2026-08-16 with a real browser** (Playwright chromium against
the live public URL): tag present → `/tg/sdk/tg.js` 200 → `/tg/i/init` 200 → `POST /tg/i`
204 → `kind='beacon'` row in ClickHouse carrying `has_js_beacon=1`,
`beacon_integrity_ok=1` (nonce + HMAC verified), a fingerprint visitor id, the real client
IP, and `webdriver_flag=1` (it caught the automation flag). A `curl` beacon in the same
table shows `beacon_integrity_ok=0` with every SDK feature `NaN`, which is the intended
contrast.

Getting there required two fixes — see 2.7.

**Client-IP correctness, verified against the live deployment:**

| request | recorded IP |
|---|---|
| honest | `192.168.9.11` (the real client — not the edge, not nginx) |
| forged `X-Forwarded-For: 8.8.8.8` | `192.168.9.11` — spoof rejected |

---

## 4. Credentials (ask the owner)

**Nothing secret is recorded here. Ask the owner for anything you need**, and do not paste
secrets back into chat or into repo files.

You may need: the MSSQL `sa` password (also in the gitignored `.env` at the TelemetryGuard
repo root), the ClickHouse password (same `.env`), the tenant API key `tg_ak_…` (shown once
at provisioning; only its SHA-256 is stored — if lost, issue a new one with `provision
issue-api-key`), GitHub auth for the private iplegence repo (`gh auth login`), and the
MySQL credentials for WordPress (they live only in the PHP-FPM pool files under
`/etc/php/8.5/fpm/pool.d/`, deliberately not in the repo).

The owner offered OPNsense root credentials during this session. **I did not use them and
you should not need them** — the same-origin `/tg` design exists precisely so the edge
stays untouched. That password was exposed in chat and I advised rotating it. If some
future task genuinely needs edge changes (e.g. adding a hostname to the shared SAN
certificate), ask the owner rather than assuming those credentials are still valid.

---

## 5. Ground rules (do not violate these silently)

From TelemetryGuard's `CLAUDE.md` and `doc/spec.md`:

- **Backend is .NET end-to-end.** No Python or Node server-side (D1). The browser SDK is
  TypeScript→esbuild, which is not a server runtime. Go (iplegence) is build-time only.
- **Never call iplegence's HTTP API from the request path.** Memory-mapped file only (D24).
- **Analytics providers abstract by intent, not by query** (D7). No generic query layer.
- **Multi-tenancy is enforced by SQL Server RLS**, not by developer discipline (D11).
  Connections only via `TenantConnectionFactory`.
- **Dapper, not EF Core** (D9). Migrations are versioned SQL run by DbUp (D10).
- **<50 ms scoring budget**, model inference in-process (D3).
- **Rules may only raise a score, never lower it.**
- **Missing signal ≠ zero.** Absent SDK features are `NaN`, absent enrichment is `null` —
  never 0/false. "Not covered by the dataset" is *unknown*, not *clean*.
- **Do not reintroduce PostgreSQL** (dropped in D23).
- **Decisions are amended by adding D25+**, never by editing history.
- Don't run `./scripts/dev-down.sh` while anyone else is using the stack.

---

## 6. Environment gotchas that cost me time

1. **`dotnet` is not on `PATH`** for non-login shells. Every build/test command needs:
   `export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"`.
2. **nginx: `^~` is mandatory for `/tg/`.** WordPress vhosts carry a regex location for
   static assets (`~* \.(…|js|…)$`), and **regex locations outrank plain prefix locations**.
   `location /tg/` would send `/tg/sdk/tg.js` to disk as a 404 while everything else looks
   healthy.
3. **nginx reload is asynchronous.** A `curl` immediately after `systemctl reload nginx`
   can still hit the old workers — I chased a phantom failure for a few minutes. Sleep a
   couple of seconds.
4. **Perf assertions must run in isolation.** The repo gates them behind
   `RUN_PERF_TESTS=1`; alongside 780 parallel tests they measure CPU contention, not the
   thing under test. `RealIplegenceDatabaseTests` follows the same convention.
5. **`IPLEGENCE_MMDB` must be an absolute path** — the test process runs from its own
   output directory, so a relative path silently skips the test.
6. **MaxMind.Db typed decoding**: every `[Constructor]` parameter needs a **default value**
   or an absent key throws. Unknown keys present in the database but absent from your DTO
   are skipped silently (verified against both a fixture and a real build).
7. **Go internal packages cannot be imported across modules**, so the MMDB test fixture is
   generated with `mmdbwriter` directly rather than iplegence's `internal/schema`. The
   generator is committed next to the fixture as a `.go` file that no build compiles; see
   `tests/TelemetryGuard.Tests.Unit/Enrichment/Data/README.md`.
8. **`dotnet run --project TelemetryGuard.Api` uses the *project* directory as CWD**, which
   is why `appsettings.Development.json` sets `IpEnrichment:DataDir` to `../data/geo`.
9. **ClickHouse died on its own** (exit 137, 8 h before I found it; no host OOM — 52 GB
   free). Restart policies now cover it, but if `/ready` returns 503, check the containers
   first: that is almost always the cause.

---

## 7. Verification recipes

```bash
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"

dotnet build TelemetryGuard.sln                       # must be 0 warnings
dotnet test tests/TelemetryGuard.Tests.Unit           # 783 passed / 3 skipped as of handoff

# Opt-in check against the real 128 MB database (record layout, known rows, latency).
# Run ALONE — the timing half is meaningless under parallel load.
IPLEGENCE_MMDB="$PWD/data/geo/Superior-IP.mmdb" RUN_PERF_TESTS=1 \
  dotnet test tests/TelemetryGuard.Tests.Unit --filter FullyQualifiedName~RealIplegence

# Refresh the IP database (hot-swapped, no restart). Sources tried in order:
# IPLEGENCE_MMDB_URL, then IPLEGENCE_DIST_DIR, then `gh release download`.
IPLEGENCE_DIST_DIR=/home/shafqat/git/iplegence/dist ./scripts/update-iplegence.sh

# Service
sudo systemctl status telemetryguard-api
sudo journalctl -u telemetryguard-api -f
curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:5120/healthz   # liveness -> 200
curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:5120/ready     # 503 = a datastore is down

# Public path
B=https://buweb-shafqat.cloudlabs.live
curl -s $B/ | grep -A1 TelemetryGuard          # the tag
curl -s -o /dev/null -w "%{http_code}\n" $B/tg/sdk/tg.js

# Redeploy after a code change (the /opt copy is a snapshot — a rebuild alone changes nothing)
dotnet publish TelemetryGuard.Api -c Release -o /tmp/tg-publish
sudo rsync -a --delete /tmp/tg-publish/ /opt/telemetryguard/
sudo systemctl restart telemetryguard-api
```

Provisioning CLI (needs `MIGRATIONS_CONNECTIONSTRING`):

```bash
dotnet run --project TelemetryGuard.MigrationRunner -- provision \
  <create-tenant|issue-api-key|register-site|create-campaign> …
```

---

## 8. Open items

1. **`bu.edu.bd` (production) is not wired up.** Its key is already in the plugin's host
   map, so the tag will render as soon as that code deploys — but that host needs its own
   `/tg` proxy and a reachable API, or every page load fires a 404. Production looks
   Azure-hosted (`web.config`, `startup_azure.sh`), so it is a different reverse proxy.
2. **The tenant lives in the local dev SQL Server.** A separate production API means
   re-provisioning tenant/sites/keys there and updating the host map.
3. `scripts/update-iplegence.sh` is not scheduled anywhere. iplegence publishes daily at
   01:00 UTC; add a cron/systemd timer.
4. `IpEnrichment:DataDir` points into a user working tree
   (`/home/shafqat/git/shafqat/telemtry-guard/data/geo`). Fine for this box, fragile for a
   real deployment — move to `/var/lib/telemetryguard/geo` when it matters.
5. Validate the inferred `usage_type` against live data. The composite provider (layering
   IP2Proxy `usage_type` over iplegence) remains the fallback if it proves unreliable; the
   seam already allows it without touching a caller.
6. **Nothing is committed in either repo.** Review and commit when ready.

---

### Noticed in passing (not tasks)

**Noticed in passing — NOT tasks.** The owner did not ask for a security review and explicitly does not want one. These were tripped over while doing the requested work (`hdr_trace.php` while hunting for how the edge forwards headers; the `wp-config.php` tokens while adding the `TG_*` constants; `env_result.txt` is already in that repo's own Known Issues). They are recorded here **only** so the next agent does not spend time rediscovering them and does not re-raise them as work. Do not act on them unasked.

- `hdr_trace.php`, `ng_chk.php`, `read-nginx-default.php`, `read-nginx-active.php`,
   `fixmic.php`, `chkw2.php` sit in the webroot and are matched by `location ~ \.php$`,
   i.e. **publicly executable**. `hdr_trace.php` reads *and writes*
   `/etc/nginx/conf.d/spec-settings.conf`. The vhost only denies `wp-config.php` and
   `mu-plugins/`. I did not execute any of them (that would modify nginx config). They look
   like leftover debugging tools; deleting them is the fix.
- `wp-config.php` is tracked in git with a live `FB_PAGE_TOKEN` and `YOUTUBE_API_KEY` in
   cleartext.
- Already documented in that repo's own Known Issues: `env-dumper.php` wrote live DB
   credentials to `env_result.txt`, which was committed in `74317f7` and is still in git
   history. Rotation + history scrub still not done.
- The OPNsense root password was pasted into a chat session; advise rotation.


## 9. Where the written record lives

| Document | Contents |
|---|---|
| `doc/spec.md` → **D24** | the IP-intelligence decision: seam, mapping table, why file not HTTP, usage_type coverage and caveats |
| `doc/runbooks/iplegence-ip-intelligence.md` | install/refresh/rollback the dataset, the `usage_type` → `AsnType` table, verification |
| `doc/runbooks/local-edge-deployment.md` | the systemd service, redeploy, the two-hop `ForwardLimit` trap, the nginx `^~` trap |
| `doc/tasks/RSK-02-ip-enrichment.md` | original task, with a header noting what D24 amended |
| `tests/…/Enrichment/Data/README.md` | fixture row table + how to regenerate it |
| `buwebsite/CLAUDE.md` | the BU-side wiring: keys, plugin behavior, the `/tg` proxy block |
| `CLAUDE.md` (TelemetryGuard) | updated component list, the new constraint, `update-iplegence.sh` |
