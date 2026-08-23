-- Canonical journey hierarchy over the append-only event stream:
--   tg_sessions (one browser journey) -> tg_visits (one document load) -> tg_events.
-- Aggregate-state tables absorb repeated beacon/verdict inserts; the public views
-- expose ordinary scalar columns and exactly one logical row per identifier.

CREATE TABLE IF NOT EXISTS tg_session_states
(
    tenant_id UUID,
    session_id String,
    first_seen AggregateFunction(min, DateTime64(3, 'UTC')),
    last_seen AggregateFunction(max, DateTime64(3, 'UTC')),
    event_count AggregateFunction(count),
    visit_count AggregateFunction(uniq, String),
    last_visit_id AggregateFunction(argMax, String, DateTime64(3, 'UTC')),
    last_site_key AggregateFunction(argMax, String, DateTime64(3, 'UTC')),
    retention_days AggregateFunction(argMax, UInt16, DateTime64(3, 'UTC'))
)
ENGINE = AggregatingMergeTree
ORDER BY (tenant_id, session_id);

CREATE MATERIALIZED VIEW IF NOT EXISTS tg_session_states_mv TO tg_session_states AS
SELECT
    tenant_id,
    session_id,
    minState(timestamp) AS first_seen,
    maxState(timestamp) AS last_seen,
    countState() AS event_count,
    uniqStateIf(visit_id, visit_id != '') AS visit_count,
    argMaxState(visit_id, timestamp) AS last_visit_id,
    argMaxState(site_key, timestamp) AS last_site_key,
    argMaxState(retention_days, timestamp) AS retention_days
FROM tg_events
WHERE session_id != ''
GROUP BY tenant_id, session_id;

CREATE VIEW IF NOT EXISTS tg_sessions AS
SELECT
    tenant_id,
    session_id,
    minMerge(first_seen) AS first_seen,
    maxMerge(last_seen) AS last_seen,
    countMerge(event_count) AS event_count,
    uniqMerge(visit_count) AS visit_count,
    argMaxMerge(last_visit_id) AS last_visit_id,
    argMaxMerge(last_site_key) AS last_site_key,
    argMaxMerge(retention_days) AS retention_days
FROM tg_session_states
GROUP BY tenant_id, session_id;

CREATE TABLE IF NOT EXISTS tg_visit_states
(
    tenant_id UUID,
    visit_id String,
    session_id AggregateFunction(argMax, String, DateTime64(3, 'UTC')),
    site_key AggregateFunction(argMax, String, DateTime64(3, 'UTC')),
    first_seen AggregateFunction(min, DateTime64(3, 'UTC')),
    last_seen AggregateFunction(max, DateTime64(3, 'UTC')),
    event_count AggregateFunction(count),
    beacon_count AggregateFunction(count),
    verdict_count AggregateFunction(count),
    landing_url AggregateFunction(argMax, String, DateTime64(3, 'UTC')),
    landing_path AggregateFunction(argMax, String, DateTime64(3, 'UTC')),
    attribution_channel AggregateFunction(argMax, String, DateTime64(3, 'UTC')),
    utm_source AggregateFunction(argMax, String, DateTime64(3, 'UTC')),
    utm_medium AggregateFunction(argMax, String, DateTime64(3, 'UTC')),
    utm_campaign AggregateFunction(argMax, String, DateTime64(3, 'UTC')),
    utm_content AggregateFunction(argMax, String, DateTime64(3, 'UTC')),
    utm_term AggregateFunction(argMax, String, DateTime64(3, 'UTC')),
    score AggregateFunction(argMax, Int16, DateTime64(3, 'UTC')),
    band AggregateFunction(argMax, String, DateTime64(3, 'UTC')),
    action AggregateFunction(argMax, String, DateTime64(3, 'UTC')),
    rule_hits AggregateFunction(argMax, Array(String), DateTime64(3, 'UTC')),
    verdict_at AggregateFunction(max, DateTime64(3, 'UTC')),
    retention_days AggregateFunction(argMax, UInt16, DateTime64(3, 'UTC'))
)
ENGINE = AggregatingMergeTree
ORDER BY (tenant_id, visit_id);

