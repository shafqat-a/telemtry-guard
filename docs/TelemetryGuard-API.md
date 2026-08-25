# TelemetryGuard API guide

This document is for anyone integrating a website, application, or server with
TelemetryGuard. It describes what TelemetryGuard accepts, what it observes, how it
groups data into visits and sessions, and what it can forward to MarketIQ.

The API is designed to be silent and fail-open for public telemetry: ingestion normally
returns `204 No Content`, including for malformed or unknown telemetry. A `204` means
the request was handled, not that a fraud verdict was returned synchronously.

## 1. Integration choices

There are three capture paths:

| Path | Use | Main signals |
|---|---|---|
| Browser SDK | Recommended for normal page traffic | Page views, session/visit IDs, interaction, fingerprint, GA status, honeypots, conversions |
| Tracker | Paid-click redirect or server-known landing URL | IP, headers, referrer, click IDs, UTM tags, campaign and visit identity |
| Pixel | Server-rendered fallback when JavaScript is unavailable | IP, headers, referrer, UTM tags, campaign and visit identity |

Most sites use the SDK and optionally put paid links through the tracker. The SDK is
installed with a public site key; the site key is not a secret.

```html
<script
  src="https://tel.bu.edu.bd/sdk/tg.js"
  data-site-key="tg_sk_your_public_site_key"
  data-endpoint="https://tel.bu.edu.bd">
</script>
```

If the endpoint is same-origin, `data-endpoint` may be omitted when the deployment
configuration allows the SDK to derive it. When the API is cross-origin, keep
`data-endpoint` explicit and configure the page's CSP/CORS accordingly.

## 2. Browser beacon API

### `GET /i/init`

Initializes a browser session and returns the values needed to authenticate subsequent
beacons. It also returns active browser conversion goals and configured decoy paths.

```http
GET /i/init?k=tg_sk_your_public_site_key&sid=4f8c... HTTP/1.1
```

The SDK calls this automatically. A successful response includes values similar to:

```json
{
  "nonce": "...",
  "storageTs": 1724500000000,
  "storageSig": "...",
  "decoyPaths": ["/tg-decoy/catalog-preview"],
  "conversionGoals": [
    {
      "goalId": "11111111-1111-4111-8111-111111111111",
      "name": "Sports engagement",
      "triggerType": "time_on_page",
      "pagePaths": ["/sports", "/sports/cricket"],
      "selector": null,
      "minimumSeconds": 45
    }
  ]
}
```

Do not cache this response. It is session-specific.

### `POST /i`

The SDK sends a signed JSON envelope as `text/plain` (or JSON when `fetch` is used as a
fallback). The endpoint accepts one envelope containing one or more events.

```json
{
  "k": "tg_sk_your_public_site_key",
  "session_id": "d83e31c1-0a8b-401f-a558-ca22b94396af",
  "sid": "f188cb3b-1397-4bab-9b69-cfb2c308e245",
  "visit_id": "f188cb3b-1397-4bab-9b69-cfb2c308e245",
  "device_id": "dev_9f3c2a1b",
  "seq": 0,
  "nonce": "...",
  "sent_at": 1724500000000,
  "u": "https://example.com/sports?utm_source=facebook",
  "r": "https://www.google.com/",
  "ck": {"_fbp": "..."},
  "events": [
    {"e": "pv", "t": 12},
    {"e": "sc", "t": 450, "y": 600}
  ],
  "c": "checksum"
}
```

The SDK adds `device_id` as a persistent first-party identifier when storage is
available. `session_id` groups related page visits; `visit_id` identifies one document
load. A reload normally creates a new visit but keeps the same session.

The API validates the site key, sequence, nonce, timestamp, envelope shape, and checksum.
Invalid data is silently dropped with `204`.

### Browser event vocabulary

The SDK currently emits these events. Event timestamps are relative browser timing values;
the server converts them into session aggregates and never exposes raw keystrokes or form
values.

