#!/usr/bin/env bash
set -euo pipefail

# Mutation-free and idempotent: aggregate states safely merge repeat executions.
ch_url="${CLICKHOUSE_URL:-http://localhost:8123}"
ch_db="${CLICKHOUSE_DB:-telemetry_guard}"
ch_user="${CLICKHOUSE_USER:-tg}"
ch_password="${CLICKHOUSE_PASSWORD:?Set CLICKHOUSE_PASSWORD}"
query() { curl -fsS "$ch_url/?database=$ch_db" --user "$ch_user:$ch_password" --data-binary "$1"; }

city_expr="city"
if [[ "$(query "EXISTS TABLE tg_ip_city_join FORMAT TabSeparated")" == "1" ]]; then
  city_expr="coalesce(city,joinGet('${ch_db}.tg_ip_city_join','city',ip))"
fi

query "INSERT INTO tg_visit_ip_states SELECT tenant_id,visit_id,
 argMinState(ip,timestamp),argMinState(asn,timestamp),argMinState(asn_type,timestamp),
 argMinState($city_expr,timestamp),argMinState(country,timestamp),
 argMinState(is_datacenter,timestamp),argMinState(is_proxy,timestamp),argMinState(is_vpn,timestamp),
 argMinState(is_tor,timestamp),argMinState(is_private_relay,timestamp)
 FROM tg_events WHERE visit_id!='' GROUP BY tenant_id,visit_id"

query "INSERT INTO tg_session_ip_states SELECT tenant_id,session_id,
 argMinState(ip,timestamp),argMinState(asn,timestamp),argMinState(asn_type,timestamp),
 argMinState($city_expr,timestamp),argMinState(country,timestamp),
 argMinState(is_datacenter,timestamp),argMinState(is_proxy,timestamp),argMinState(is_vpn,timestamp),
 argMinState(is_tor,timestamp),argMinState(is_private_relay,timestamp),
 argMaxState(ip,timestamp),argMaxState(asn,timestamp),argMaxState(asn_type,timestamp),
 argMaxState($city_expr,timestamp),argMaxState(country,timestamp),
 argMaxState(is_datacenter,timestamp),argMaxState(is_proxy,timestamp),argMaxState(is_vpn,timestamp),
 argMaxState(is_tor,timestamp),argMaxState(is_private_relay,timestamp)
 FROM tg_events WHERE session_id!='' GROUP BY tenant_id,session_id"

query "SELECT 'visits',count(),countIf(city!='') FROM tg_visits
 UNION ALL SELECT 'sessions',count(),countIf(first_city!='') FROM tg_sessions FORMAT TabSeparated"
