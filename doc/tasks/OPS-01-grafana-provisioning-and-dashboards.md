---
id: OPS-01
title: Grafana provisioning and starter dashboards
phase: 1
workstream: foundation
depends_on: [FND-02, ANA-02]
size: M
spec_refs: [D6, D11, D16, D17, D19, D20, "§6.3 Scoring bands"]
detail_level: full
---

# OPS-01: Grafana provisioning and starter dashboards

## Objective
Provision Grafana entirely as code: install the official ClickHouse datasource plugin via the compose file, provision the datasource and a dashboard provider from YAML, and commit two starter dashboards as JSON — **Traffic & Verdicts** (clicks by verdict band over time, score histogram, JS-beacon coverage) and **Fraud Sources** (top blocked IPs, ASNs, campaigns). These are the internal/early-tenant reporting surface for the MVP and the manual-review window until the customer portal exists.

## Spec context (self-contained)
- **Grafana on ClickHouse is the MVP dashboard answer** (spec D17): internal + early-tenant reporting; the customer-facing portal is Phase 2. Until then, internal Grafana views (plus a manual whitelist API) are also the human-oversight/override surface (D19) — so the Fraud Sources dashboard doubles as the false-positive review screen.
- **Scoring bands** (spec §6.3): 0–30 allow, 31–70 challenge, 71–100 block. On event rows the band values are the strings `allow` / `challenge` / `block` (NULL until a verdict is finalized).
- **`has_js_beacon`** is a real feature, not just plumbing: a large share of click fraud never executes JS, and pixel-mode tenants send no beacons at all (D22). The coverage ratio panel tells operators how much of the traffic carries SDK signals.
- **Tenancy**: ClickHouse rows carry `tenant_id` as the first `ORDER BY` column (D11). Grafana is an *internal ops* tool here and may see all tenants; dashboards still expose a `tenant` template variable so operators scope views per tenant. (RLS protects the SQL Server side; the application's `IAnalyticsQueries` layer — not Grafana — is where tenant injection is mandatory and non-optional.)
- **Data volume note**: dashboards query the raw events table directly, which is acceptable at MVP volume on the single-node ClickHouse (D6/D16). SQL-Server rollup tables (ANA-07/D23) serve APIs/portals, not these ops dashboards.
- Retention (D20) means panels silently show at most `retention_days` of history per row — no dashboard handling needed.

## Prerequisites
- **FND-02** is complete: `docker-compose.yml` at the repo root runs `grafana/grafana-oss:11.2.0` as service `grafana` (container `tg-grafana`) with bind mounts `./ops/grafana/provisioning:/etc/grafana/provisioning` and `./ops/grafana/dashboards:/var/lib/grafana/dashboards`, on the same bridge network as service `clickhouse` (ClickHouse 24.8, native port 9000, HTTP 8123, database `telemetry_guard`, credentials from `.env`: `CLICKHOUSE_USER` / `CLICKHOUSE_PASSWORD`). The mounted directories exist with `.gitkeep` files.
- **ANA-02** is complete: ClickHouse DDL is committed under `/home/shafqat/git/shafqat/telemtry-guard/TelemetryGuard.Analytics.ClickHouse/schema/` and applied by its `SchemaMigrator`. The contract this task consumes (from the ANA-02 task file — reconcile against the actually committed DDL before building panels, and note any renames in the commit message):
  - One shared table **`tg_events`** holding raw capture rows AND verdict rows, discriminated by `kind LowCardinality(String)` with values `'tracker' | 'pixel' | 'beacon' | 'verdict'`.
  - Columns used by these dashboards: `tenant_id UUID`, `timestamp DateTime64(3, 'UTC')`, `session_id String`, `kind`, `campaign_id String DEFAULT ''`, `ip IPv6` (IPv4 stored as IPv4-mapped `::ffff:a.b.c.d`), `asn Nullable(UInt32)`, `asn_org Nullable(String)`, `has_js_beacon UInt8`, `score Nullable(Int16)` (0–100, NULL until scored), `band LowCardinality(Nullable(String))` (`allow`/`challenge`/`block`), `scorer_version LowCardinality(Nullable(String))`, `retention_days UInt16`.
  - `ENGINE = MergeTree`, `ORDER BY (tenant_id, timestamp)`, per-row TTL `toDateTime(timestamp) + toIntervalDay(retention_days) DELETE`.
  - **Verdict semantics for panels**: one `kind = 'verdict'` row exists per scored click (written at verdict finalization) carrying `score`, `band`, and `has_js_beacon`; capture rows (`tracker`/`pixel`/`beacon`) may have these NULL/unset. Verdict-based panels therefore filter `kind = 'verdict'`; raw traffic counts filter `kind IN ('tracker','pixel')`.
  - The ClickHouse **database** is `telemetry_guard` (created by FND-02's `CLICKHOUSE_DB`). If ANA-02's migrator was pointed at a different database (its fallback assumption is `default`), use whatever database `SHOW TABLES` proves `tg_events` lives in, in both the datasource `defaultDatabase` and every query.

## Implementation steps

1. **Extend the `grafana` service in `/home/shafqat/git/shafqat/telemtry-guard/docker-compose.yml`** — add three environment entries (keep everything else exactly as FND-02 wrote it):

```yaml
  grafana:
    # ... existing image/ports/volumes/networks/mem_limit/healthcheck unchanged ...
    environment:
      GF_SECURITY_ADMIN_USER: ${GRAFANA_ADMIN_USER}
      GF_SECURITY_ADMIN_PASSWORD: ${GRAFANA_ADMIN_PASSWORD}
      GF_USERS_ALLOW_SIGN_UP: "false"
      GF_INSTALL_PLUGINS: grafana-clickhouse-datasource
      CLICKHOUSE_USER: ${CLICKHOUSE_USER}
      CLICKHOUSE_PASSWORD: ${CLICKHOUSE_PASSWORD}
```

   `GF_INSTALL_PLUGINS` makes Grafana download the official ClickHouse plugin at container start (needs outbound internet on first start; the plugin persists in the `grafana-data` volume). `CLICKHOUSE_USER`/`CLICKHOUSE_PASSWORD` are passed through because datasource provisioning files interpolate environment variables.

2. **Create the datasource provisioning file** `/home/shafqat/git/shafqat/telemtry-guard/ops/grafana/provisioning/datasources/clickhouse.yml`:

```yaml
apiVersion: 1

datasources:
  - name: TelemetryGuard ClickHouse
    uid: tg-clickhouse
    type: grafana-clickhouse-datasource
    access: proxy
    isDefault: true
    editable: false
    jsonData:
      host: clickhouse          # compose service name on the tg network
      port: 9000                # native protocol
      protocol: native
      secure: false
      defaultDatabase: telemetry_guard
      username: $CLICKHOUSE_USER
    secureJsonData:
      password: $CLICKHOUSE_PASSWORD
```

   The `uid: tg-clickhouse` is load-bearing — both dashboard JSONs reference it. Do not change it.

3. **Create the dashboard provider** `/home/shafqat/git/shafqat/telemtry-guard/ops/grafana/provisioning/dashboards/dashboards.yml`:

```yaml
apiVersion: 1

providers:
  - name: telemetry-guard
    orgId: 1
    folder: TelemetryGuard
    type: file
    disableDeletion: false
    allowUiUpdates: true
    updateIntervalSeconds: 30
    options:
      path: /var/lib/grafana/dashboards
      foldersFromFilesStructure: false
```

4. **Panel SQL contract.** Every panel uses the Grafana ClickHouse plugin macros `$__timeFilter(col)` (expands to the dashboard time range) and `$__timeInterval(col)` (bucketed `toStartOfInterval` matched to panel width), plus a multi-select `tenant` template variable rendered via `${tenant:singlequote}`. The queries, panel by panel:

   **Dashboard 1 — Traffic & Verdicts** (`uid: tg-traffic-verdicts`)

   - *Clicks by verdict band over time* (timeseries, stacked; one `kind='verdict'` row per scored click):

```sql
SELECT
    $__timeInterval(timestamp) AS time,
    coalesce(band, 'pending') AS verdict_band,
    count() AS clicks
FROM telemetry_guard.tg_events
WHERE $__timeFilter(timestamp)
  AND kind = 'verdict'
  AND toString(tenant_id) IN (${tenant:singlequote})
GROUP BY time, verdict_band
ORDER BY time
```

   - *Score histogram* (bar chart, 10-point buckets):

```sql
SELECT
    toString(intDiv(toUInt16(assumeNotNull(score)), 10) * 10) AS bucket,
    count() AS clicks
FROM telemetry_guard.tg_events
WHERE $__timeFilter(timestamp)
  AND kind = 'verdict'
  AND isNotNull(score)
  AND toString(tenant_id) IN (${tenant:singlequote})
GROUP BY bucket
ORDER BY toUInt16(bucket)
```

   - *JS beacon coverage %* (stat; share of scored clicks that carried SDK beacons):

```sql
SELECT round(100 * countIf(has_js_beacon = 1) / count(), 1) AS js_beacon_pct
FROM telemetry_guard.tg_events
WHERE $__timeFilter(timestamp)
  AND kind = 'verdict'
  AND toString(tenant_id) IN (${tenant:singlequote})
```

   - *Raw capture hits* (stat; tracker + pixel rows, i.e. pre-verdict traffic volume):

```sql
SELECT count() AS hits
FROM telemetry_guard.tg_events
WHERE $__timeFilter(timestamp)
  AND kind IN ('tracker', 'pixel')
  AND toString(tenant_id) IN (${tenant:singlequote})
```

   **Dashboard 2 — Fraud Sources** (`uid: tg-fraud-sources`)

   - *Top blocked IPs* (table; `ip` is `IPv6`, IPv4 shown mapped — strip the `::ffff:` prefix for readability):

```sql
SELECT
    replaceOne(toString(ip), '::ffff:', '') AS ip,
    count() AS blocked_clicks,
    uniqExact(session_id) AS sessions
FROM telemetry_guard.tg_events
WHERE $__timeFilter(timestamp)
  AND kind = 'verdict'
  AND band = 'block'
  AND toString(tenant_id) IN (${tenant:singlequote})
GROUP BY ip
ORDER BY blocked_clicks DESC
LIMIT 20
```

   - *Top blocked ASNs* (table):

```sql
SELECT
    assumeNotNull(asn) AS asn,
    any(coalesce(asn_org, '')) AS organisation,
    count() AS blocked_clicks,
    uniqExact(ip) AS distinct_ips
FROM telemetry_guard.tg_events
WHERE $__timeFilter(timestamp)
  AND kind = 'verdict'
  AND band = 'block'
  AND isNotNull(asn)
  AND toString(tenant_id) IN (${tenant:singlequote})
GROUP BY asn
ORDER BY blocked_clicks DESC
LIMIT 20
```

   - *Campaigns by blocked clicks* (table; block share per campaign across all scored clicks):

```sql
SELECT
    campaign_id,
    countIf(band = 'block') AS blocked_clicks,
    count() AS scored_clicks,
    round(100 * blocked_clicks / scored_clicks, 1) AS block_pct
FROM telemetry_guard.tg_events
WHERE $__timeFilter(timestamp)
  AND kind = 'verdict'
  AND campaign_id != ''
  AND toString(tenant_id) IN (${tenant:singlequote})
GROUP BY campaign_id
ORDER BY blocked_clicks DESC
LIMIT 20
```

5. **Create** `/home/shafqat/git/shafqat/telemtry-guard/ops/grafana/dashboards/traffic-verdicts.json` with the following content (the `rawSql` values are exactly the step-4 queries):

```json
{
  "uid": "tg-traffic-verdicts",
  "title": "Traffic & Verdicts",
  "tags": ["telemetryguard"],
  "timezone": "utc",
  "editable": true,
  "schemaVersion": 39,
  "version": 1,
  "refresh": "1m",
  "time": { "from": "now-24h", "to": "now" },
  "annotations": { "list": [] },
  "templating": {
    "list": [
      {
        "name": "tenant",
        "label": "Tenant",
        "type": "query",
        "datasource": { "type": "grafana-clickhouse-datasource", "uid": "tg-clickhouse" },
        "query": "SELECT DISTINCT toString(tenant_id) FROM telemetry_guard.tg_events ORDER BY 1",
        "refresh": 2,
        "multi": true,
        "includeAll": true,
        "current": { "selected": true, "text": ["All"], "value": ["$__all"] }
      }
    ]
  },
  "panels": [
    {
      "id": 1,
      "type": "timeseries",
      "title": "Clicks by verdict band",
      "gridPos": { "h": 9, "w": 24, "x": 0, "y": 0 },
      "datasource": { "type": "grafana-clickhouse-datasource", "uid": "tg-clickhouse" },
      "fieldConfig": {
        "defaults": {
          "custom": { "drawStyle": "line", "fillOpacity": 30, "stacking": { "mode": "normal" } },
          "unit": "short"
        },
        "overrides": [
          { "matcher": { "id": "byName", "options": "allow" },     "properties": [ { "id": "color", "value": { "mode": "fixed", "fixedColor": "green" } } ] },
          { "matcher": { "id": "byName", "options": "challenge" }, "properties": [ { "id": "color", "value": { "mode": "fixed", "fixedColor": "yellow" } } ] },
          { "matcher": { "id": "byName", "options": "block" },     "properties": [ { "id": "color", "value": { "mode": "fixed", "fixedColor": "red" } } ] },
          { "matcher": { "id": "byName", "options": "pending" },   "properties": [ { "id": "color", "value": { "mode": "fixed", "fixedColor": "gray" } } ] }
        ]
      },
      "targets": [
        {
          "refId": "A",
          "datasource": { "type": "grafana-clickhouse-datasource", "uid": "tg-clickhouse" },
          "editorType": "sql",
          "queryType": "timeseries",
          "format": 0,
          "rawSql": "SELECT $__timeInterval(timestamp) AS time, coalesce(band, 'pending') AS verdict_band, count() AS clicks FROM telemetry_guard.tg_events WHERE $__timeFilter(timestamp) AND kind = 'verdict' AND toString(tenant_id) IN (${tenant:singlequote}) GROUP BY time, verdict_band ORDER BY time"
        }
      ]
    },
    {
      "id": 2,
      "type": "barchart",
      "title": "Score distribution (0–100, 10-pt buckets)",
      "gridPos": { "h": 9, "w": 12, "x": 0, "y": 9 },
      "datasource": { "type": "grafana-clickhouse-datasource", "uid": "tg-clickhouse" },
      "fieldConfig": { "defaults": { "unit": "short" }, "overrides": [] },
      "options": { "xTickLabelRotation": 0, "showValue": "auto" },
      "targets": [
        {
          "refId": "A",
          "datasource": { "type": "grafana-clickhouse-datasource", "uid": "tg-clickhouse" },
          "editorType": "sql",
          "queryType": "table",
          "format": 1,
          "rawSql": "SELECT toString(intDiv(toUInt16(assumeNotNull(score)), 10) * 10) AS bucket, count() AS clicks FROM telemetry_guard.tg_events WHERE $__timeFilter(timestamp) AND kind = 'verdict' AND isNotNull(score) AND toString(tenant_id) IN (${tenant:singlequote}) GROUP BY bucket ORDER BY toUInt16(bucket)"
        }
      ]
    },
    {
      "id": 3,
      "type": "stat",
      "title": "JS beacon coverage",
      "gridPos": { "h": 9, "w": 6, "x": 12, "y": 9 },
      "datasource": { "type": "grafana-clickhouse-datasource", "uid": "tg-clickhouse" },
      "fieldConfig": {
        "defaults": {
          "unit": "percent",
          "thresholds": { "mode": "absolute", "steps": [ { "color": "red", "value": null }, { "color": "yellow", "value": 40 }, { "color": "green", "value": 70 } ] }
        },
        "overrides": []
      },
      "targets": [
        {
          "refId": "A",
          "datasource": { "type": "grafana-clickhouse-datasource", "uid": "tg-clickhouse" },
          "editorType": "sql",
          "queryType": "table",
          "format": 1,
          "rawSql": "SELECT round(100 * countIf(has_js_beacon = 1) / count(), 1) AS js_beacon_pct FROM telemetry_guard.tg_events WHERE $__timeFilter(timestamp) AND kind = 'verdict' AND toString(tenant_id) IN (${tenant:singlequote})"
        }
      ]
    },
    {
      "id": 4,
      "type": "stat",
      "title": "Raw capture hits (tracker + pixel)",
      "gridPos": { "h": 9, "w": 6, "x": 18, "y": 9 },
      "datasource": { "type": "grafana-clickhouse-datasource", "uid": "tg-clickhouse" },
      "fieldConfig": { "defaults": { "unit": "short" }, "overrides": [] },
      "targets": [
        {
          "refId": "A",
          "datasource": { "type": "grafana-clickhouse-datasource", "uid": "tg-clickhouse" },
          "editorType": "sql",
          "queryType": "table",
          "format": 1,
          "rawSql": "SELECT count() AS hits FROM telemetry_guard.tg_events WHERE $__timeFilter(timestamp) AND kind IN ('tracker', 'pixel') AND toString(tenant_id) IN (${tenant:singlequote})"
        }
      ]
    }
  ]
}
```

6. **Create** `/home/shafqat/git/shafqat/telemtry-guard/ops/grafana/dashboards/fraud-sources.json`:

```json
{
  "uid": "tg-fraud-sources",
  "title": "Fraud Sources",
  "tags": ["telemetryguard"],
  "timezone": "utc",
  "editable": true,
  "schemaVersion": 39,
  "version": 1,
  "refresh": "5m",
  "time": { "from": "now-7d", "to": "now" },
  "annotations": { "list": [] },
  "templating": {
    "list": [
      {
        "name": "tenant",
        "label": "Tenant",
        "type": "query",
        "datasource": { "type": "grafana-clickhouse-datasource", "uid": "tg-clickhouse" },
        "query": "SELECT DISTINCT toString(tenant_id) FROM telemetry_guard.tg_events ORDER BY 1",
        "refresh": 2,
        "multi": true,
        "includeAll": true,
        "current": { "selected": true, "text": ["All"], "value": ["$__all"] }
      }
    ]
  },
  "panels": [
    {
      "id": 1,
      "type": "table",
      "title": "Top blocked IPs",
      "gridPos": { "h": 12, "w": 8, "x": 0, "y": 0 },
      "datasource": { "type": "grafana-clickhouse-datasource", "uid": "tg-clickhouse" },
      "fieldConfig": { "defaults": { "unit": "short" }, "overrides": [] },
      "targets": [
        {
          "refId": "A",
          "datasource": { "type": "grafana-clickhouse-datasource", "uid": "tg-clickhouse" },
          "editorType": "sql",
          "queryType": "table",
          "format": 1,
          "rawSql": "SELECT replaceOne(toString(ip), '::ffff:', '') AS ip, count() AS blocked_clicks, uniqExact(session_id) AS sessions FROM telemetry_guard.tg_events WHERE $__timeFilter(timestamp) AND kind = 'verdict' AND band = 'block' AND toString(tenant_id) IN (${tenant:singlequote}) GROUP BY ip ORDER BY blocked_clicks DESC LIMIT 20"
        }
      ]
    },
    {
      "id": 2,
      "type": "table",
      "title": "Top blocked ASNs",
      "gridPos": { "h": 12, "w": 8, "x": 8, "y": 0 },
      "datasource": { "type": "grafana-clickhouse-datasource", "uid": "tg-clickhouse" },
      "fieldConfig": { "defaults": { "unit": "short" }, "overrides": [] },
      "targets": [
        {
          "refId": "A",
          "datasource": { "type": "grafana-clickhouse-datasource", "uid": "tg-clickhouse" },
          "editorType": "sql",
          "queryType": "table",
          "format": 1,
          "rawSql": "SELECT assumeNotNull(asn) AS asn, any(coalesce(asn_org, '')) AS organisation, count() AS blocked_clicks, uniqExact(ip) AS distinct_ips FROM telemetry_guard.tg_events WHERE $__timeFilter(timestamp) AND kind = 'verdict' AND band = 'block' AND isNotNull(asn) AND toString(tenant_id) IN (${tenant:singlequote}) GROUP BY asn ORDER BY blocked_clicks DESC LIMIT 20"
        }
      ]
    },
    {
      "id": 3,
      "type": "table",
      "title": "Campaigns by blocked clicks",
      "gridPos": { "h": 12, "w": 8, "x": 16, "y": 0 },
      "datasource": { "type": "grafana-clickhouse-datasource", "uid": "tg-clickhouse" },
      "fieldConfig": {
        "defaults": {
          "unit": "short",
          "custom": { "align": "auto" }
        },
        "overrides": [
          { "matcher": { "id": "byName", "options": "block_pct" }, "properties": [ { "id": "unit", "value": "percent" } ] }
        ]
      },
      "targets": [
        {
          "refId": "A",
          "datasource": { "type": "grafana-clickhouse-datasource", "uid": "tg-clickhouse" },
          "editorType": "sql",
          "queryType": "table",
          "format": 1,
          "rawSql": "SELECT campaign_id, countIf(band = 'block') AS blocked_clicks, count() AS scored_clicks, round(100 * blocked_clicks / scored_clicks, 1) AS block_pct FROM telemetry_guard.tg_events WHERE $__timeFilter(timestamp) AND kind = 'verdict' AND campaign_id != '' AND toString(tenant_id) IN (${tenant:singlequote}) GROUP BY campaign_id ORDER BY blocked_clicks DESC LIMIT 20"
        }
      ]
    }
  ]
}
```

7. **Remove the now-redundant `.gitkeep` files** in `ops/grafana/provisioning/datasources/`, `ops/grafana/provisioning/dashboards/`, and `ops/grafana/dashboards/` (each directory now has real content).

8. **Bring the stack up and verify**: `./scripts/dev-up.sh`, wait for healthy, then run the acceptance checks below. First start downloads the plugin — check `docker logs tg-grafana | grep -i clickhouse` shows the plugin installed. If a panel renders "No data" despite rows existing, open the panel in Grafana's editor, re-select the format (Time series vs Table) in the query editor, verify, and copy any changed `format`/`queryType` values back into the committed JSON — plugin versions occasionally shift these enum encodings.

## Files to create or modify
- `/home/shafqat/git/shafqat/telemtry-guard/docker-compose.yml` (grafana service: add `GF_INSTALL_PLUGINS`, `CLICKHOUSE_USER`, `CLICKHOUSE_PASSWORD` env entries)
- `/home/shafqat/git/shafqat/telemtry-guard/ops/grafana/provisioning/datasources/clickhouse.yml`
- `/home/shafqat/git/shafqat/telemtry-guard/ops/grafana/provisioning/dashboards/dashboards.yml`
- `/home/shafqat/git/shafqat/telemtry-guard/ops/grafana/dashboards/traffic-verdicts.json`
- `/home/shafqat/git/shafqat/telemtry-guard/ops/grafana/dashboards/fraud-sources.json`
- Delete: the three `.gitkeep` files under `ops/grafana/`

## Acceptance criteria
- `docker compose config -q` exits 0; `./scripts/dev-up.sh` brings all services healthy.
- `curl -s -u admin:admin http://localhost:3000/api/datasources` shows one datasource with `"uid": "tg-clickhouse"` and type `grafana-clickhouse-datasource`; `curl -s -u admin:admin http://localhost:3000/api/datasources/uid/tg-clickhouse/health` returns `"status": "OK"`.
- `curl -s -u admin:admin "http://localhost:3000/api/search?query=&type=dash-db"` lists both dashboards (`tg-traffic-verdicts`, `tg-fraud-sources`) inside folder `TelemetryGuard`.
- After seeding test rows (Testing section), every panel on both dashboards renders data with no query errors, and changing the `tenant` variable filters rows.
- Both dashboard JSON files are valid JSON (`jq . file` exits 0) and every `rawSql` string matches the step-4 queries (modulo any renames reconciled against ANA-02's actual committed DDL, noted in the commit message).
- All configuration is file-based: wiping the `grafana-data` volume (`./scripts/dev-down.sh -v && ./scripts/dev-up.sh`, then re-applying the ANA-02 schema and re-seeding) reproduces datasource + dashboards with zero manual clicks.

## Testing
Manual verification via the acceptance criteria (Grafana provisioning has no xUnit surface). Suggested seed for panel verification — column names per the ANA-02 `tg_events` DDL; unlisted columns take their defaults (verify the exact required column list against the committed DDL first, since `tg_events` has many more columns; add any non-defaulted ones):

```bash
docker exec tg-clickhouse clickhouse-client --user tg --password tg-dev-password --query "
INSERT INTO telemetry_guard.tg_events
  (tenant_id, timestamp, session_id, kind, campaign_id, ip, asn, asn_org, has_js_beacon, score, band, scorer_version, retention_days)
VALUES
  ('11111111-1111-1111-1111-111111111111', now() - 300, 's1', 'verdict', 'camp-A', toIPv6('203.0.113.7'),  64496, 'ExampleNet DC',   0, 88,   'block',     'heuristic-v1', 90),
  ('11111111-1111-1111-1111-111111111111', now() - 240, 's2', 'verdict', 'camp-A', toIPv6('198.51.100.9'), 64500, 'Residential-ISP', 1, 12,   'allow',     'heuristic-v1', 90),
  ('11111111-1111-1111-1111-111111111111', now() - 120, 's3', 'verdict', 'camp-B', toIPv6('203.0.113.7'),  64496, 'ExampleNet DC',   0, 55,   'challenge', 'heuristic-v1', 90),
  ('11111111-1111-1111-1111-111111111111', now() -  90, 's1', 'tracker', 'camp-A', toIPv6('203.0.113.7'),  64496, 'ExampleNet DC',   0, NULL, NULL,        NULL,           90),
  ('22222222-2222-2222-2222-222222222222', now() -  60, 's4', 'verdict', 'camp-C', toIPv6('192.0.2.44'),   64501, 'OtherNet',        1, 5,    'allow',     'heuristic-v1', 30)"
```

Expected: verdict-band panel shows green/yellow/red series; histogram shows buckets 0, 10, 50, 80; JS beacon coverage 50% (2 of 4 verdict rows); raw capture hits = 1; Fraud Sources top IP is `203.0.113.7` with 1 blocked click; ASN 64496 tops the ASN table; `camp-A` shows 1 blocked of 2 scored (50%). Switching `tenant` to `22222222-…` empties the fraud tables.

## Out of scope / guardrails
- **This is the internal ops surface, not the customer portal** — no auth-per-tenant, no public exposure, no embedding. The tenant-facing portal with review/override is Phase 2 (P2-03).
- **Do not query SQL Server from Grafana** and do not add a SQL Server datasource: dashboards read ClickHouse only (D17). The SQL Server side is RLS-protected precisely because ad-hoc tools like Grafana exist (D11) — keep Grafana away from it entirely at MVP.
- **Do not build panels through any app-level query layer** — but equally, do NOT extend `IAnalyticsQueries` for dashboard needs. Grafana speaks native ClickHouse SQL directly; the intent-based interfaces (D7, no generic cross-engine query layer) are for application code, and their tenant_id injection is never optional there.
- **No alerting rules, no Grafana OnCall, no extra plugins** — starter dashboards only.
- Do not modify the ANA-02 schema to make a panel prettier — dashboards adapt to the schema, never the reverse.
- Keep the datasource `uid` (`tg-clickhouse`), dashboard `uid`s, and folder name stable — future dashboards and docs will reference them.
- Do not add mock/sample data generators to the repo; the seed insert above is a manual, throwaway verification step.
