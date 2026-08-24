#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

# Requires the FND-02 compose stack: run ./scripts/dev-up.sh first.
if [ ! -f .env ]; then
  echo "ERROR: .env not found — run ./scripts/dev-up.sh first (FND-02)." >&2
  exit 1
fi
set -a; source .env; set +a

export MIGRATIONS_CONNECTIONSTRING="Server=localhost,1433;Database=TelemetryGuard;User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=True"

echo "== Applying SQL Server migrations (DAT-01) =="
dotnet run --project TelemetryGuard.MigrationRunner

# 0013: the API (ConnectionStrings:Main) and background jobs (ConnectionStrings:System)
# run as least-privilege users, never as sa. Defaults match appsettings.json's dev
# connection strings; override both in .env for anything that is not a laptop.
export TG_APP_DB_PASSWORD="${TG_APP_DB_PASSWORD:-TgApp!Dev0Passw0rd}"
export TG_SYSTEM_DB_PASSWORD="${TG_SYSTEM_DB_PASSWORD:-TgSys!Dev0Passw0rd}"
echo "== Creating least-privilege database users tg_app / tg_system (0013, idempotent) =="
dotnet run --project TelemetryGuard.MigrationRunner -- provision create-db-user \
  --name tg_app --role tg_app --password-env TG_APP_DB_PASSWORD
dotnet run --project TelemetryGuard.MigrationRunner -- provision create-db-user \
  --name tg_system --role tg_system --password-env TG_SYSTEM_DB_PASSWORD

echo "== Applying ClickHouse schema (no-op until ANA-02 lands) =="
CLICKHOUSE_CONNECTIONSTRING="Host=localhost;Port=8123;Database=${CLICKHOUSE_DB:-telemetry_guard};Username=${CLICKHOUSE_USER:-tg};Password=${CLICKHOUSE_PASSWORD:-tg-dev-password}" \
  dotnet run --project TelemetryGuard.MigrationRunner -- --clickhouse

echo "== Provisioning well-known dev tenant (idempotent) =="
dotnet run --project TelemetryGuard.MigrationRunner -- provision create-tenant \
  --tenant-id 33333333-3333-3333-3333-333333333333 \
  --name "Dev Tenant" --retention-days 90 --enforcement-mode AutoEnforce

dotnet run --project TelemetryGuard.MigrationRunner -- provision issue-api-key \
  --tenant-id 33333333-3333-3333-3333-333333333333 \
  --scopes "admin ingest report" \
  --key tg_ak_dev0000000000000000000000000000000000000000

dotnet run --project TelemetryGuard.MigrationRunner -- provision register-site \
  --tenant-id 33333333-3333-3333-3333-333333333333 \
  --domain localhost --integration-mode js \
  --site-key tg_sk_dev0000000000000000000

dotnet run --project TelemetryGuard.MigrationRunner -- provision create-campaign \
  --tenant-id 33333333-3333-3333-3333-333333333333 \
  --campaign-id 44444444-4444-4444-4444-444444444444 \
  --platform google --external-id dev-campaign-001 \
  --landing-url http://localhost:4650/landing.html \
  --geo-targets '["US"]'

cat <<'EOF'

== Dev seed complete — well-known values ==
SQL users: tg_app (ConnectionStrings:Main) / tg_system (ConnectionStrings:System) — sa is for migrations only
TenantId : 33333333-3333-3333-3333-333333333333
API key  : tg_ak_dev0000000000000000000000000000000000000000  (scopes: admin ingest report)
Site key : tg_sk_dev0000000000000000000  (localhost, js mode)
Campaign : 44444444-4444-4444-4444-444444444444  (google, dev-campaign-001)

Try it (API dev port assumed 8080, per SDK-06; substitute what `dotnet run --project TelemetryGuard.Api` reports):
  snippet : <script async src="http://localhost:8080/sdk/tg.js" data-site-key="tg_sk_dev0000000000000000000"></script>   (SDK-08 route)
  pixel   : <img src="http://localhost:8080/p.gif?k=tg_sk_dev0000000000000000000" alt="">                               (API-03)
  tracker : http://localhost:8080/c?k=tg_sk_dev0000000000000000000&cid=44444444-4444-4444-4444-444444444444&gclid=test1 (API-02)
  admin   : curl -H "X-Api-Key: tg_ak_dev0000000000000000000000000000000000000000" http://localhost:8080/admin/whitelist (API-07)
  bots    : cd TelemetryGuard.Sdk && npm run bot-traffic -- --target http://localhost:8080 --site-key tg_sk_dev0000000000000000000  (SDK-06)
EOF
