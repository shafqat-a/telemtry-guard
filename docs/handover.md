# Handover — TelemetryGuard, iplegence, and the Bangladesh University site

Complete state as of **2026-08-16**, written for whoever (or whatever) picks this up next.
Everything here was verified against the running machine, not recalled. **No credentials
are in this file** — see [Credentials](#credentials).

Supersedes `docs/agent01.md`, which is the earlier snapshot from the same day and is now
missing the attribution work, the SDK change and the deployment. Read this one.

Also worth reading: `docs/how-it-works.md` (how ingestion, scoring and updates work),
`doc/spec.md` (the numbered decision record D1–D25 — *why* each choice exists).

---

## 1. Repositories and their exact state

| Path | What it is |
|---|---|
| `~/git/shafqat/telemtry-guard` | **TelemetryGuard** — .NET 8 multi-tenant ad-fraud / bot-detection SaaS |
| `~/git/shafqat/buwebsite` | **Bangladesh University WordPress site** (WP 6.9 / PHP 8.5-FPM / nginx / MySQL 8.4) |
| `~/git/iplegence` | **iplegence** — private Go pipeline producing the merged IP-intelligence database. Untouched; head `6a8e30d` |

**telemtry-guard** — branch `feat/d24-iplegence-ip-intelligence`, **6 commits, none pushed**
(no remote configured for this work):

```
12670d5 feat(sdk): report page URL, referrer and readable cookies in the envelope (SDK-09)
416d421 docs: explain ingestion, scoring and the update paths
3ac4bd1 feat(analytics): capture the whole request — every header, every cookie, full URL (D25)
7588d48 feat(analytics): capture ad attribution on every event (D25)
f2d1951 docs: add the agent handoff and record the SDK/API contract gap
3bbcdc3 fix(api): accept the beacon site key from the envelope, and make ForwardLimit configurable
21e73aa feat(enrichment): add IP intelligence provider model, default to iplegence (D24)
```

Still uncommitted there: `Dockerfile` (modified) and `docker-compose.app.yml` (untracked) —
**pre-existing Phase-2 portal work, not mine**, except one edit of mine inside the compose
file (a read-only `./data/geo` mount + `IpEnrichment__DataDir`).

**buwebsite** — checked out on `main`:

| branch | local | remote | has the tag |
|---|---|---|---|
| `main` | `bb2eb13` | `c543f56` | yes — **not pushed** |
| `production` | `e1d11bf` | `e1d11bf` | yes — pushed ✓ |
| `shafqat` | `54b2063` | `54b2063` | no |
| `monirul` | `54b2063` | `c543f56` | no |

Deliberately **not** committed in that repo: `DB/budatabase_28072026.sql` — a 153 MB MySQL
dump containing `wp_users` / `wp_usermeta` inserts (real accounts and password hashes),
**not gitignored**. One `git add -A` from entering history permanently. Add `DB/` to
`.gitignore`. Also untracked and left alone: `router.php`, `serve.sh` (documented
leftovers), and a pending deletion of `wp-content/mu-plugins/env_result.txt`.

---

## 2. The live deployment

```
internet ─▶ OPNsense 192.168.9.1 (TLS, shared SAN cert) ─▶ nginx :80/:443 on 192.168.9.11
                                                             ├── /            WordPress (PHP-FPM)
                                                             ├── ^~ /tg/      ─┐
                                                             └── tel.bu.edu.bd ┴─▶ 127.0.0.1:5120
                                                                                   telemetryguard-api.service
                                                                                   ├── tg-mssql   :1433
                                                                                   ├── tg-redis   :6379
                                                                                   └── tg-clickhouse :8123
```

| Thing | Value |
|---|---|
| API service | `telemetryguard-api.service` — active, enabled at boot, `127.0.0.1:5120` |
| App files | `/opt/telemetryguard` (root-owned **snapshot** of a Release publish) |
| Config + secrets | `/etc/telemetryguard/tg.env` (root, `0600`) |
| Public ingestion URL | `https://buweb-shafqat.cloudlabs.live/tg` |
| nginx vhosts | `/etc/nginx/sites-available/buweb-shafqat.cloudlabs.live.conf` (`^~ /tg/`), `tel.bu.edu.bd.conf` (new, inert — see §6) |
| Geo data | `~/git/shafqat/telemtry-guard/data/geo/Superior-IP.mmdb` (128 MB, gitignored, 2026-08-15 build) |
| Tenant | `9dd11629-09ec-4d66-afa8-929cf1c00ac4` — "Bangladesh University", 90-day retention, `ApprovalQueue` |
| Site key (dev) | `buweb-shafqat.cloudlabs.live` → `tg_sk_1QkVuujgAWT899pAIKN6QD` |
| Site key (prod) | `bu.edu.bd`, `www.bu.edu.bd` → `tg_sk_1uIiZzN3XsyoB3skHSvCyf` |

Site keys are **public by design** — they ship in page HTML. The tenant API key (`tg_ak_…`)
is the secret one and is not in this file.

Data captured so far: 30 `beacon`, 3 `pixel`, 3 `verdict` rows (mostly test traffic plus
one real visitor from Dhaka).

### How the website connects

One line of HTML, from `wp-content/mu-plugins/bu-telemetryguard.php` on `wp_head`:

```html
<script async src="https://buweb-shafqat.cloudlabs.live/tg/sdk/tg.js"
        data-site-key="tg_sk_1QkVuujgAWT899pAIKN6QD"
        data-endpoint="https://buweb-shafqat.cloudlabs.live/tg"></script>
```

The site holds no TelemetryGuard code and makes no server-to-server call. The site key is
chosen at runtime from a host map in the plugin, so **one file serves every instance** —
production automatically emits the production key. `TG_ENDPOINT` / `TG_SITE_KEY` /
`TG_ENABLED` in the PHP-FPM pool env override it; `TG_ENABLED=0` is the kill switch.

`data-endpoint` is **mandatory**: without it the SDK derives its base as
`new URL(src).origin` — path discarded — so a `/tg` mount collapses to `https://host` and
every call 404s while the page still looks instrumented.

---

## 3. What was done, and why

### D24 — IP intelligence behind a provider model
`IIpIntelligenceProvider`, selected by `IpEnrichment:Provider`: `Iplegence` (default, one
memory-mapped `Superior-IP.mmdb`) or `MaxMind` (GeoLite2 + IP2Proxy, the rollback).
A **file, not iplegence's HTTP API**: enrichment runs on every click and beacon inside a
50 ms budget, and a network hop would force a fail-open/fail-closed choice on every
timeout. iplegence stays a build-time data producer — no Go in production.

`AsnType` comes from the dataset's inferred `traits.usage_type` (residential / mobile /
business / education / government / hosting), covering ~45% of routable IPv4 space; the
rest falls through to prefix flags and the embedded datacenter-ASN seed, else `Unknown`.
`IsDatacenter` is **derived from** the resolved `AsnType` so the two cannot contradict.