| Event | Captured information |
|---|---|
| `pv` | Page-view marker |
| `pm` | Coalesced mouse/pointer coordinates and timing; coordinates are not sent to MarketIQ, only derived counts/statistics are |
| `pd`, `cl` | Trusted pointer-down/click, pointer type, click timing |
| `sc` | Coalesced scroll position samples |
| `ky` | Keydown timing/count only; never key identity or text |
| `ff`, `fb`, `fs` | Form focus, blur, and submit timing; never field contents |
| `pa` | Coarse paste classification, such as identity-field paste |
| `af` | Autofill signal |
| `hp` | Honeypot focus, input, submit-filled, or hidden-link click |
| `fi` | First trusted interaction modality |
| `rt` | First-contentful/first-paint timing when available |
| `ga` | Google Analytics status: `loaded`, `blocked`, `unknown`, `page_view_sent`, or `page_view_accepted` |
| `fp` | Browser/device fingerprint material, BotD flags, screen geometry, language, timezone, storage-age evidence |

## 3. Tracker and pixel requests

Tracker and pixel routes are deployment-specific, but their input is conceptually:

```text
site key, campaign/landing identifier, visitor IP, User-Agent,
request headers, referrer, click IDs, UTM parameters, and cookies visible to the server
```

The tracker is appropriate when a campaign link first passes through TelemetryGuard. It
can preserve the campaign's internal ID and redirect to the configured landing URL. The
pixel is a fallback capture path and does not invent browser interaction or fingerprint
signals.

## 4. Attribution fields

TelemetryGuard preserves these marketing parameters when present:

`utm_source`, `utm_medium`, `utm_campaign`, `utm_content`, `utm_term`, `utm_id`,
`utm_platform`, `utm_publisher_id`, `utm_campaign_id`, `gclid`, `gbraid`, `wbraid`,
`fbclid`, `ttclid`, and `msclkid`.

For the MarketIQ export, the mapping is:

| MarketIQ field | Source and precedence |
|---|---|
| `utm_platform` | Explicit `utm_platform`; otherwise normalized `utm_source`; otherwise click-ID type |
| `utm_publisher_id` | Explicit `utm_publisher_id`; otherwise an available `utm_content` placement/creative identifier |
| `utm_campaign_id` | Explicit `utm_campaign_id`; otherwise `utm_id`, then legacy/internal campaign value |

A platform name such as `facebook` must not be used as a publisher ID. If no real
publisher/placement identifier exists, omit `utm_publisher_id` rather than inventing one.

## 5. Data TelemetryGuard derives

### Identity and request context

- Tenant/site key, session ID, visit ID, device ID when available
- Visitor IP (server-observed; never trusted from a client-supplied value)
- User-Agent, Client Hints, Accept-Language, referrer, document referrer
- Header names and server-visible cookies, subject to deployment retention/access policy
- Landing URL, path, and query-key names
- TLS JA3/JA4 and Cloudflare edge fields when the proxy supplies them

### Browser and interaction aggregates

- Mouse event/sample count
- Touch event count
- Scroll event count
- Keystroke count, without key identity or typed content
- Pages viewed in the session
- Time on page
- Mean and standard deviation of inter-event timing
- Mouse-path linearity when enough samples exist
- First-interaction delay
- Click-before-render
- Form submitted and form-fill duration
- Paste in identity fields and autofill indicators
- Input modality mismatch

Missing signals remain missing internally. In the MarketIQ visit payload, the interaction
counts are emitted as numeric values (zero means no interaction was observed for that
visit/session); derived values are omitted when they cannot be calculated.

### Fingerprint and automation signals

- `navigator.webdriver`
- Headless/BotD indicators
- Emulator/VM heuristic
- Screen-resolution anomaly
- Cookies disabled
- Canvas fingerprint blocked/randomized
- Browser timezone and language
- Storage age and repeated zero-age storage behavior
- Beacon integrity, sequence, nonce, and clock-skew status

These are evidence signals. They are not individually proof of fraud.

### Network enrichment

When the local enrichment databases are available, TelemetryGuard can attach:

