-- tg_events: raw capture + verdict rows, one shared table for all tenants (D11/D20)
CREATE TABLE IF NOT EXISTS tg_events
(
    -- identity
    tenant_id                  UUID,
    site_key                   LowCardinality(String),
    session_id                 String,
    kind                       LowCardinality(String),   -- 'tracker'|'pixel'|'beacon'|'verdict'
    -- click ids (one column per supported ad platform — API-02 extracts all four)
    campaign_id                String DEFAULT '',
    gclid                      String DEFAULT '',
    fbclid                     String DEFAULT '',
    msclkid                    String DEFAULT '',
    ttclid                     String DEFAULT '',
    click_id_invalid           Nullable(UInt8),
    -- HTTP layer
    ip                         IPv6,
    header_names               Array(String),            -- ordered as received
    user_agent                 Nullable(String),
    sec_ch_ua                  Nullable(String),
    sec_ch_ua_mobile           Nullable(String),
    sec_ch_ua_platform         Nullable(String),
    accept_language            Nullable(String),
    referrer                   Nullable(String),
    tls_ja3                    Nullable(String),
    tls_ja4                    Nullable(String),
    cf_asn                     Nullable(UInt32),
    -- enrichment snapshot
    country                    LowCardinality(Nullable(String)),
    asn                        Nullable(UInt32),
    asn_org                    Nullable(String),
    asn_type                   LowCardinality(Nullable(String)),
    is_datacenter              Nullable(UInt8),
    is_proxy                   Nullable(UInt8),
    is_vpn                     Nullable(UInt8),
    is_tor                     Nullable(UInt8),
    is_private_relay           Nullable(UInt8),
    -- SDK summary (Float32 NaN = absent; Nullable(UInt8) NULL = absent)
    has_js_beacon              UInt8,
    beacon_integrity_ok        Nullable(UInt8),
    fingerprint_visitor_id     Nullable(String),
    storage_age_sec            Float32,
    webdriver_flag             Nullable(UInt8),
    headless_browser           Nullable(UInt8),
    screen_width               Float32,
    screen_height              Float32,
    timezone                   Nullable(String),
    language                   Nullable(String),
    mouse_event_count          Float32,
    key_event_count            Float32,
    touch_event_count          Float32,
    scroll_event_count         Float32,
    mean_inter_event_ms        Float32,
    std_inter_event_ms         Float32,
    mouse_path_linearity       Float32,
    first_interaction_delay_ms Float32,
    form_fill_time_sec         Float32,
    autofill_detected          Nullable(UInt8),
    paste_in_identity_fields   Nullable(UInt8),
    honeypot_touched           Nullable(UInt8),
    pointer_untrusted          Nullable(UInt8),
    input_modality_mismatch    Nullable(UInt8),
    time_on_page_sec           Float32,
    pages_viewed               Float32,
    -- velocity snapshot (0 = cold, never NULL/NaN)
    ip_clicks_last_min         Int32,
    ip_distinct_uas_last_hour  Int32,
    device_sessions_last_hour  Int32,
    device_ids_this_ip_hour    Int32,
    -- verdict block
    score                      Nullable(Int16),
    band                       LowCardinality(Nullable(String)),  -- 'allow'|'challenge'|'block'
    action                     LowCardinality(Nullable(String)),
    rule_hits                  Array(String),
    scorer_version             LowCardinality(Nullable(String)),
    feature_set_version        Nullable(UInt16),
    -- storage control
    retention_days             UInt16,                   -- denormalized at ingest (D20)
    timestamp                  DateTime64(3, 'UTC')
)
ENGINE = MergeTree
PARTITION BY toYYYYMM(timestamp)
ORDER BY (tenant_id, timestamp)
TTL toDateTime(timestamp) + toIntervalDay(retention_days) DELETE;

-- tg_labels: training labels (D18/D19), fixed 400-day TTL
CREATE TABLE IF NOT EXISTS tg_labels
(
    tenant_id    UUID,
    session_id   String,
    label        LowCardinality(String),   -- 'fraud'|'legit'
    label_source LowCardinality(String),   -- 't1_rule'|'synthetic_bot'|'conversion'|'review_screen'
    created_at   DateTime64(3, 'UTC')
)
ENGINE = MergeTree
PARTITION BY toYYYYMM(created_at)
ORDER BY (tenant_id, session_id, created_at)
TTL toDateTime(created_at) + toIntervalDay(400) DELETE;