### PrivateRelayMatcher — 365 µs → 0.7 µs
It scanned Apple's egress list linearly. The live list is **287,798 ranges**; measured
365 µs per IPv6 lookup, on every enrichment call. Rewritten as sorted merged intervals
with binary search.

### Beacon site key — a product bug, not a deployment one
The shipped SDK posts `POST /i` with the site key **inside the envelope** (SDK-02 is the
canonical wire contract); the API read it from the **query string** only and returned a
silent, success-shaped `204`. The JS beacon path was non-functional against any real
deployment for the entire MVP. Fixed: query first, then envelope.
**Why it hid:** the SDK e2e suite runs against a *mock* `/i`, and `BeaconEndpointTests`
hand-builds `/i?k=…`. Neither side ever exercised the real pair.

### ForwardedHeaders:ForwardLimit made configurable
Was hardcoded to `1`, which unwinds one proxy. This chain has **two** (edge → nginx →
Kestrel), so every visitor would have recorded as the edge's IP — uniform geo, ASN and
velocity, with nothing appearing broken. Trust and limit must be raised together; the
middleware still stops at the first untrusted hop, which is what blocks spoofing.

### D25 — attribution, then full-request capture
First pass: UTMs, click ids (incl. `gbraid`/`wbraid` — Google sends those *instead of*
`gclid` on iOS/consent-limited traffic), platform cookies and a stored
`attribution_channel`, with an allowlist that excluded raw cookies and arbitrary query
values.

**The owner then directed full capture** for cross-page visitor tracking: `headers` (every
header), `cookies` (every cookie), `landing_url` (full URL with query). The allowlist is
gone by decision, not by oversight.

> **Consequence to keep visible:** `tg_events` now contains session and auth cookies —
> on the WordPress deployments, `wordpress_logged_in_*` for every logged-in editor — plus
> any PII a GET form put in a query string. **Read access to the event store is equivalent
> to holding those credentials.** `retention_days` (90) is what expires them.