- Country, city, latitude, longitude, timezone
- ASN and ASN organization/type
- Datacenter/hosting classification
- Proxy/VPN, Tor, and Private Relay indicators
- IP reputation and geo-target mismatch where configured

## 6. Fraud decision data

For a finalized visit TelemetryGuard calculates and stores:

- Numeric score from 0–100
- Verdict band
- Enforced action (`allow`, `challenge`, or `block`, subject to tenant policy)
- Rule hits
- Scorer version and feature-set version
- Complete projected `FraudFeatureVector`, with unavailable floating-point values serialized as `null`
- Optional shadow-model score/version

The system can operate in observe-only or approval-queue modes. MarketIQ delivery does not
change the local enforcement decision.

## 7. Conversion API

Conversion goals are configured through the admin portal or admin API. Browser-supported
triggers are:

- `time_on_page` with `minimumSeconds`
- `link_click` with a CSS `selector`
- `button_click` with a CSS `selector`
- `form_submitted` with a CSS `selector`
- `server` for an authenticated backend confirmation

Browser conversions are recorded through `POST /i/conversion` and are idempotent by event
ID. Trusted conversions use `POST /admin/conversions/events` and are marked verified.

MarketIQ conversion payloads include `event_id`, `session_id`, `occurred_at`,
`page_to_conversion_ms`, `verified_conversion`, conversion goal ID/name, company ID, and
the visitor IP when available.

## 8. MarketIQ delivery

When a site has MarketIQ enabled, TelemetryGuard writes a durable SQL outbox record and
notifies the Redis delivery stream. A background worker performs the HTTP call with the
configured relay key and retries according to its delivery policy. The public collector
response is intentionally treated as `204 No Content`; delivery status is tracked in the
outbox/audit data, not inferred from a synchronous browser response.

### Visit payload

The current visit export can contain:

```json
{
  "companyId": 2,
  "event_id": "visit-guid",
  "session_id": "session-guid",
  "device_id": "device-guid",
  "occurred_at": "2026-08-25T14:05:22.0000000Z",
  "user_agent": "Mozilla/5.0 ...",
  "ip": "203.0.113.45",
  "utm_platform": "facebook",
  "utm_publisher_id": "pub_10457",
  "utm_campaign_id": "269",
  "is_mobile": true,
  "mouse_events": 23,
  "scroll_events": 7,
  "touch_events": 12,
  "keystrokes": 0,
  "time_on_page_sec": 2.223,
  "pages_viewed": 1,
  "form_submitted": false,
  "referrer_missing": false,
  "honeypot_touched": false,
  "honeypot_field_filled": false,
  "honeypot_link_clicked": false,
  "honey_identifier_seen": false,
  "decoy_page": false,
  "score": 18,
  "band": "allow",
  "action": "allow",
  "rule_hits": [],
  "scorer_version": "heuristic-1",
  "feature_set_version": 1,
  "fraud_features": {},
  "country": "BD",
  "asn": 64500,
  "ip_type": "isp",
  "is_datacenter": false,
  "is_proxy_or_vpn": false,
  "is_tor": false,
  "tg_export_mode": "live"
}
```

Fields with no reliable value are omitted. The example is illustrative; scores, geo data,
and interaction counts are generated per visit.

The visitor IP is sent only under the configured MarketIQ relay-key contract. It is the
end visitor's address, not the TelemetryGuard server address. Do not send a caller-claimed
IP to TelemetryGuard expecting it to be trusted.

## 9. Security and privacy boundaries

- Site keys are public identifiers, not credentials.
- Admin endpoints require a tenant admin API key and are not part of the public beacon API.
- Public ingestion does not expose scores or stored records synchronously.
- TelemetryGuard never collects key identity, typed text, password contents, or form values.
- Honeypots report interaction metadata only; hidden field values are not transmitted.
- Access to raw headers, cookies, landing URLs, and IP data must be restricted because they
  can contain sensitive information.
- Configure retention, tenant access, MarketIQ relay keys, and admin keys outside source
  control.

