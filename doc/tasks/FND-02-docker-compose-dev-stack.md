---
id: FND-02
title: Docker Compose dev stack
phase: 0
workstream: foundation
depends_on: []
size: M
spec_refs: [D5, D6, D8, D16, D17, D20]
detail_level: full
---

# FND-02: Docker Compose dev stack

## Objective
Create the local development stack in a single `docker-compose.yml` at the repo root — SQL Server 2022, Redis 7, ClickHouse 24.8, Grafana OSS — with healthchecks, named volumes, one bridge network, memory limits sized for the 8 GB VPS deployment target, an `.env.example`, and `scripts/dev-up.sh` / `scripts/dev-down.sh`. Grafana's provisioning directories are mounted but left empty (task OPS-01 fills them).

## Spec context (self-contained)
- **Local/MVP hosting is Docker Compose** (spec D16): ASP.NET service (runs on the host during dev, in a container later), Redis, ClickHouse, SQL Server, Grafana — all sized to run together on **one 8 GB VPS** at early volume. Memory limits below implement that budget.
- **SQL Server** (D8): `mcr.microsoft.com/mssql/server` container locally, Azure SQL in prod, same T-SQL surface. Owns tenants, config, verdict summaries, exclusion-sync state.
- **Redis** (D5): plain Redis in Docker; the Redis *protocol* is the seam — no provider abstraction. Workload: sliding-window counters, HyperLogLogs, dedupe, challenge tokens, per-tenant quotas. Keys are prefixed `t:{tenantId}:…` by the application.
- **ClickHouse** (D6): single-node is sufficient early; the entire dev loop must run locally. Raw click/event rows live here with per-row TTL (D20). The database name contract for all later tasks (ANA-02 DDL, OPS-01 dashboards) is **`telemetry_guard`**.
- **Grafana** (D17): Grafana-on-ClickHouse is the MVP dashboarding answer. This task only mounts the provisioning/dashboards directories; OPS-01 adds datasource + dashboard provisioning and the ClickHouse plugin env var.
- Ports (host-mapped, fixed): SQL Server **1433**, Redis **6379**, ClickHouse **8123** (HTTP) + **9000** (native), Grafana **3000**.

## Prerequisites
None (phase 0, no dependencies). Docker Engine with the `docker compose` v2 plugin on the dev machine. FND-01 may or may not have run yet; this task must not assume the .NET solution exists.

## Implementation steps

1. **Create `.env.example` at the repo root** (developers copy it to `.env`; `.env` must be git-ignored — append a `.env` line to `.gitignore`, creating that file if it does not exist yet):

```dotenv
# SQL Server (sa password must meet complexity: 8+ chars, upper, lower, digit, symbol)
MSSQL_SA_PASSWORD=TgDev!Str0ngPassw0rd

# ClickHouse
CLICKHOUSE_DB=telemetry_guard
CLICKHOUSE_USER=tg
CLICKHOUSE_PASSWORD=tg-dev-password

# Grafana
GRAFANA_ADMIN_USER=admin
GRAFANA_ADMIN_PASSWORD=admin
```

2. **Create `docker-compose.yml` at the repo root** with exactly this content:

```yaml
name: telemetry-guard

services:
  mssql:
    image: mcr.microsoft.com/mssql/server:2022-latest
    container_name: tg-mssql
    environment:
      ACCEPT_EULA: "Y"
      MSSQL_SA_PASSWORD: ${MSSQL_SA_PASSWORD}
      MSSQL_PID: Developer
      MSSQL_MEMORY_LIMIT_MB: "1536"
    ports:
      - "1433:1433"
    volumes:
      - mssql-data:/var/opt/mssql
    networks: [tg]
    mem_limit: 2g
    healthcheck:
      test: ["CMD-SHELL", "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"$${MSSQL_SA_PASSWORD}\" -C -b -Q 'SELECT 1' || exit 1"]
      interval: 10s
      timeout: 5s
      retries: 10
      start_period: 40s

  redis:
    image: redis:7-alpine
    container_name: tg-redis
    command: ["redis-server", "--maxmemory", "200mb", "--maxmemory-policy", "volatile-lru", "--appendonly", "no"]
    ports:
      - "6379:6379"
    volumes:
      - redis-data:/data
    networks: [tg]
    mem_limit: 256m
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]
      interval: 10s
      timeout: 3s
      retries: 5
      start_period: 5s

  clickhouse:
    image: clickhouse/clickhouse-server:24.8
    container_name: tg-clickhouse
    environment:
      CLICKHOUSE_DB: ${CLICKHOUSE_DB}
      CLICKHOUSE_USER: ${CLICKHOUSE_USER}
      CLICKHOUSE_PASSWORD: ${CLICKHOUSE_PASSWORD}
      CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT: "1"
    ports:
      - "8123:8123"
      - "9000:9000"
    volumes:
      - clickhouse-data:/var/lib/clickhouse
    networks: [tg]
    mem_limit: 2g
    ulimits:
      nofile:
        soft: 262144
        hard: 262144
    healthcheck:
      test: ["CMD-SHELL", "wget --no-verbose --tries=1 --spider http://localhost:8123/ping || exit 1"]
      interval: 10s
      timeout: 5s
      retries: 10
      start_period: 20s

  grafana:
    image: grafana/grafana-oss:11.2.0
    container_name: tg-grafana
    environment:
      GF_SECURITY_ADMIN_USER: ${GRAFANA_ADMIN_USER}
      GF_SECURITY_ADMIN_PASSWORD: ${GRAFANA_ADMIN_PASSWORD}
      GF_USERS_ALLOW_SIGN_UP: "false"
    ports:
      - "3000:3000"
    volumes:
      - grafana-data:/var/lib/grafana
      - ./ops/grafana/provisioning:/etc/grafana/provisioning
      - ./ops/grafana/dashboards:/var/lib/grafana/dashboards
    networks: [tg]
    mem_limit: 512m
    depends_on:
      clickhouse:
        condition: service_healthy
    healthcheck:
      test: ["CMD-SHELL", "wget --no-verbose --tries=1 --spider http://localhost:3000/api/health || exit 1"]
      interval: 10s
      timeout: 5s
      retries: 10
      start_period: 20s

networks:
  tg:
    driver: bridge

volumes:
  mssql-data:
  redis-data:
  clickhouse-data:
  grafana-data:
```