Per-page rows additionally require `Beacon:SinkEveryNthBeacon=1` (set in `tg.env`); the
default of `10` yields one row per *session*, not per page, at ~N× the event volume.

### SDK-09 — page context in the envelope
Cross-origin, the browser trims `Referer` to the bare origin and sends no `SameSite=Lax`
cookies. Measured: a Meta ad click recorded as `attribution_channel=referral` with no
UTMs, no click id and zero cookies — *wrong*, not merely incomplete. The SDK now sends
`u` (`location.href`), `r` (`document.referrer`) and `ck` (script-readable cookies); the
server uses them only to fill what it could not observe. An informative `Referer` still
wins, and a cookie the request carried beats a reported one of the same name.

`r` also fixed a pre-existing gap: the request's `Referer` is the tagged page itself, so
every untagged visit read as `direct`. `document.referrer` separates `organic_search`
from `direct` properly.

Integrity was unaffected — the verifier hashes the serialized prefix before the last
`,"c":"` and never needed the field list. Bundle **26.4 KB gzip** against the 30 KB gate.

### Website integration
`bu-telemetryguard.php` (mu-plugin, `wp_head`, modelled on the existing `bu-ga4.php`),
`wp-config.php` overrides, CLAUDE.md notes. Tracks **every** visitor including logged-in
ones (owner decision); still silent for wp-admin, AJAX/REST/cron and feeds.

---

## 4. Verified facts (evidence, not assumption)