## 10. Operational expectations

Telemetry is asynchronous. A browser may send a beacon successfully while the visitor's
page closes, a content blocker prevents another vendor's request, or a downstream worker
is still processing the event. For delivery verification, inspect TelemetryGuard's outbox
status and MarketIQ's health/ingestion counters; do not use the browser's `204` alone as a
delivery receipt.

For implementation questions, start with [how-it-works.md](how-it-works.md) for the
request lifecycle and [conversions.md](conversions.md) for conversion-goal configuration.

## 11. Reading reports and traffic data

TelemetryGuard does not make public traffic data readable through the beacon endpoints.
Reads use the protected admin API, the portal, or an authorized analytics query.

### Admin API authentication

Every `/admin/*` request requires the tenant's admin API key in the header:

```http
X-Api-Key: tg_ak_your_admin_key
```

The portal stores this key in its encrypted session cookie and forwards it server-side;
the key is never put in a URL. Never place an admin key in browser JavaScript, a dashboard
URL, or source control.

### Available report endpoints

| Endpoint | Reads | Important limitation |
|---|---|---|
| `GET /admin/reports/summary` | Daily verdict totals, scores, allowed/challenged/blocked | Counts scored verdict events, not unique sessions or HTTP requests |
| `GET /admin/reports/flagged-sources` | Flagged IP/device/fingerprint sources | Only sources with flagged verdicts are returned |
| `GET /admin/reports/publishers` | Daily placement/publisher rollups | Placement totals are scored events, not session counts |
| `GET /admin/reports/sites` | Daily per-site rollups | `Events` means scored verdict events |
| `GET /admin/reports/domain-traffic` | Narrow, redacted traffic lookup | Does not expose cookies or raw headers |
| `POST /admin/analytics/clickhouse/query` | Controlled tenant-scoped ClickHouse read | Single read-only `SELECT`/`WITH`, max 10,000 rows, 30-second timeout |

For MarketIQ or another approved analytics consumer that needs a query not covered by a
prebuilt report, use the ClickHouse read-through endpoint. It still uses the admin API key
and requires the tenant placeholder in every query:

```http
POST /admin/analytics/clickhouse/query
X-Api-Key: tg_ak_your_admin_key
Content-Type: application/json
```

```json
{
  "sql": "SELECT utm_source, countDistinct(visit_id) AS requests FROM telemetry_guard.tg_events WHERE tenant_id = {tenantId:UUID} AND site_key = 'tg_sk_your_public_site_key' AND timestamp >= now() - INTERVAL 3 DAY GROUP BY utm_source ORDER BY requests DESC",
  "maxRows": 500
}
```

The API binds `{tenantId:UUID}` from the authenticated tenant; callers cannot substitute
another tenant. It accepts exactly one comment-free `SELECT` or `WITH` statement, rejects
multi-statements, DML/DDL, system and metadata namespaces, and applies a server-side row
limit and timeout. Configure `Analytics:ClickHouse:ReadConnectionString` to a ClickHouse
account granted `SELECT` only. The existing provider connection is used as a compatibility
fallback, but production deployments should always configure the separate read-only account.

Successful responses have this shape:

```json
{
  "columns": ["utm_source", "requests"],
  "rows": [
    {"utm_source": "facebook", "requests": 5512},
    {"utm_source": "tiktok", "requests": 1915}
  ],
  "truncated": false
}
```

`columns` preserves the ClickHouse result-column order. `rows` contains JSON-safe scalar
values, with database `NULL` represented as JSON `null`. `truncated: true` means the
server-side `maxRows` limit was reached and the caller must narrow the time range or query.

Typical responses are:

| Status | Meaning |
|---:|---|
| `200` | Query completed |
| `400` | Query is not an allowed single read statement, lacks the tenant placeholder, or exceeds validation limits |
| `401` | Missing or invalid `X-Api-Key` |
| `403` | Key is valid but not authorized for the tenant/scope |
| `408`/`504` | Query timed out or was cancelled |
| `429` | Rate limit reached |

