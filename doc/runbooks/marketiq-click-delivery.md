# MarketIQ click delivery

TelemetryGuard publishes one finalized record per `visit_id`. SQL Server's
`dbo.MarketIqOutbox` is the durable source of truth; the Redis Stream
`tg:marketiq:dispatch` only wakes/distributes workers. A ten-second SQL scan recovers
messages if Redis is cleared or a notification is missed.

Per-site settings live on `dbo.Sites`: `MarketIqEnabled`, `MarketIqCompanyId`,
`MarketIqCollectUrl`, `MarketIqHealthUrl`, `MarketIqHealthTokenRef`, and
`MarketIqRelayKeyRef`. Keys never
belong in SQL or source control. Supply them through configuration using the token ref:

```text
MarketIq__Enabled=true
MarketIq__RelayKeys__<key-ref>=<relay-key>
MarketIq__HealthKeys__<key-ref>=<health-key>
```

BU uses company ID `2`, token ref `miq_bu_edu_bd_telemetry_guard`, and the dedicated
Azure Click Collection host. Delivery success means HTTP 204. Network/non-204 failures
are retried with exponential backoff and eventually dead-lettered in SQL. MarketIQ's
protected health endpoint is checked every five minutes; a positive `lost` counter is
logged as an error.

The trusted relay sends the end visitor's IP with `X-Ingest-Relay-Key`, plus TG
session/device identity, attribution, behavioral counts, and the finalized fraud
feature vector where values are available. Live records carry
`tg_export_mode: "live"`.

## Behavioral signal semantics

- Missing is never zero. Unknown/non-finite counters and durations must be omitted.
- `mouse_events` is the number of mouse points (`mm_n + 1` when `mm_first_x`
  exists), not the number of gaps between points.
- `time_on_page_sec` is derived from the first page-view's navigation-relative
  timestamp and the latest accepted beacon receive time. Client wall-clock time is
  not trusted for this calculation.

## Historical replay

Historical replay payloads must carry `tg_export_mode: "historical_backfill"`.
ClickHouse aggregate functions return a type-default zero when an `argMaxIf` has no
matching row, so a backfill must explicitly guard every nullable behavioral value:

```sql
if(countIf(isFinite(mouse_event_count)) = 0, NULL,
   argMaxIf(toNullable(mouse_event_count), timestamp,
            isFinite(mouse_event_count))) AS mouse_events
```

Use the same `countIf(...)=0 -> NULL` rule for key, touch, scroll, dwell, and all
other non-finite feature values. Remove null properties when constructing JSON; do
not coalesce them to zero. Replays require a replacement/deduplication agreement
with MarketIQ before already-delivered event IDs are sent again.
