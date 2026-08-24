# MarketIQ click delivery

TelemetryGuard publishes one finalized record per `visit_id`. SQL Server's
`dbo.MarketIqOutbox` is the durable source of truth; the Redis Stream
`tg:marketiq:dispatch` only wakes/distributes workers. A ten-second SQL scan recovers
messages if Redis is cleared or a notification is missed.

Per-site settings live on `dbo.Sites`: `MarketIqEnabled`, `MarketIqCompanyId`,
`MarketIqCollectUrl`, `MarketIqHealthUrl`, and `MarketIqHealthTokenRef`. Tokens never
belong in SQL or source control. Supply them through configuration using the token ref:

```text
MarketIq__Enabled=true
MarketIq__HealthTokens__<token-ref>=<bearer-token>
```

BU uses company ID `2`, token ref `miq_bu_edu_bd_telemetry_guard`, and the dedicated
Azure Click Collection host. Delivery success means HTTP 204. Network/non-204 failures
are retried with exponential backoff and eventually dead-lettered in SQL. MarketIQ's
protected health endpoint is checked every five minutes; a positive `lost` counter is
logged as an error.

MarketIQ receives no caller-supplied IP field because its API discards it. The payload
includes TG session/device identity, attribution, behavioral counts, and the finalized
fraud feature vector where values are available.
