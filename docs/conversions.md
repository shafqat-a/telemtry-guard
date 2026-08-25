# Conversion tracking

TelemetryGuard supports multiple conversion goals on one page and the same goal on multiple exact page paths.

## Goal triggers

- `time_on_page`: fires after `minimumSeconds` on a matching page.
- `link_click` / `button_click`: fires when the clicked element or an ancestor matches `selector`.
- `form_submitted`: fires when a matching form submits. Field contents are never read or transmitted.
- `server`: accepts only an authenticated admin API call and produces a verified conversion.

Browser goals are delivered by `/i/init` and recorded through `POST /i/conversion`. Every achievement has a GUID event ID and SQL insertion is idempotent. Browser events send `verified_conversion: false`; trusted server events send `verified_conversion: true`. MarketIQ delivery uses the existing SQL outbox, Redis notification stream, relay header, retries, and delivery audit.

## Admin API

- `GET /admin/conversions/goals`
- `POST /admin/conversions/goals`
- `DELETE /admin/conversions/goals/{goalId}`
- `GET /admin/conversions/export`
- `POST /admin/conversions/import`
- `POST /admin/conversions/events` for trusted server-confirmed conversions

The JSON document format is `{ "version": 1, "goals": [...] }`. Import upserts goals by `goalId`; it does not delete goals omitted from the document.

## Destination status

MarketIQ publishing is implemented. Meta, Google Ads, GA4 and TikTok flags currently preserve per-goal routing intent only; enabling those publishers requires their platform credentials and destination-specific adapters.
