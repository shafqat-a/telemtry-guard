-- Browser-reported document.referrer. This is distinct from the HTTP Referer on
-- the telemetry request, which points at the tagged site when the API is cross-origin.
ALTER TABLE tg_events ADD COLUMN IF NOT EXISTS document_referrer Nullable(String);
