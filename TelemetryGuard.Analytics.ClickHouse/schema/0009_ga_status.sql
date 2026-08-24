-- GA runtime delivery status observed by the browser SDK for this page visit.
ALTER TABLE tg_events ADD COLUMN IF NOT EXISTS ga_status LowCardinality(String) DEFAULT 'unknown';
ALTER TABLE tg_events ADD INDEX IF NOT EXISTS idx_ga_status ga_status TYPE set(8) GRANULARITY 4;
