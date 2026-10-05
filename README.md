# TelemetryGuard

Score ad clicks and page visits before they become paid conversions. Drop one script tag on a site, or point an ad URL at the tracker, and TelemetryGuard records the visit, enriches the IP, and scores it in process.

It is a .NET 8 service for teams running paid traffic (Google Ads, Meta) and lead forms. The browser snippet is a small static file. The API, scoring, and stores are C#.

Three capture paths, one session model:

| Path | When to use it | What it sees |
|---|---|---|
| Browser SDK | Normal pages | Timing, fingerprint, bot signals, honeypots, conversions |
| Click tracker `GET /c` | The destination URL of an ad | IP, headers, referrer, click ids. Works when the client never runs JavaScript |
| Pixel `GET /p.gif` | A page that cannot take a script tag | The same HTTP-layer signals as the tracker |

A `204` from the beacon means the request was handled. It does not mean the event was stored. Unknown site keys are answered with the same success shape as known ones, so a probe cannot tell them apart.

## Add it to a page

Build the SDK once so the API can serve it (`cd TelemetryGuard.Sdk && npm ci && npm run build`), then:

```html
<script
  async
  src="http://localhost:5120/sdk/tg.js"
  data-site-key="tg_sk_dev0000000000000000000">
</script>
```

`tg_sk_dev0000000000000000000` is the site key created by `./scripts/dev-seed.sh`. Replace it with the key you issue for a real site. The site key identifies the site. It is not a secret and it grants no admin access.

When the script is loaded from the API origin, the SDK sends beacons to that origin. If you host `tg.js` somewhere else, set the API explicitly:

```html
<script
  async
  src="https://cdn.example.com/tg.js"
  data-site-key="YOUR_SITE_KEY"
  data-endpoint="https://telemetry.example.com">
</script>
```

The SDK calls `GET /i/init` and then `POST /i` itself. You do not sign the envelope by hand. A body without the nonce from `/i/init` is acknowledged and dropped.

### No JavaScript

```html
<img src="http://localhost:5120/p.gif?k=YOUR_SITE_KEY" alt="" width="1" height="1">
```

The response is always a 1×1 GIF, including for an unknown key.

### Ad click URL

Send the ad platform to the tracker. `cid` is the TelemetryGuard campaign id. The redirect target is the campaign's configured landing URL, never a URL from the query string.

```text
http://localhost:5120/c?k=YOUR_SITE_KEY&cid=CAMPAIGN_GUID&gclid={gclid}
```

The dev seed creates campaign `44444444-4444-4444-4444-444444444444`.

### Hold a lead form

Gating is off until you opt a form in. Add `data-tg-gate` to the form:

```html
<form action="/lead" method="post" data-tg-gate>
  <input name="email" type="email" required>
  <button type="submit">Send</button>
</form>
```

On submit the SDK calls `POST /decide`. The response is an action (`allow`, `challenge`, or `block`), not a score. `allow` releases the submit. `challenge` loads Cloudflare Turnstile. `block` holds the submit and fires `tg:decision` on the form. If `/decide` is unreachable, the submit is released.

Turnstile needs `Turnstile:SiteKey` and `Turnstile:SecretKey`. With those empty, a challenge cannot complete.

## Run it locally

You need the .NET 8 SDK, Node.js 20 or newer, and Docker.

```bash
./scripts/dev-up.sh     # SQL Server :1433, Redis :6379, ClickHouse :8123, Grafana :3000
./scripts/dev-seed.sh   # migrations, dev tenant, site key, campaign
dotnet run --project TelemetryGuard.Api
```

The `http` launch profile listens on `http://localhost:5120`. `GET /healthz` is liveness. `GET /ready` stays 503 until SQL Server, Redis, and ClickHouse answer.

The portal is a separate Razor Pages app. It keeps an API key in a cookie and calls the API. It does not open the database itself.

```bash
dotnet run --project TelemetryGuard.Portal   # http://localhost:5140
```

Check the admin API with the dev API key printed by `dev-seed.sh`:

```bash
curl -s -H "X-Api-Key: tg_ak_dev0000000000000000000000000000000000000000" \
  http://localhost:5120/admin/whitelist
```

`X-Api-Key` is for operators. Scopes are `admin`, `report`, and `ingest`. `ingest` alone cannot read `/admin`.

Stop the stack with `./scripts/dev-down.sh`.

IP enrichment reads a local MaxMind database (`./scripts/update-iplegence.sh` fills `./data/geo`). The API starts without it and leaves geo fields empty.

## What a visit becomes

1. The site key selects the tenant. An unknown key is dropped quietly.
2. The API keeps running aggregates in Redis and velocity counters per IP.
3. The risk engine enriches the IP and scores in the same process. Rules can raise a score. They do not lower one.
4. Events land in ClickHouse. Tenant configuration, campaigns, and verdict summaries live in SQL Server.
5. The portal and `/admin` read SQL Server. They do not query the event store.

The enforcing scorer in a fresh checkout is the heuristic. An ML.NET LightGBM model can run beside it in `ListenOnly` and is not used for decisions until someone promotes it. A new deployment also has `Enforcement:ObserveOnly` enabled and `DefaultMode` set to `ApprovalQueue`, so scores are recorded and actions wait for approval. The dev seed tenant is the exception: it is created with `AutoEnforce`.

Google Ads and Meta exclusion clients ship in `TelemetryGuard.Integrations`. They stay idle until credentials are set. Both workers default to dry-run.

## Projects

| Project | What it is |
|---|---|
| `TelemetryGuard.Api` | Ingest, tracker, pixel, `/decide`, admin API |
| `TelemetryGuard.Sdk` | TypeScript source for `tg.js` |
| `TelemetryGuard.RiskEngine` | Features, rules, heuristic scorer, optional ML.NET |
| `TelemetryGuard.Analytics.ClickHouse` | Event store |
| `TelemetryGuard.Analytics.Kusto` | Alternate store. Unused unless you select it |
| `TelemetryGuard.Data` | SQL Server repositories and migrations |
| `TelemetryGuard.Portal` | Operator UI |
| `TelemetryGuard.MigrationRunner` | Schema and `provision` commands |
| `TelemetryGuard.Training` | Label, train, and promote a model |
| `TelemetryGuard.Integrations` | Turnstile, Google Ads, Meta |

An embeddable collector for another ASP.NET Core app (`TelemetryGuard.Client`) is on the `client` branch. It is not in the default branch and it does not expose `/decide`.

## Tests

```bash
dotnet build TelemetryGuard.sln
dotnet test tests/TelemetryGuard.Tests.Unit
cd TelemetryGuard.Sdk && npm test
```

Integration tests need Docker. They start SQL Server, ClickHouse, and Redis with Testcontainers.

## Read next

- [API guide](docs/TelemetryGuard-API.md) — envelopes, conversions, and what each route returns
- [How a beacon is stored](docs/how-it-works.md)
- [SDK build and gating](TelemetryGuard.Sdk/README.md)
- [Decision record](doc/spec.md)

This repository does not yet include a license file.
