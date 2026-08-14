-- RSK-08: training weight column + listen-only shadow columns + serialized feature vector.
-- tg_labels itself is OWNED BY ANA-02 (schema/0001_events.sql): tenant_id UUID,
-- session_id String, label 'fraud'|'legit', label_source LowCardinality(String),
-- created_at DateTime64(3,'UTC'), plain MergeTree, fixed 400-day TTL.
-- Do NOT re-create or re-shape it here — ALTER only. Duplicate/conflicting label rows
-- are resolved at training-read time (step 4 of RSK-08), not by the storage engine.
-- NOTE for API-06 owner: populate `features` (JSON of FraudFeatureVector, via
-- TelemetryGuard.RiskEngine.Contracts.FraudFeatureVectorJson) and, when a listen-only
-- (D18) shadow scorer ran, shadow_score/shadow_scorer_version on verdict finalization
-- (TelemetryGuard.Api.Services.VerdictFinalizer step 5).
ALTER TABLE tg_labels ADD COLUMN IF NOT EXISTS weight Float32 DEFAULT 1;

ALTER TABLE tg_events ADD COLUMN IF NOT EXISTS features String DEFAULT '';
-- Nullable(Int16), matching the existing `score` column's type exactly (not
-- Nullable(UInt8) — a model probability rounded to 0..100 still fits Int16, and this
-- keeps the two score columns symmetric for any query that compares them).
ALTER TABLE tg_events ADD COLUMN IF NOT EXISTS shadow_score Nullable(Int16);
ALTER TABLE tg_events ADD COLUMN IF NOT EXISTS shadow_scorer_version LowCardinality(String) DEFAULT '';
