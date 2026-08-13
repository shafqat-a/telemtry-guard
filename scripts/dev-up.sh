#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

if [ ! -f .env ]; then
  cp .env.example .env
  echo "Created .env from .env.example — review credentials before exposing this machine."
fi

docker compose up -d --wait
echo
docker compose ps
echo
echo "SQL Server : localhost,1433  (sa / \$MSSQL_SA_PASSWORD)"
echo "Redis      : localhost:6379"
echo "ClickHouse : http://localhost:8123 (native 9000, db: telemetry_guard)"
echo "Grafana    : http://localhost:3000"
