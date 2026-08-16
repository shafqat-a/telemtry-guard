# How it works — ingestion, scoring, and the update paths

How data gets from a visitor's browser into TelemetryGuard, what happens to it on the
way, and how each moving part is updated. Written 2026-08-16 against the running
deployment on the cloudlabs host; specifics (hostnames, ports, keys) describe that
deployment, the shapes apply everywhere.

Companion documents: `doc/spec.md` (the numbered decision record — *why* each of these
choices exists), `doc/runbooks/local-edge-deployment.md` (the service and proxy),
`doc/runbooks/iplegence-ip-intelligence.md` (the IP dataset), `docs/agent01.md` (handoff).

---

## 1. What actually connects the Bangladesh University site to TelemetryGuard

One line of HTML. `wp-content/mu-plugins/bu-telemetryguard.php` hooks `wp_head`, so every
page render emits:

```html
<script async src="https://buweb-shafqat.cloudlabs.live/tg/sdk/tg.js"
        data-site-key="tg_sk_1QkVuujgAWT899pAIKN6QD"
        data-endpoint="https://buweb-shafqat.cloudlabs.live/tg"></script>
```

That is the entire coupling. The website holds no TelemetryGuard code, opens no database
connection, and makes no server-to-server call. Everything happens between the
**visitor's browser** and the API.

`/tg` is the seam. nginx on the same host proxies `location ^~ /tg/` to
`127.0.0.1:5120`, so to the browser the API looks like part of the website. Same-origin
means the server observes the landing URL (`Referer`) and the cookies itself, which is
both simpler and unforgeable.

The API can also run on a **different** origin — the SDK then reports the page URL,
referrer and script-readable cookies in the beacon itself (SDK-09), because cross-origin
the browser sends neither. Everything survives except `HttpOnly` cookies (a site's
login/session cookies), which no script can read. Same-origin stays preferable: what the
server observed cannot be forged by a bot.

`data-endpoint` is required because the SDK otherwise derives its API base as
`new URL(src).origin` — origin only, path discarded — which turns `/tg` into `https://host`
and sends every call to a WordPress 404.

---

## 2. The three capture paths

Two of them exist because **a large share of click fraud never executes JavaScript**, so
the design is deliberately redundant (spec §4).

| Path | Endpoint | Who uses it | What it sees |
|---|---|---|---|
| **Click tracker** | `GET /c` | ad destination URLs | HTTP layer only; works for bots that never run JS |
| **Web pixel** | `GET /p.gif` | sites that cannot add script tags (D22) | HTTP layer only; SDK features stay `NaN` |
| **Beacon** | `GET /i/init` + `POST /i` | the JS SDK — **this is what BU uses** | HTTP layer + behavioural, fingerprint, honeypot, storage age |

One mode per site, recorded as `Sites.IntegrationMode`. They do not combine: the pixel
maintains a `tg_sid` **cookie** while the SDK takes its session id from the URL →
`sessionStorage` → generated, so running both would split one visit into two unrelated
sessions.

### 2a. The tracker (`/c`) — not currently used by BU

Ad destination URLs point here. It resolves the campaign from SQL Server (cached),
extracts the click id, writes a `tracker` event, registers a grace entry, sets the
`tg_sid` cookie, and 302-redirects to the campaign's configured landing URL. The redirect
target comes **only** from `Campaign.LandingUrl` in config, never from the request — that
is the open-redirect guardrail.

### 2b. The pixel (`/p.gif`) — not currently used by BU

Serves a constant 43-byte transparent GIF, always `200`, even when Redis or the sink are
down: capture may be lost, never visible to the page. Byte-identical to the response for
an unknown site key, so a prober cannot tell valid keys from invalid ones.

### 2c. The beacon (`/i/init` + `/i`) — the BU path

```
visitor's browser
  │  1. GET /tg/sdk/tg.js                    bundle, served from the API's wwwroot
  │  2. GET /tg/i/init?k=<site key>&sid=…    → { nonce, storageTs, storageSig }
  │  3. …collect timing, fingerprint, botd, honeypots, storage age…
  │  4. POST /tg/i                           sendBeacon, text/plain JSON envelope
  ▼
OPNsense edge 192.168.9.1 ──▶ nginx :443 ──▶ Kestrel 127.0.0.1:5120
```

**Why step 2 exists:** `sendBeacon` is fire-and-forget and can never read a response, so
every server-issued value the SDK needs — the integrity nonce and a signed storage
timestamp — has to come from one separate readable fetch first.

