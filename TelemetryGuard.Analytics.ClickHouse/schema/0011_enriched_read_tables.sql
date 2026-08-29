-- Persist first-observed visit enrichment and first/last session enrichment.
-- These compact state tables update incrementally and avoid mutations or raw-event scans.

CREATE TABLE IF NOT EXISTS tg_visit_ip_states
(
 tenant_id UUID, visit_id String,
 first_ip AggregateFunction(argMin, IPv6, DateTime64(3, 'UTC')),
 first_asn AggregateFunction(argMin, Nullable(UInt32), DateTime64(3, 'UTC')),
 first_ip_type AggregateFunction(argMin, Nullable(String), DateTime64(3, 'UTC')),
 first_city AggregateFunction(argMin, Nullable(String), DateTime64(3, 'UTC')),
 first_country AggregateFunction(argMin, Nullable(String), DateTime64(3, 'UTC')),
 first_is_datacenter AggregateFunction(argMin, Nullable(UInt8), DateTime64(3, 'UTC')),
 first_is_proxy AggregateFunction(argMin, Nullable(UInt8), DateTime64(3, 'UTC')),
 first_is_vpn AggregateFunction(argMin, Nullable(UInt8), DateTime64(3, 'UTC')),
 first_is_tor AggregateFunction(argMin, Nullable(UInt8), DateTime64(3, 'UTC')),
 first_is_private_relay AggregateFunction(argMin, Nullable(UInt8), DateTime64(3, 'UTC'))
) ENGINE=AggregatingMergeTree ORDER BY (tenant_id, visit_id);

CREATE MATERIALIZED VIEW IF NOT EXISTS tg_visit_ip_states_mv TO tg_visit_ip_states AS
SELECT tenant_id, visit_id,
 argMinState(ip,timestamp) first_ip, argMinState(asn,timestamp) first_asn,
 argMinState(asn_type,timestamp) first_ip_type, argMinState(city,timestamp) first_city,
 argMinState(country,timestamp) first_country,
 argMinState(is_datacenter,timestamp) first_is_datacenter,
 argMinState(is_proxy,timestamp) first_is_proxy, argMinState(is_vpn,timestamp) first_is_vpn,
 argMinState(is_tor,timestamp) first_is_tor,
 argMinState(is_private_relay,timestamp) first_is_private_relay
FROM tg_events WHERE visit_id!='' GROUP BY tenant_id,visit_id;

CREATE TABLE IF NOT EXISTS tg_session_ip_states
(
 tenant_id UUID, session_id String,
 first_ip AggregateFunction(argMin, IPv6, DateTime64(3, 'UTC')),
 first_asn AggregateFunction(argMin, Nullable(UInt32), DateTime64(3, 'UTC')),
 first_ip_type AggregateFunction(argMin, Nullable(String), DateTime64(3, 'UTC')),
 first_city AggregateFunction(argMin, Nullable(String), DateTime64(3, 'UTC')),
 first_country AggregateFunction(argMin, Nullable(String), DateTime64(3, 'UTC')),
 first_is_datacenter AggregateFunction(argMin, Nullable(UInt8), DateTime64(3, 'UTC')),
 first_is_proxy AggregateFunction(argMin, Nullable(UInt8), DateTime64(3, 'UTC')),
 first_is_vpn AggregateFunction(argMin, Nullable(UInt8), DateTime64(3, 'UTC')),
 first_is_tor AggregateFunction(argMin, Nullable(UInt8), DateTime64(3, 'UTC')),
 first_is_private_relay AggregateFunction(argMin, Nullable(UInt8), DateTime64(3, 'UTC')),
 last_ip AggregateFunction(argMax, IPv6, DateTime64(3, 'UTC')),
 last_asn AggregateFunction(argMax, Nullable(UInt32), DateTime64(3, 'UTC')),
 last_ip_type AggregateFunction(argMax, Nullable(String), DateTime64(3, 'UTC')),
 last_city AggregateFunction(argMax, Nullable(String), DateTime64(3, 'UTC')),
 last_country AggregateFunction(argMax, Nullable(String), DateTime64(3, 'UTC')),
 last_is_datacenter AggregateFunction(argMax, Nullable(UInt8), DateTime64(3, 'UTC')),
 last_is_proxy AggregateFunction(argMax, Nullable(UInt8), DateTime64(3, 'UTC')),
 last_is_vpn AggregateFunction(argMax, Nullable(UInt8), DateTime64(3, 'UTC')),
 last_is_tor AggregateFunction(argMax, Nullable(UInt8), DateTime64(3, 'UTC')),
 last_is_private_relay AggregateFunction(argMax, Nullable(UInt8), DateTime64(3, 'UTC'))
) ENGINE=AggregatingMergeTree ORDER BY (tenant_id, session_id);

CREATE MATERIALIZED VIEW IF NOT EXISTS tg_session_ip_states_mv TO tg_session_ip_states AS
SELECT tenant_id,session_id,
 argMinState(ip,timestamp) first_ip, argMinState(asn,timestamp) first_asn,
 argMinState(asn_type,timestamp) first_ip_type, argMinState(city,timestamp) first_city,
 argMinState(country,timestamp) first_country,
 argMinState(is_datacenter,timestamp) first_is_datacenter,
 argMinState(is_proxy,timestamp) first_is_proxy, argMinState(is_vpn,timestamp) first_is_vpn,
 argMinState(is_tor,timestamp) first_is_tor,
 argMinState(is_private_relay,timestamp) first_is_private_relay,
 argMaxState(ip,timestamp) last_ip, argMaxState(asn,timestamp) last_asn,
 argMaxState(asn_type,timestamp) last_ip_type, argMaxState(city,timestamp) last_city,
 argMaxState(country,timestamp) last_country,
 argMaxState(is_datacenter,timestamp) last_is_datacenter,
 argMaxState(is_proxy,timestamp) last_is_proxy, argMaxState(is_vpn,timestamp) last_is_vpn,
 argMaxState(is_tor,timestamp) last_is_tor,
 argMaxState(is_private_relay,timestamp) last_is_private_relay
