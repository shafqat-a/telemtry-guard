ALTER TABLE tg_events ADD COLUMN IF NOT EXISTS city Nullable(String) AFTER country;

-- Preserve the original aggregate-backed views and expose enriched projections.
RENAME TABLE tg_visits TO tg_visits_base;
RENAME TABLE tg_sessions TO tg_sessions_base;

CREATE VIEW tg_visits AS
SELECT b.*, g.ip, g.asn, g.asn_type AS ip_type, g.city, g.country,
       g.is_datacenter, g.is_proxy, g.is_vpn, g.is_tor, g.is_private_relay
FROM tg_visits_base b
LEFT JOIN
(
 SELECT tenant_id, visit_id,
   argMin(IPv6NumToString(ip), timestamp) ip,
   argMin(asn, timestamp) asn, argMin(asn_type, timestamp) asn_type,
   argMin(city, timestamp) city, argMin(country, timestamp) country,
   argMin(is_datacenter, timestamp) is_datacenter, argMin(is_proxy, timestamp) is_proxy,
   argMin(is_vpn, timestamp) is_vpn, argMin(is_tor, timestamp) is_tor,
   argMin(is_private_relay, timestamp) is_private_relay
 FROM tg_events WHERE visit_id != '' GROUP BY tenant_id, visit_id
) g ON b.tenant_id=g.tenant_id AND b.visit_id=g.visit_id;

CREATE VIEW tg_sessions AS
SELECT b.*,
       f.first_ip, f.first_asn, f.first_ip_type, f.first_city, f.first_country,
       l.last_ip, l.last_asn, l.last_ip_type, l.last_city, l.last_country
FROM tg_sessions_base b
LEFT JOIN
(
 SELECT tenant_id, session_id,
   argMin(IPv6NumToString(ip), timestamp) first_ip, argMin(asn, timestamp) first_asn,
   argMin(asn_type, timestamp) first_ip_type, argMin(city, timestamp) first_city,
   argMin(country, timestamp) first_country
 FROM tg_events WHERE session_id != '' GROUP BY tenant_id, session_id
) f ON b.tenant_id=f.tenant_id AND b.session_id=f.session_id
LEFT JOIN
(
 SELECT tenant_id, session_id,
   argMax(IPv6NumToString(ip), timestamp) last_ip, argMax(asn, timestamp) last_asn,
   argMax(asn_type, timestamp) last_ip_type, argMax(city, timestamp) last_city,
   argMax(country, timestamp) last_country
 FROM tg_events WHERE session_id != '' GROUP BY tenant_id, session_id
) l ON b.tenant_id=l.tenant_id AND b.session_id=l.session_id;