**When step 4 fires:** batch size (`count`), a timer, tab `hidden`, and `pagehide`. The
pagehide flush is why navigating away produces a row, and why each row carries the page
it was flushed from.

**Why `text/plain`:** it keeps the POST a CORS "simple request", so no preflight is ever
required. The endpoint accepts `application/json` too (the `fetch(keepalive)` fallback).

---

## 3. What the API does with a beacon

In order, inside `BeaconEndpoints.HandleBeaconAsync`:

1. **Body cap** — `413` is the only non-`204` status this endpoint can return.
2. **Parse JSON**, then **resolve the tenant** from the site key: query string first, then
   the envelope's own `k` (the shipped SDK puts it in the envelope — SDK-02 is the
   canonical wire contract). An unknown key gets a success-shaped `204`, indistinguishable
   from an accepted beacon, so bots get no oracle.
3. **Validate** the envelope: sid shape, `seq >= 0`, `events` array. Failures are logged
   and swallowed — always `204`.
4. **Integrity**: the envelope checksum is recomputed against the stored nonce →
   `beacon_integrity_ok`. A `curl`-forged beacon lands with `0`; a real SDK session
   with `1`.
5. **Attribution** (`AttributionExtractor`): UTMs and click ids from the landing URL,
   every cookie, every header, the full `landing_url`, and a derived
   `attribution_channel` (D25).
6. **Aggregate** into the Redis hash `t:{tid}:sess:{sid}` — running aggregates only, never
   raw event points. Every behavioural feature is later derived from this hash alone.
7. **Velocity** counters into `t:{tid}:v:ipua:{ip}:{bucket}`.
8. **Snapshot** a `ClickEvent` onto the event sink.

The sink is a **bounded in-memory channel drained in batches**; the request thread only
enqueues. If the queue fills, rows are dropped and counted rather than making a visitor
wait — the hot path never blocks on analytics.

---

## 4. What lands where

| Store | Contents | Lifetime |
|---|---|---|
| **Redis** | per-session running aggregates, velocity counters, nonces, grace/finalize markers | session 30 min, nonce 15 min |
| **ClickHouse `tg_events`** | one row per snapshot: identity, full headers + cookies + `landing_url`, UTMs, click ids, `attribution_channel`, SDK signals, and (on verdict rows) score + enrichment | per-row TTL from `retention_days`, 90 by default (D20) |
| **SQL Server** | tenants, site keys, campaigns, verdict summaries, exclusion state, rollups | permanent config |

Redis holds the *working* state; ClickHouse holds the *record*; SQL Server holds *config
and aggregates*. Portals and APIs read the small RLS-protected SQL tables, never the event
store directly (D23).

Raw behavioural events never reach ClickHouse — only periodic snapshots of the Redis
aggregate. The cadence is `Beacon:SinkEveryNthBeacon`: the default (`10`) snapshots the
first beacon, fp/fs-bearing batches, and every tenth, which yields **one row per session**.
This deployment runs `1`, so every batch is stored and a session becomes a page-by-page
journey — at roughly N× the event volume.

---

## 5. Scoring, and where it currently stops

The scoring pipeline (in-process, < 50 ms budget, D3) runs: IP enrichment → velocity →
feature extraction → T1 deterministic rules (which may only *raise* a score) → `IScorer`
→ band (`allow` 0–30 / `challenge` 31–70 / `block` 71–100) → verdict row.

It is triggered by the **verdict finalizer**, a worker that sweeps `t:{tid}:grace` every
second for sessions whose grace period has expired.

> **The gap, stated plainly:** `t:{tid}:grace` is populated **only** by the tracker and
> pixel endpoints. The beacon endpoint does not add to it. A visit that arrives organically
> and is measured only by the JS SDK is therefore **captured but never scored** — and since
> IP enrichment is wired into the scoring pipeline rather than the capture endpoints, its
> `country` / `asn` / `asn_type` / proxy columns stay null too.
>
> As of writing, BU has 19 beacon rows and 2 verdicts — both verdicts from pixel tests.
> Both facts trace to one assumption: the system was built around *paid clicks arriving
> through `/c`*, and a pure organic JS visit falls outside that loop.
>
> Options: register a grace entry on a session's first beacon; enable form gating so the
> SDK calls `/decide`; or accept the beacon rows as raw analytics.

