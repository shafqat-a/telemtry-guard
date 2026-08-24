-- Reconstruct page visits for pre-0006 events. A boundary is conservative:
-- the first event in a session, a non-empty landing-path change, or >30 minutes
-- of inactivity. Deterministic ids make the migration reproducible/auditable.

CREATE VIEW IF NOT EXISTS tg_legacy_event_visits AS
WITH enriched AS
(
    SELECT *,
        anyLast(nullIf(landing_path, '')) OVER
        (
            PARTITION BY tenant_id, session_id
            ORDER BY timestamp, kind, landing_path, landing_url, ifNull(score, -1)
            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
        ) AS effective_path
    FROM tg_events
    WHERE visit_id = '' AND session_id != ''
), ordered AS
(
    SELECT *,
        lagInFrame(timestamp, 1, timestamp) OVER
        (
            PARTITION BY tenant_id, session_id
            ORDER BY timestamp, kind, landing_path, landing_url, ifNull(score, -1)
            ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING
        ) AS previous_timestamp,
        lagInFrame(effective_path, 1, NULL) OVER
        (
            PARTITION BY tenant_id, session_id
            ORDER BY timestamp, kind, landing_path, landing_url, ifNull(score, -1)
            ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING
        ) AS previous_path,
        row_number() OVER
        (
            PARTITION BY tenant_id, session_id
            ORDER BY timestamp, kind, landing_path, landing_url, ifNull(score, -1)
        ) AS event_ordinal
    FROM enriched
), marked AS
(
    SELECT *,
        if(event_ordinal = 1
           OR dateDiff('second', previous_timestamp, timestamp) > 1800
           OR (isNotNull(effective_path) AND isNotNull(previous_path)
               AND effective_path != previous_path), 1, 0) AS starts_visit
    FROM ordered
), segmented AS
(
    SELECT *,
        sum(starts_visit) OVER
        (
            PARTITION BY tenant_id, session_id
            ORDER BY timestamp, kind, landing_path, landing_url, ifNull(score, -1)
            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
        ) AS legacy_visit_number
    FROM marked
)
SELECT *, concat('legacy-', lower(hex(MD5(concat(
    toString(tenant_id), '|', session_id, '|', toString(legacy_visit_number)
))))) AS inferred_visit_id
FROM segmented;

CREATE TABLE IF NOT EXISTS tg_legacy_visit_map
(
    tenant_id UUID,
    session_id String,
    visit_id String,
    visit_number UInt64,
    first_seen DateTime64(3, 'UTC'),
    last_seen DateTime64(3, 'UTC'),
    landing_path String,
    event_count UInt64,
    inference_method LowCardinality(String) DEFAULT 'path-change-or-30m-gap'
)
ENGINE = MergeTree
ORDER BY (tenant_id, session_id, visit_number);

INSERT INTO tg_legacy_visit_map
SELECT
    tenant_id,
    session_id,
    inferred_visit_id,
    legacy_visit_number,
    min(timestamp),
    max(timestamp),
    ifNull(argMinIf(effective_path, timestamp, isNotNull(effective_path)), ''),
    count(),
    'path-change-or-30m-gap'
FROM tg_legacy_event_visits
GROUP BY tenant_id, session_id, inferred_visit_id, legacy_visit_number;

-- Add inferred visit membership to the already-backfilled canonical sessions.
-- countStateIf(..., 0) contributes zero, avoiding double-counting legacy events.
INSERT INTO tg_session_states
SELECT
    tenant_id,
    session_id,
    minState(timestamp),
    maxState(timestamp),
    countStateIf(0),
    uniqState(inferred_visit_id),
    argMaxState(inferred_visit_id, timestamp),
    argMaxState(site_key, timestamp),
    argMaxState(retention_days, timestamp)
FROM tg_legacy_event_visits
GROUP BY tenant_id, session_id;

INSERT INTO tg_visit_states
SELECT
    tenant_id,
    inferred_visit_id,
    argMaxState(session_id, timestamp),
    argMaxState(site_key, timestamp),
    minState(timestamp),
    maxState(timestamp),
    countState(),
    countStateIf(kind = 'beacon'),
    countStateIf(kind = 'verdict'),
    argMaxStateIf(landing_url, timestamp, landing_url != ''),
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
    argMaxStateIf(rule_hits, timestamp, kind = 'verdict'),
    maxStateIf(timestamp, kind = 'verdict'),
    argMaxState(retention_days, timestamp)
FROM tg_legacy_event_visits
GROUP BY tenant_id, inferred_visit_id;