The endpoint is intended for summarized and bounded drilldown queries. For repeated
dashboards, use the prebuilt report endpoints or a saved-query layer rather than sending
large unbounded scans on every page refresh.

Date-based reports use `from` and `to` in `yyyy-MM-dd` format. For example:

```http
GET /admin/reports/sites?from=2026-08-23&to=2026-08-25
X-Api-Key: tg_ak_your_admin_key
```

### Sessions, requests, and Facebook/TikTok breakdowns

For traffic reporting, use these definitions:

- **Request/page visit:** one distinct `visit_id`, normally one document load.
- **Session:** one distinct `session_id`, grouping one visitor's related visits.
- **Facebook request:** a distinct visit whose attribution is `meta_ads` or whose
  `utm_source`/click ID identifies Facebook/Meta.
- **Facebook session:** a distinct session containing at least one Facebook-attributed
  visit.
- **TikTok request/session:** the same calculation using `tiktok_ads`/TikTok attribution.
- **Organic:** a visit/session without a paid platform attribution, normally classified as
  `organic_search`, `referral`, or `direct`.

Do not count `tg_events` rows directly as requests. One visit can generate several rows:
beacons, fingerprint events, and one final verdict. Counting rows would overstate traffic.

The detailed source for this calculation is the ClickHouse `tg_events` table. An authorized
analytics service can use a query shaped like this (replace the tenant/site values and
time range):

```sql
WITH visits AS
(
    SELECT
        visit_id,
        any(session_id) AS session_id,
        argMax(attribution_channel, timestamp) AS channel,
        argMax(utm_source, timestamp) AS utm_source,
        argMax(fbclid, timestamp) AS fbclid,
        argMax(ttclid, timestamp) AS ttclid
    FROM telemetry_guard.tg_events
    WHERE tenant_id = toUUID('TENANT_UUID')
      AND site_key = 'tg_sk_your_public_site_key'
      AND visit_id != ''
      AND timestamp >= toDateTime64('2026-08-23 00:00:00', 3, 'UTC')
      AND timestamp <  toDateTime64('2026-08-26 00:00:00', 3, 'UTC')
    GROUP BY visit_id
)
SELECT
    count() AS requests,
    uniqExact(session_id) AS sessions,
    countIf(channel = 'meta_ads' OR lower(utm_source) IN ('facebook','fb','meta') OR fbclid != '') AS facebook_requests,
    uniqExactIf(session_id, channel = 'meta_ads' OR lower(utm_source) IN ('facebook','fb','meta') OR fbclid != '') AS facebook_sessions,
    countIf(channel = 'tiktok_ads' OR lower(utm_source) = 'tiktok' OR ttclid != '') AS tiktok_requests,
    uniqExactIf(session_id, channel = 'tiktok_ads' OR lower(utm_source) = 'tiktok' OR ttclid != '') AS tiktok_sessions
FROM visits;
```

For an hourly or daily chart, add a bucket to the inner query and group by it:

```sql
toStartOfHour(timestamp) AS utc_hour
```

The site timezone should be applied when presenting the result; stored event timestamps are
UTC. For a strict “first attribution wins” report, use the earliest event for each visit
instead of `argMax`; for a final-state report, use the latest event as shown above.

### Why the portal may show different numbers

The following numbers are intentionally different measures:

1. Raw `tg_events` rows include multiple beacons per visit.
2. Verdict reports count one scored verdict per finalized visit.
3. Unique visit reports count `visit_id`.
4. Unique session reports count `session_id`.
5. GA4 page views/sessions are a separate vendor's processed measurements and can be
   lower because of consent, blockers, script timing, or reporting delay.

If a dashboard needs “last 3 days: sessions, visits, Facebook sessions, Facebook visits,
TikTok sessions, TikTok visits,” it should query a dedicated aggregate built from distinct
`visit_id`/`session_id` values. It should not reuse `/admin/reports/summary` event totals or
sum beacon rows.