The SDK also calls `POST /decide` directly when a page is configured to gate forms — that
path returns an allow/challenge/block decision synchronously and can present a Cloudflare
Turnstile challenge (D14).

---

## 6. Enrichment data

IP intelligence is a **memory-mapped file, never a service call** (D24). One merged
`Superior-IP.mmdb` from iplegence supplies country, city, coordinates, ASN, and an inferred
`usage_type` that becomes `AsnType` (residential / mobile / business / education /
government / hosting → Cdn/Datacenter). A network hop here would force a
fail-open/fail-closed decision on every timeout, inside a budget that also has to cover
velocity, features and the model.

Missing data degrades to null, never zero: an address the dataset does not cover is
*unknown*, not *clean* — about 54% of routable IPv4 space carries no inferred type.

---

## 7. The update paths

| What changes | How | Restart |
|---|---|---|
| **API code** | `dotnet publish -c Release` → `rsync` into `/opt/telemetryguard` → `systemctl restart telemetryguard-api` | yes (~2 s) |
| **ClickHouse schema** | numbered scripts in `TelemetryGuard.Analytics.ClickHouse/schema/`, applied by `MigrationRunner --clickhouse`; additive `ALTER … IF NOT EXISTS` only | no |
| **SQL Server schema** | DbUp numbered scripts, `MigrationRunner` with no args | no |
| **IP intelligence data** | `scripts/update-iplegence.sh` writes a new `Superior-IP.mmdb`; the provider polls mtime and hot-swaps the reader, disposing the old one after a 30 s grace | **no** |
| **Website tag** | edit the mu-plugin — WordPress loads mu-plugins on every request; no build, no cache | no |
| **Runtime config / secrets** | `/etc/telemetryguard/tg.env` (root, `0600`) | yes |

Two switches worth knowing:

- `TG_ENABLED=0` in the PHP-FPM env kills the tag site-side without any deploy.
- `IpEnrichment:Provider` flips `Iplegence` ↔ `MaxMind` in one line — the rollback for a
  bad daily dataset build.

Two traps:

- `/opt/telemetryguard` is a **snapshot**. Rebuilding in the working tree changes nothing
  until the rsync + restart runs.
- Nothing currently schedules `update-iplegence.sh`, so the dataset stays at whatever build
  was last fetched.

---

## 8. Deployment shape on this host

```
                    ┌───────────────────────────────────────────┐
  internet ────────▶│ OPNsense 192.168.9.1  (TLS, shared SAN)    │
                    └───────────────────┬───────────────────────┘
                                        │ HTTP + X-Forwarded-For/-Proto
                    ┌───────────────────▼───────────────────────┐
                    │ nginx  :80/:443, routes by Host/SNI       │
                    │   /            → WordPress (PHP-FPM)      │
                    │   ^~ /tg/      → 127.0.0.1:5120           │
                    └───────────────────┬───────────────────────┘
                                        │
                    ┌───────────────────▼───────────────────────┐
                    │ telemetryguard-api.service (systemd)      │
                    │   /opt/telemetryguard, tg.env             │
                    └──┬────────────┬────────────┬──────────────┘
                       │            │            │
                  tg-mssql     tg-redis    tg-clickhouse   (docker, restart=unless-stopped)
```

Two proxy hops means `ForwardedHeaders:ForwardLimit=2` with **both** `127.0.0.1/32` and
`192.168.9.1/32` trusted. With the default limit of 1, every visitor would be recorded as
the edge's own IP — uniform geo, ASN and velocity, with nothing appearing broken. The limit
only says how far the middleware may walk; it still stops at the first untrusted address,
which is what keeps a client-supplied `X-Forwarded-For` unusable for spoofing.

---

## 9. Quick checks

```bash
# Is the tag on the page?
curl -s https://buweb-shafqat.cloudlabs.live/ | grep -A1 TelemetryGuard

# Is the API reachable through the site's own origin?
curl -s -o /dev/null -w "%{http_code}\n" https://buweb-shafqat.cloudlabs.live/tg/healthz

# Liveness vs readiness (503 = a datastore is down; check the containers first)
curl -s http://127.0.0.1:5120/ready

# What has been captured?
curl -s "http://127.0.0.1:8123/?user=tg&password=<pw>&database=telemetry_guard" --data-binary \
  "SELECT kind, attribution_channel, count() FROM tg_events
   WHERE tenant_id='9dd11629-09ec-4d66-afa8-929cf1c00ac4'
   GROUP BY kind, attribution_channel FORMAT TSVWithNames"
```