Notes baked into that file (do not change them silently):
- **Memory budget for the 8 GB VPS target (D16):** mssql 2 g (engine capped at 1.5 g via `MSSQL_MEMORY_LIMIT_MB`), clickhouse 2 g (ClickHouse reads the cgroup limit and sizes itself), redis 256 m (engine capped at 200 mb), grafana 512 m. Total ≈ 4.75 g, leaving ~3 g for the ASP.NET service + OS.
- `$$` in the mssql healthcheck is Compose escaping for a literal `$` (the variable is expanded inside the container, not by Compose).
- Redis eviction policy is `volatile-lru`: only keys with TTLs are evicted — velocity counters and dedupe keys all carry TTLs; never use `allkeys-lru`, which could silently drop dedupe state.
- SQL Server 2022 images ship `sqlcmd` at `/opt/mssql-tools18/bin/sqlcmd`; the `-C` flag trusts the self-signed cert.
- The two Grafana bind mounts point at repo directories that OPS-01 will populate; they are created empty in step 3.
- OPS-01 will later add `GF_INSTALL_PLUGINS` and ClickHouse credential pass-through to the `grafana` service — leave room for that, don't add it here.

3. **Create the Grafana mount directories** (empty except `.gitkeep`):
   - `ops/grafana/provisioning/datasources/.gitkeep`
   - `ops/grafana/provisioning/dashboards/.gitkeep`
   - `ops/grafana/dashboards/.gitkeep`

4. **Create `scripts/dev-up.sh`** (mark executable, `chmod +x`):

```bash
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
```

5. **Create `scripts/dev-down.sh`** (mark executable):

```bash
#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

# Pass -v to also delete named volumes (destroys all local data).
if [ "${1:-}" = "-v" ]; then
  docker compose down -v
else
  docker compose down
fi
```

6. **Verify** by running `./scripts/dev-up.sh` and the acceptance-criteria commands below, then `./scripts/dev-down.sh`.

## Files to create or modify
- `/home/shafqat/git/shafqat/telemtry-guard/docker-compose.yml`
- `/home/shafqat/git/shafqat/telemtry-guard/.env.example`
- `/home/shafqat/git/shafqat/telemtry-guard/.gitignore` (append `.env` if not already ignored; create the file if FND-01 has not run yet)
- `/home/shafqat/git/shafqat/telemtry-guard/scripts/dev-up.sh` (executable)
- `/home/shafqat/git/shafqat/telemtry-guard/scripts/dev-down.sh` (executable)
- `/home/shafqat/git/shafqat/telemtry-guard/ops/grafana/provisioning/datasources/.gitkeep`
- `/home/shafqat/git/shafqat/telemtry-guard/ops/grafana/provisioning/dashboards/.gitkeep`
- `/home/shafqat/git/shafqat/telemtry-guard/ops/grafana/dashboards/.gitkeep`

## Acceptance criteria
- `./scripts/dev-up.sh` completes with all four services reported healthy (`docker compose ps` shows `healthy` for mssql, redis, clickhouse, grafana).
- `docker exec tg-mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -Q "SELECT @@VERSION"` prints a SQL Server 2022 version string.
- `docker exec tg-redis redis-cli ping` prints `PONG`.
- `curl -s "http://localhost:8123/ping"` returns `Ok.`, and `curl -s "http://localhost:8123/?user=tg&password=tg-dev-password&query=SHOW+DATABASES"` lists `telemetry_guard`.
- `curl -s http://localhost:3000/api/health` returns JSON with `"database": "ok"`.
- All four services declare `mem_limit`; sum of limits ≤ 5 g.
- `./scripts/dev-down.sh` stops everything; `./scripts/dev-up.sh` again restores a healthy stack with data volumes intact.
- `.env` is git-ignored; only `.env.example` is committed.

## Testing
Manual smoke test via the acceptance-criteria commands (this is infrastructure; no xUnit tests). Additionally verify config validity without starting anything: `docker compose config -q` exits 0 with a populated `.env`.

## Out of scope / guardrails
- **Do not add the ASP.NET Api as a compose service** — during Phase 0/1 dev it runs on the host via `dotnet run`; containerizing the app is a deployment concern (P2-04).
- **No Kafka/Redpanda/Event Hubs containers** — no broker at MVP (D12).
- **No PostgreSQL** — evaluated and dropped (D23); SQL Server is final for the relational tier.
- **Do not write Grafana datasources or dashboards** — OPS-01 owns everything under `ops/grafana/` except the `.gitkeep` placeholders, and owns `GF_INSTALL_PLUGINS`.
- **Do not create ClickHouse tables or SQL Server schemas** — ANA-02 and DAT-02 own DDL. This task only guarantees the `telemetry_guard` ClickHouse database exists (via `CLICKHOUSE_DB`).
- Keep the single bridge network and fixed ports exactly as specified — later task files reference `localhost:1433/6379/8123/9000/3000` and in-network hostnames `mssql`, `redis`, `clickhouse`, `grafana`.
- Do not raise memory limits "to be safe" — the 8 GB VPS budget (D16) is a hard sizing constraint.