CREATE MATERIALIZED VIEW IF NOT EXISTS tg_visit_states_mv TO tg_visit_states AS
SELECT
    tenant_id,
    visit_id,
    argMaxState(session_id, timestamp) AS session_id,
    argMaxState(site_key, timestamp) AS site_key,
    minState(timestamp) AS first_seen,
    maxState(timestamp) AS last_seen,
    countState() AS event_count,
    countStateIf(kind = 'beacon') AS beacon_count,
    countStateIf(kind = 'verdict') AS verdict_count,
    argMaxStateIf(landing_url, timestamp, landing_url != '') AS landing_url,
    argMaxStateIf(landing_path, timestamp, landing_path != '') AS landing_path,
    argMaxStateIf(attribution_channel, timestamp, attribution_channel != '') AS attribution_channel,
    argMaxStateIf(utm_source, timestamp, utm_source != '') AS utm_source,
    argMaxStateIf(utm_medium, timestamp, utm_medium != '') AS utm_medium,
    argMaxStateIf(utm_campaign, timestamp, utm_campaign != '') AS utm_campaign,
    argMaxStateIf(utm_content, timestamp, utm_content != '') AS utm_content,
    argMaxStateIf(utm_term, timestamp, utm_term != '') AS utm_term,
    argMaxStateIf(assumeNotNull(score), timestamp, isNotNull(score)) AS score,
    argMaxStateIf(assumeNotNull(band), timestamp, isNotNull(band)) AS band,
    argMaxStateIf(assumeNotNull(action), timestamp, isNotNull(action)) AS action,
    argMaxStateIf(rule_hits, timestamp, kind = 'verdict') AS rule_hits,
    maxStateIf(timestamp, kind = 'verdict') AS verdict_at,
    argMaxState(retention_days, timestamp) AS retention_days
FROM tg_events
WHERE visit_id != ''
GROUP BY tenant_id, visit_id;

CREATE VIEW IF NOT EXISTS tg_visits AS
SELECT
    tenant_id,
    visit_id,
    argMaxMerge(session_id) AS session_id,
    argMaxMerge(site_key) AS site_key,
    minMerge(first_seen) AS first_seen,
    maxMerge(last_seen) AS last_seen,
    countMerge(event_count) AS event_count,
    countMerge(beacon_count) AS beacon_count,
    countMerge(verdict_count) AS verdict_count,
    argMaxMerge(landing_url) AS landing_url,
    argMaxMerge(landing_path) AS landing_path,
    argMaxMerge(attribution_channel) AS attribution_channel,
    argMaxMerge(utm_source) AS utm_source,
    argMaxMerge(utm_medium) AS utm_medium,
    argMaxMerge(utm_campaign) AS utm_campaign,
    argMaxMerge(utm_content) AS utm_content,
    argMaxMerge(utm_term) AS utm_term,
    argMaxMerge(score) AS score,
    argMaxMerge(band) AS band,
    argMaxMerge(action) AS action,
    argMaxMerge(rule_hits) AS rule_hits,
    maxMerge(verdict_at) AS verdict_at,
    argMaxMerge(retention_days) AS retention_days
FROM tg_visit_states
GROUP BY tenant_id, visit_id;

-- Materialized views only see future inserts; backfill the state tables once.
INSERT INTO tg_session_states
SELECT
    tenant_id, session_id, minState(timestamp), maxState(timestamp), countState(),
    uniqStateIf(visit_id, visit_id != ''), argMaxState(visit_id, timestamp), argMaxState(site_key, timestamp),
    argMaxState(retention_days, timestamp)
FROM tg_events
WHERE session_id != ''
GROUP BY tenant_id, session_id;

INSERT INTO tg_visit_states
SELECT
    tenant_id, visit_id, argMaxState(session_id, timestamp), argMaxState(site_key, timestamp),
    minState(timestamp), maxState(timestamp), countState(), countStateIf(kind = 'beacon'),
    countStateIf(kind = 'verdict'), argMaxStateIf(landing_url, timestamp, landing_url != ''),
    argMaxStateIf(landing_path, timestamp, landing_path != ''),
    argMaxStateIf(attribution_channel, timestamp, attribution_channel != ''),
    argMaxStateIf(utm_source, timestamp, utm_source != ''),
    argMaxStateIf(utm_medium, timestamp, utm_medium != ''),
    argMaxStateIf(utm_campaign, timestamp, utm_campaign != ''),
    argMaxStateIf(utm_content, timestamp, utm_content != ''),
    argMaxStateIf(utm_term, timestamp, utm_term != ''),
    argMaxStateIf(assumeNotNull(score), timestamp, isNotNull(score)),
    argMaxStateIf(assumeNotNull(band), timestamp, isNotNull(band)),
    argMaxStateIf(assumeNotNull(action), timestamp, isNotNull(action)),
    argMaxStateIf(rule_hits, timestamp, kind = 'verdict'), maxStateIf(timestamp, kind = 'verdict'),
    argMaxState(retention_days, timestamp)
FROM tg_events
WHERE visit_id != ''
GROUP BY tenant_id, visit_id;
