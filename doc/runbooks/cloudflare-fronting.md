# Runbook: Cloudflare fronting for the TelemetryGuard API

INT-05 (Phase 1.5). Audience: whoever owns the deployed environment's DNS/Cloudflare
account and the app config for that environment. This is an operator runbook, not
application code — the .NET side of this contract is `TelemetryGuard.Api/Edge/*` and
the `Edge:Provider` config flag (step 7 below); the edge side is
`infra/cloudflare/tg-edge-worker.js`.

Do these steps IN ORDER. Steps 1-4 happen in the Cloudflare dashboard (or `wrangler`/
Terraform equivalents); step 5 deploys the worker; steps 6-7 are read-then-configure.

## 1. DNS: proxy the API host(s)

Point the tracker + ingestion hostname at the API's public address with a **proxied**
(orange-cloud) `A` or `CNAME` record. At MVP the tracker (`/c`), pixel (`/p.gif`),
beacon (`/i`, `/i/init`), decision (`/decide`), and admin (`/admin*`) endpoints all
share one host — one DNS record covers all of them.

A **grey-cloud (DNS-only)** record does NOT front the origin with Cloudflare: no TLS
fingerprint, no ASN, nothing reaches the Worker, and none of the rest of this runbook
takes effect. Verify orange-cloud is on before proceeding.

## 2. SSL/TLS: Full (strict)

Set the zone's SSL/TLS encryption mode to **Full (strict)** — never Flexible (which
terminates TLS at Cloudflare and speaks plaintext HTTP to the origin) and never plain
Full (which accepts any origin certificate, including self-signed).

- Install a certificate the origin will present that Cloudflare's Full (strict) mode
  accepts — a **Cloudflare Origin CA certificate** (free, issued from the dashboard,
  long-lived) is the simplest choice; a certificate from a public CA works too.
- Minimum TLS version: **1.2**.
- Confirm end-to-end: a direct `curl -v` to the origin's port 443 (bypassing
  Cloudflare, e.g. by IP with `--resolve`) must show the origin certificate and a
  successful TLS 1.2+ handshake.

## 3. Origin lockdown (mandatory, not optional)

Without this step, an attacker who learns the origin's IP address can bypass
Cloudflare entirely — no edge signals, and (worse) they could try to inject their own
`X-TG-*` headers directly. Do BOTH of the following, or at minimum the first:

- **Firewall inbound 443 to Cloudflare's published IP ranges only**
  (`https://www.cloudflare.com/ips-v4` and `/ips-v6` — the same list embedded in
  `TelemetryGuard.Api/Edge/cloudflare-ips.txt` and refreshed by
  `scripts/update-cloudflare-ips.sh`). Apply this at the cloud provider's
  security-group/NSG layer, not just in application code.
- **Enable Authenticated Origin Pulls** (Cloudflare dashboard: SSL/TLS → Origin
  Server) so the origin can additionally verify the TLS client certificate Cloudflare
  presents, rejecting connections that merely spoof a Cloudflare source IP.

> Restated plainly, because it matters: **the API-side spoof-rejection in
> `EdgeSignalReader` (requiring the direct peer to be a Cloudflare address) is the
> ONLY defense against a forged `X-TG-*` header if this firewall step is skipped.**
> Origin lockdown and the header check are both mandatory, independent layers —
> neither one alone is sufficient defense-in-depth.

## 4. Cache and rewrite bypass for capture/decision paths

These paths must reach the origin on every request — Cloudflare must never serve a
cached response, and must never rewrite the response body:

- Add a **Cache Rule** setting Cache Eligibility to **Bypass cache** for:
  `/c`, `/p.gif`, `/i*`, `/decide`, `/admin*`.
- **Disable Rocket Loader** and **disable Email Address Obfuscation** for these paths
  (both are response-body rewriters; rewriting the pixel GIF, a redirect, or a JSON
  decision body would corrupt it).

## 5. Deploy the edge worker

From `infra/cloudflare/`:

```bash
wrangler deploy
```

`wrangler.toml` ships with the route commented out — uncomment and set it to the real
API hostname before deploying (e.g. `track.example.com/*`), matching step 1's DNS
record. The worker (`tg-edge-worker.js`) does exactly one thing: it strips any
client-supplied `X-TG-*` headers, then sets its own from `request.cf`, then forwards
the request unchanged to the origin. It never blocks, challenges, or redirects — see
the honesty table below for which signals actually appear in `request.cf` on your
plan.

## 6. Honesty table (what you actually get, by plan)

The spec's original "free tier upward" wording for D13 is **optimistic** — do not
promise tenants JA3/JA4 or bot scores without an Enterprise Bot Management
subscription. What `request.cf` actually provides, by Cloudflare plan:

| Signal | `request.cf` field | Header set by our Worker | Plan required |
|---|---|---|---|
| ASN of client | `cf.asn` | `X-TG-ASN` | **All plans, including Free** |
| Bot score (1–99) | `cf.botManagement.score` | `X-TG-Bot-Score` | **Bot Management add-on (Enterprise)** |
| JA3 hash | `cf.botManagement.ja3Hash` | `X-TG-JA3` | **Bot Management (Enterprise)** |
| JA4 | `cf.botManagement.ja4` | `X-TG-JA4` | **Bot Management (Enterprise)** |

**On non-Enterprise plans expect only `X-TG-ASN`; `tls_ua_mismatch` remains null and
scoring proceeds — degraded, not broken (D13).** This is by design (spec §7 null
semantics: missing ≠ zero) — `FraudFeatureVector.TlsUaMismatch` is `bool?`, and null
means "no TLS fingerprint header", not "no mismatch". Do not upgrade a tenant's plan
promise based on the spec text alone; confirm the Bot Management add-on is active on
the zone before telling anyone JA3/JA4/bot-score will be populated.

## 7. App config: turn the gate on, and ONLY here

Set, in the deployed (Cloudflare-fronted) environment's configuration:

```json
{ "Edge": { "Provider": "Cloudflare" } }
```

**Local dev stays `Edge:Provider = "None"`** (the base `appsettings.json` default) —
there is no Cloudflare in front of a developer's machine, so honoring `X-TG-*`
headers there would let anyone on localhost spoof a TLS fingerprint. Setting
`Edge:Provider = "Cloudflare"` does two things at startup (`TelemetryGuard.Api/
Program.cs`): it merges the embedded Cloudflare IP ranges into ForwardedHeaders'
trusted-proxy set (so `X-Forwarded-For` from Cloudflare updates `RemoteIpAddress`),
and it switches on `X-TG-*` header intake in `EdgeSignalReader` — gated a SECOND time
per-request on the direct peer actually being a Cloudflare address (step 3's
defense-in-depth).

Separately, once JA3/JA4 are flowing, flip `FeatureExtraction:TlsUaMismatchEnabled`
to `true` in the same environment (RSK-04's gate) — INT-05 wires the transport;
turning that flag on is what makes the risk engine actually start computing
`tls_ua_mismatch` from live data instead of the default null.

## Keeping the IP range list current

Cloudflare's published ranges are stable for years but do change occasionally. Refresh
the embedded list with:

```bash
bash scripts/update-cloudflare-ips.sh
```

This overwrites `TelemetryGuard.Api/Edge/cloudflare-ips.txt` from
`https://www.cloudflare.com/ips-v4` and `/ips-v6` in the same commented format the
file already ships in. Review the diff, then redeploy the API — the list is loaded
once at process startup, not polled at runtime.
