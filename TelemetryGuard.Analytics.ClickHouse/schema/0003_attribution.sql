-- ANA-08: ad attribution. Answers "did this visit come from a Google/Meta/TikTok ad,
-- and which ad?" without a join — the raw event carries its own provenance.
--
-- Appended as ALTERs (same pattern as 0002): ClickHouseEventSink.ColumnNames is an
-- explicit name/order whitelist, so new columns at the end need no reshuffle.
--
-- NOT stored, deliberately: the raw Cookie header and arbitrary query-string values.
-- The Cookie header carries session and auth cookies (on a WordPress site, every
-- logged-in editor's), and a GET form can put an email address in a query string.
-- Only the four platform attribution cookies and the marketing parameters are kept by
-- value; everything else is kept by NAME, exactly as header_names already does.

-- Google Ads sends gbraid/wbraid INSTEAD of gclid on iOS and consent-limited traffic.
-- Without these two, a growing share of genuinely paid Google clicks reads as organic.
ALTER TABLE tg_events ADD COLUMN IF NOT EXISTS gbraid              String DEFAULT '';
ALTER TABLE tg_events ADD COLUMN IF NOT EXISTS wbraid              String DEFAULT '';

-- UTM tagging as received. utm_id is the campaign id under GA4 manual tagging;
-- utm_content / utm_term usually carry the ad creative or keyword id.
ALTER TABLE tg_events ADD COLUMN IF NOT EXISTS utm_source          LowCardinality(String) DEFAULT '';
ALTER TABLE tg_events ADD COLUMN IF NOT EXISTS utm_medium          LowCardinality(String) DEFAULT '';
ALTER TABLE tg_events ADD COLUMN IF NOT EXISTS utm_campaign        String DEFAULT '';
ALTER TABLE tg_events ADD COLUMN IF NOT EXISTS utm_term            String DEFAULT '';
ALTER TABLE tg_events ADD COLUMN IF NOT EXISTS utm_content         String DEFAULT '';
ALTER TABLE tg_events ADD COLUMN IF NOT EXISTS utm_id              String DEFAULT '';

-- Platform first-party attribution cookies (allowlist).
ALTER TABLE tg_events ADD COLUMN IF NOT EXISTS cookie_fbc          String DEFAULT '';
ALTER TABLE tg_events ADD COLUMN IF NOT EXISTS cookie_fbp          String DEFAULT '';
ALTER TABLE tg_events ADD COLUMN IF NOT EXISTS cookie_gcl_aw       String DEFAULT '';
ALTER TABLE tg_events ADD COLUMN IF NOT EXISTS cookie_ttp          String DEFAULT '';

-- Derived channel, stored so dashboards and rollups do not each re-implement the
-- precedence rules: click id > paid utm_medium > click-id cookie > referrer.
-- 'google_ads'|'meta_ads'|'tiktok_ads'|'microsoft_ads'|'paid_other'|
-- 'organic_search'|'referral'|'direct'
ALTER TABLE tg_events ADD COLUMN IF NOT EXISTS attribution_channel LowCardinality(String) DEFAULT '';

-- Landing page context: path by value, query parameters by name only.
ALTER TABLE tg_events ADD COLUMN IF NOT EXISTS landing_path        String DEFAULT '';
ALTER TABLE tg_events ADD COLUMN IF NOT EXISTS landing_query_keys  Array(String);

-- Allowlisted request headers, values included. Cookie/Authorization are never here.
ALTER TABLE tg_events ADD COLUMN IF NOT EXISTS headers             Map(String, String);

-- Channel-first reporting: "how much of last week's traffic was Meta ads, and how did
-- it score?" is a partition-pruned scan on this projection instead of a full sort.
ALTER TABLE tg_events ADD INDEX IF NOT EXISTS idx_attribution_channel attribution_channel
    TYPE set(16) GRANULARITY 4;
