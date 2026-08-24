#!/usr/bin/env node

/**
 * GA4 query script for Codex CLI / Node.js
 *
 * Auth method:
 *   - Service account JSON via GOOGLE_APPLICATION_CREDENTIALS
 *   - The service account must be added to the GA4 property with at least Viewer/Analyst access
 *
 * Install:
 *   npm install @google-analytics/data
 *
 * Run:
 *   export GOOGLE_APPLICATION_CREDENTIALS=/absolute/path/to/service-account.json
 *   export GA4_PROPERTY_ID=540507795
 *   node ga4-codex-query.js
 */

const { BetaAnalyticsDataClient } = require('@google-analytics/data');

const propertyId = process.env.GA4_PROPERTY_ID || '540507795';

async function main() {
  const client = new BetaAnalyticsDataClient();

  // Example report: last 7 days landing pages with sessions, views, bounce rate
  const [response] = await client.runReport({
    property: `properties/${propertyId}`,
    dateRanges: [{ startDate: '7daysAgo', endDate: 'yesterday' }],
    dimensions: [{ name: 'landingPagePlusQueryString' }],
    metrics: [
      { name: 'sessions' },
      { name: 'screenPageViews' },
      { name: 'bounceRate' },
    ],
    orderBys: [{ metric: { metricName: 'sessions' }, desc: true }],
    limit: 20,
  });

  const rows = (response.rows || []).map((row) => ({
    landingPage: row.dimensionValues?.[0]?.value,
    sessions: row.metricValues?.[0]?.value,
    views: row.metricValues?.[1]?.value,
    bounceRate: row.metricValues?.[2]?.value,
  }));

  console.log(JSON.stringify({ propertyId, rows }, null, 2));
}

main().catch((err) => {
  console.error('GA4 query failed');
  console.error(err.message || err);
  process.exit(1);
});