- **End to end with a real browser:** tag → `/tg/sdk/tg.js` 200 → `/tg/i/init` 200 →
  `POST /tg/i` 204 → `beacon` row in ClickHouse with `has_js_beacon=1`,
  `beacon_integrity_ok=1`, a fingerprint id, the real client IP, and `webdriver_flag=1`
  (it caught Playwright's automation flag).
- **Client IP through two proxies:** an honest request records the real client address;
  a request carrying a forged `X-Forwarded-For: 8.8.8.8` is **ignored**.
- **Attribution:** Meta / Google / TikTok test arrivals recorded `meta_ads` / `google_ads`
  / `tiktok_ads` with their click ids and full UTM sets; an untagged visit records `direct`.
- **Cross-page:** a 3-page browse produces 3 rows sharing `session_id` and the `_ga` cookie.
- **Cross-origin (after SDK-09):** channel, UTMs, click id, full landing URL and
  `_ga`/`_fbp`/`tg_fp` all recovered even though the browser sent only the bare origin
  and no cookies.
- **Real visitor observed:** `163.61.241.195` — BD / Dhaka / AS134732 "Dot Internet",
  residential, real browser.
- **Test suite:** 818 passed, 3 skipped. Solution builds with 0 warnings.

---

## 5. Open items, in priority order

1. **Organic JS visits are captured but never scored.** The verdict finalizer sweeps
   `t:{tid}:grace`, which is populated **only** by the tracker (`/c`) and pixel endpoints —
   the beacon endpoint does not add to it. Consequence: 30 beacon rows, 3 verdicts (all
   from pixel tests), and because IP enrichment is wired into the *scoring* pipeline rather
   than the capture endpoints, `country`/`asn`/`asn_type` stay null on capture rows. The
   whole design assumed *paid clicks arriving through `/c`*; a pure organic JS visit falls
   outside the loop. Options: register a grace entry on a session's first beacon; enable
   form gating so the SDK calls `/decide`; or accept the rows as raw analytics.
2. **`tel.bu.edu.bd` is half-built.** See §6.
3. **Production `bu.edu.bd` is not wired up.** Its key is already in the plugin's host map,
   so the tag renders the moment that code deploys — and 404s until that host has a
   reachable API. Either mount `/tg` there, finish `tel.bu.edu.bd`, or set
   `TG_ENDPOINT=https://buweb-shafqat.cloudlabs.live/tg` (works today, keeps full
   attribution since SDK-09).
4. **`main` in buwebsite is unpushed** (`bb2eb13` vs `c543f56` on the remote). The live dev
   site runs from it.
5. **The tenant lives in the local dev SQL Server.** A separate production API means
   re-provisioning tenant/sites/keys there.
6. **Nothing schedules `scripts/update-iplegence.sh`** — the IP dataset stays at the
   2026-08-15 build until someone runs it.
7. **`IpEnrichment:DataDir` points into a user working tree.** Fine here, wrong for a
   service; `/var/lib/telemetryguard/geo` is the right home.
8. **No test drives the real SDK against the real API.** That gap is exactly what let the
   beacon site-key bug survive the whole MVP. Logged in `doc/plan.md`.
9. **Nothing aggregates by `attribution_channel`** — rollups and admin reports don't know
   the column exists, so "how much of last week's paid Meta traffic was fraudulent" is
   still a raw ClickHouse query.
10. **`beacon_integrity_ok=0` on follow-up batches.** The first batch of a session verifies;
    later ones do not. The 85-minutes-later case is explained by the 15-minute nonce TTL;
    a one-minute-later case is not. Pre-existing, unexplained, low priority.

### Noticed in passing — NOT tasks
No security review was requested and the owner explicitly does not want one. Recorded only
so the next agent doesn't rediscover them or re-raise them as work.

- `hdr_trace.php`, `ng_chk.php`, `read-nginx-*.php`, `fixmic.php`, `chkw2.php` sit in the
  webroot and are matched by `location ~ \.php$`, i.e. publicly executable; `hdr_trace.php`
  reads *and writes* `/etc/nginx/conf.d/spec-settings.conf`.
- `wp-config.php` is tracked in git with a live `FB_PAGE_TOKEN` and `YOUTUBE_API_KEY`.
- The `env_result.txt` credential leak in history (`74317f7`) — already in that repo's own
  Known Issues; rotation and history scrub still not done.
- The OPNsense root password was pasted into a chat session (and turned out to be unusable
  from that host — see §6). Worth rotating.

---

## 6. `tel.bu.edu.bd` — live

The subdomain exists and resolves correctly: `tel.bu.edu.bd` → `103.139.235.67` (the edge).

The backend vhost `/etc/nginx/sites-available/tel.bu.edu.bd.conf` serves the whole API at
the **root** (`/i`, `/i/init`, `/sdk/tg.js`, no `/tg` prefix) through
`127.0.0.1:5120`. OPNsense has a dedicated nginx HTTP Server for `tel.bu.edu.bd`, reusing
the `dev1-pool` upstream to `192.168.9.11:80`.

TLS uses a separate Let's Encrypt certificate for `tel.bu.edu.bd`, issued with the ACME
plugin's `telemetry-http01` validation and automatic 60-day renewal. Its post-renewal
automation reloads nginx. This deliberately does not modify the shared
`fw.cloudlabs.live` SAN certificate. Verified publicly on 2026-08-16: `/healthz`,
`/sdk/tg.js`, and production-key `/i/init` all return 200 with normal certificate
verification.

Then production sets `TG_ENDPOINT=https://tel.bu.edu.bd`.

**Why a subdomain is worth it:** it is the same *site* as `bu.edu.bd` (different origin),
so it is not a third-party context — no third-party-cookie blocking, domain-scoped cookies
(`_ga`, `_fbp`) are sent by the browser, and ad blockers treat it more kindly than a
`cloudlabs.live` endpoint. The only thing a subdomain cannot capture is **HttpOnly**
cookies (a site's login/session cookies); that needs a true same-origin `/tg` mount.

---

## 7. Environment gotchas that cost real time

1. **`dotnet` is not on `PATH`** for non-login shells:
   `export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"`.
   Node is at `$HOME/.local/node/bin`.
2. **nginx: `^~` is mandatory for a `/tg/` prefix.** WordPress vhosts carry a regex
   location for static assets, and **regex beats prefix** — `location /tg/` would send
   `/tg/sdk/tg.js` to disk as a 404 while everything else looked fine.
3. **`proxy_pass` needs its trailing slash** (`http://127.0.0.1:5120/`) so the `/tg` prefix
   is stripped. Without it every path arrives as `/tg/i` and the API 404s silently.
4. **nginx reload is asynchronous** — a `curl` immediately after `systemctl reload nginx`
   can still hit old workers. Sleep a couple of seconds before concluding anything.
5. **The Bash tool's working directory persists between calls.** A `cd` into the SDK
   directory made later `dotnet` commands fail with MSB1009.
6. **Perf assertions must run in isolation** (`RUN_PERF_TESTS=1`); alongside 800 parallel
   tests they measure CPU contention.
7. **`IPLEGENCE_MMDB` must be an absolute path** — the test runs from its own output dir.
8. **MaxMind.Db typed decoding** needs a default value on every `[Constructor]` parameter;
   unknown keys present in the database are skipped silently.
9. **Go internal packages can't be imported across modules**, so the MMDB test fixture is
   generated with `mmdbwriter` directly (generator committed beside the fixture).
10. **ClickHouse died on its own once** (exit 137, no host OOM). If `/ready` returns 503,
    check the containers first — they now carry `--restart unless-stopped`.
11. **`/opt/telemetryguard` is a snapshot.** Rebuilding the working tree changes nothing
    until publish + rsync + restart.
12. **The SDK bundle ships from the API's `wwwroot`.** An SDK change needs
    `npm run build` *and* a republish of the API, or the served bundle stays stale —
    verify by comparing md5 of `dist/tg.js` against what the site serves.

---

## 8. Verification recipes

```bash
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"

dotnet build TelemetryGuard.sln                  # must be 0 warnings
dotnet test tests/TelemetryGuard.Tests.Unit      # 818 passed / 3 skipped at handover

# SDK (needs $HOME/.local/node/bin on PATH)
cd TelemetryGuard.Sdk && npm run typecheck && npm run build && npm run size && npm run test:integrity

# Opt-in check against the real 128 MB IP database — run ALONE
IPLEGENCE_MMDB="$PWD/data/geo/Superior-IP.mmdb" RUN_PERF_TESTS=1 \
  dotnet test tests/TelemetryGuard.Tests.Unit --filter FullyQualifiedName~RealIplegence

# Redeploy after a code or SDK change
dotnet publish TelemetryGuard.Api -c Release -o /tmp/tg-publish
sudo rsync -a --delete /tmp/tg-publish/ /opt/telemetryguard/
sudo systemctl restart telemetryguard-api

# Refresh the IP dataset (hot-swapped, no restart)
IPLEGENCE_DIST_DIR=~/git/iplegence/dist ./scripts/update-iplegence.sh

# Live checks
curl -s https://buweb-shafqat.cloudlabs.live/ | grep -A1 TelemetryGuard
curl -s -o /dev/null -w "%{http_code}\n" https://buweb-shafqat.cloudlabs.live/tg/healthz
curl -s http://127.0.0.1:5120/ready          # 503 = a datastore is down
sudo journalctl -u telemetryguard-api -f

# What has been captured
curl -s "http://127.0.0.1:8123/?user=tg&password=<pw>&database=telemetry_guard" --data-binary \
  "SELECT kind, attribution_channel, count() FROM tg_events
   WHERE tenant_id='9dd11629-09ec-4d66-afa8-929cf1c00ac4'
   GROUP BY kind, attribution_channel FORMAT TSVWithNames"
```

Driving a real browser (this is how the beacon bugs were found — no substitute for it):
Playwright chromium is cached; put a `.mjs` script **inside `TelemetryGuard.Sdk/`** so
`import { chromium } from 'playwright'` resolves, run it, delete it.

---

## 9. Ground rules (do not violate silently)

- **Backend is .NET end-to-end.** No server-side Python or Node (D1). The browser SDK is
  TypeScript→esbuild. Go (iplegence) is build-time only.
- **Never call iplegence's HTTP API from the request path** — memory-mapped file only (D24).
- **Analytics providers abstract by intent, not by query** (D7), and **both providers move
  together**: a ClickHouse schema change needs the matching Kusto columns and ingestion
  mapping, and the MapRow guard tests count columns (currently 89).
- **Multi-tenancy is enforced by SQL Server RLS** (D11); connections only via
  `TenantConnectionFactory`. **Dapper, not EF Core** (D9); DbUp migrations (D10).
- **< 50 ms scoring budget**, model inference in-process (D3).
- **Rules may only raise a score, never lower it.**
- **Missing signal ≠ zero.** Absent SDK features are `NaN`, absent enrichment is `null`.
  "Not covered by the dataset" is *unknown*, not *clean*.
- **Do not reintroduce PostgreSQL** (dropped in D23).
- **Decisions are amended by adding D26+**, never by rewriting history.
- Don't run `./scripts/dev-down.sh` while anyone else is using the stack.

---

## Credentials

**Nothing secret is in this file. Ask the owner for anything you need**, and don't paste
secrets back into chat or into repo files.

You may need: the MSSQL `sa` and ClickHouse passwords (both in the gitignored `.env` at the
TelemetryGuard repo root), the tenant API key `tg_ak_…` (shown once at provisioning; only
its SHA-256 is stored — reissue with `provision issue-api-key` if lost), GitHub auth for
the private iplegence repo, and the MySQL credentials for WordPress (only in the PHP-FPM
pool files under `/etc/php/8.5/fpm/pool.d/`).

The OPNsense root password was offered during this session and proved **unusable from the
app host** — the management plane isn't reachable from that VLAN. Don't assume it is still
valid; ask, and prefer an API key or an SSH path that actually reaches the device.
