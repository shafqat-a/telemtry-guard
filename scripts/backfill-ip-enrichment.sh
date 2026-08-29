#!/usr/bin/env bash
set -euo pipefail

# Resumable historical city backfill. Requires curl and ClickHouse HTTP access.
# Usage: CLICKHOUSE_PASSWORD=... ./scripts/backfill-ip-enrichment.sh

CH_URL="${CLICKHOUSE_URL:-http://localhost:8123}"
CH_DB="${CLICKHOUSE_DB:-telemetry_guard}"
CH_USER="${CLICKHOUSE_USER:-tg}"
CH_PASSWORD="${CLICKHOUSE_PASSWORD:?Set CLICKHOUSE_PASSWORD}"
BATCH_SIZE="${BATCH_SIZE:-250}"

query() {
  curl -fsS "$CH_URL/?database=$CH_DB" --user "$CH_USER:$CH_PASSWORD" --data-binary "$1"
}

offset=0
while :; do
  ips=$(query "SELECT IPv6NumToString(ip) FROM tg_ip_city WHERE city IS NOT NULL ORDER BY ip LIMIT $BATCH_SIZE OFFSET $offset FORMAT TabSeparated")
  [[ -z "$ips" ]] && break
  while IFS= read -r ip; do
    [[ -z "$ip" ]] && continue
    escaped=${ip//\\/\\\\}; escaped=${escaped//\'/\'\'}
    query "ALTER TABLE tg_events UPDATE city = joinGet('telemetry_guard.tg_ip_city_join', 'city', ip) WHERE ip = toIPv6('$escaped')" >/dev/null
  done <<< "$ips"
  offset=$((offset + BATCH_SIZE))
  echo "queued $offset historical IPs" >&2
  # Keep mutation pressure low; ClickHouse applies these asynchronously.
  sleep 1
done
echo "Backfill mutations queued for $offset IPs" >&2
