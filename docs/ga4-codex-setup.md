# GA4 from Codex CLI

## Important
GA4 private property data does **not** use an API key alone.

Use one of these:
1. **Service account** (best for Codex CLI)
2. OAuth user login

## Best option: Service account

### 1) Enable API
In Google Cloud, enable:
- **Google Analytics Data API**

### 2) Create service account
- Create a service account
- Download the JSON key file

### 3) Add service account to GA4
In GA4 Admin for property **540507795**:
- Add the service account email as a user
- Give it at least **Viewer** or **Analyst** access

### 4) Install package
```bash
npm install @google-analytics/data
```

### 5) Set env vars
```bash
export GOOGLE_APPLICATION_CREDENTIALS=/absolute/path/to/service-account.json
export GA4_PROPERTY_ID=540507795
```

### 6) Run
```bash
node ga4-codex-query.js
```

## Notes
- Do **not** use browser session tokens from the logged-in Analytics tab.
- If you want, I can also make:
  - a script for a specific report
  - a CSV export script
  - an OAuth version instead of service account