FROM tg_events WHERE session_id!='' GROUP BY tenant_id,session_id;

CREATE OR REPLACE VIEW tg_visit_ip_enrichment AS
SELECT tenant_id,visit_id, IPv6NumToString(argMinMerge(first_ip)) ip,
 argMinMerge(first_asn) asn,argMinMerge(first_ip_type) ip_type,
 argMinMerge(first_city) city,argMinMerge(first_country) country,
 argMinMerge(first_is_datacenter) is_datacenter,argMinMerge(first_is_proxy) is_proxy,
 argMinMerge(first_is_vpn) is_vpn,argMinMerge(first_is_tor) is_tor,
 argMinMerge(first_is_private_relay) is_private_relay
FROM tg_visit_ip_states GROUP BY tenant_id,visit_id;

CREATE OR REPLACE VIEW tg_session_ip_enrichment AS
SELECT tenant_id,session_id,
 IPv6NumToString(argMinMerge(first_ip)) first_ip,argMinMerge(first_asn) first_asn,
 argMinMerge(first_ip_type) first_ip_type,argMinMerge(first_city) first_city,
 argMinMerge(first_country) first_country,
 argMinMerge(first_is_datacenter) first_is_datacenter,argMinMerge(first_is_proxy) first_is_proxy,
 argMinMerge(first_is_vpn) first_is_vpn,argMinMerge(first_is_tor) first_is_tor,
 argMinMerge(first_is_private_relay) first_is_private_relay,
 IPv6NumToString(argMaxMerge(last_ip)) last_ip,argMaxMerge(last_asn) last_asn,
 argMaxMerge(last_ip_type) last_ip_type,argMaxMerge(last_city) last_city,
 argMaxMerge(last_country) last_country,
 argMaxMerge(last_is_datacenter) last_is_datacenter,argMaxMerge(last_is_proxy) last_is_proxy,
 argMaxMerge(last_is_vpn) last_is_vpn,argMaxMerge(last_is_tor) last_is_tor,
 argMaxMerge(last_is_private_relay) last_is_private_relay
FROM tg_session_ip_states GROUP BY tenant_id,session_id;

CREATE OR REPLACE VIEW tg_visits_enriched AS
SELECT b.tenant_id tenant_id,b.visit_id visit_id,b.session_id session_id,b.site_key site_key,
 b.first_seen first_seen,b.last_seen last_seen,b.event_count event_count,
 b.beacon_count beacon_count,b.verdict_count verdict_count,b.landing_url landing_url,
 b.landing_path landing_path,b.attribution_channel attribution_channel,b.utm_source utm_source,
 b.utm_medium utm_medium,b.utm_campaign utm_campaign,b.utm_content utm_content,
 b.utm_term utm_term,b.score score,b.band band,b.action action,b.rule_hits rule_hits,
 b.verdict_at verdict_at,b.retention_days retention_days,i.ip ip,i.asn asn,
 i.ip_type ip_type,i.city city,i.country country,i.is_datacenter is_datacenter,
 i.is_proxy is_proxy,i.is_vpn is_vpn,i.is_tor is_tor,i.is_private_relay is_private_relay
FROM tg_visits_base b LEFT JOIN tg_visit_ip_enrichment i
ON b.tenant_id=i.tenant_id AND b.visit_id=i.visit_id;

CREATE OR REPLACE VIEW tg_sessions_enriched AS
SELECT b.tenant_id tenant_id,b.session_id session_id,b.first_seen first_seen,
 b.last_seen last_seen,b.event_count event_count,b.visit_count visit_count,
 b.last_visit_id last_visit_id,b.last_site_key last_site_key,b.retention_days retention_days,
 i.first_ip first_ip,i.first_asn first_asn,i.first_ip_type first_ip_type,
 i.first_city first_city,i.first_country first_country,
 i.first_is_datacenter first_is_datacenter,i.first_is_proxy first_is_proxy,
 i.first_is_vpn first_is_vpn,i.first_is_tor first_is_tor,
 i.first_is_private_relay first_is_private_relay,i.last_ip last_ip,i.last_asn last_asn,
 i.last_ip_type last_ip_type,i.last_city last_city,i.last_country last_country,
 i.last_is_datacenter last_is_datacenter,i.last_is_proxy last_is_proxy,
 i.last_is_vpn last_is_vpn,i.last_is_tor last_is_tor,
 i.last_is_private_relay last_is_private_relay
FROM tg_sessions_base b LEFT JOIN tg_session_ip_enrichment i
ON b.tenant_id=i.tenant_id AND b.session_id=i.session_id;

CREATE OR REPLACE VIEW tg_visits AS SELECT * FROM tg_visits_enriched;
CREATE OR REPLACE VIEW tg_sessions AS SELECT * FROM tg_sessions_enriched;
