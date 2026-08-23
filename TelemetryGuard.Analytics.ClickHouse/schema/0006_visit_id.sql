-- One page/document load. Empty for legacy SDK rows during rolling upgrades.
ALTER TABLE tg_events ADD COLUMN IF NOT EXISTS visit_id String DEFAULT '';
ALTER TABLE tg_events ADD INDEX IF NOT EXISTS idx_visit_id visit_id TYPE bloom_filter GRANULARITY 4;
