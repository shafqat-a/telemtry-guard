-- D25 (owner decision): capture the WHOLE request, so a visitor can be followed across
-- pages by whatever identifier their cookies carry.
--
-- `cookies` holds every cookie sent with the request, including session and auth cookies
-- (on the WordPress deployments, wordpress_logged_in_* for every logged-in editor), and
-- `landing_url` keeps the query string that 0003 deliberately dropped. Read access to
-- tg_events is therefore equivalent to holding those credentials — grant it accordingly,
-- and remember the per-row TTL (retention_days, D20) is what eventually expires them.
ALTER TABLE tg_events ADD COLUMN IF NOT EXISTS landing_url String DEFAULT '';
ALTER TABLE tg_events ADD COLUMN IF NOT EXISTS cookies     Map(String, String);
