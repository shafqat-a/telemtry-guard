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
