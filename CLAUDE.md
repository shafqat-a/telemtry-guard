# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project status

**Phases 0 and 1 (MVP) are implemented and green**: all 44 phase-0/1 tasks from `doc/plan.md` are done — full solution build (0 warnings), 478 unit tests, 123 integration + 8 contract tests passing, SDK bundle under its 30 KB gzip gate. Remaining work: Phase 1.5 (INT-02..05 exclusion sync + Cloudflare signals, RSK-08 first LightGBM model), Phase 2 outlines (P2-01..05), and the backlog in `doc/plan.md`.

**Start here for implementation work:** `doc/plan.md` — 54 tasks across phases 0/1/1.5/2/later with a dependency graph. Each task file in `doc/tasks/` is self-contained (exact paths, signatures, SQL, package names, acceptance criteria): to implement a task, read only that file plus the task files of its `depends_on`. Do not begin a task whose dependencies are incomplete. Contract-bearing tasks (FND-04, ANA-01, RSK-01) are normative for type names.

## Build, test, and run commands

Prereqs: .NET 8 SDK, Node 20+, a Docker-compatible container runtime (podman works; set `DOCKER_HOST` to its socket and `TESTCONTAINERS_RYUK_DISABLED=true`).

```bash
dotnet build TelemetryGuard.sln                          # full solution
dotnet test tests/TelemetryGuard.Tests.Unit              # pure unit tests (no containers)
dotnet test tests/TelemetryGuard.Tests.Integration       # Testcontainers: SQL Server + ClickHouse + Redis
dotnet test tests/TelemetryGuard.Tests.Contracts         # analytics contract suite (per provider)

cd TelemetryGuard.Sdk
npm ci && npm run build        # esbuild → dist/tg.js + dist/tg-<version>.js
npm run size                   # 30 KB gzip gate (D2)
npm run typecheck              # tsc --noEmit
npm test                       # Playwright e2e (fixture pages; needs `npx playwright install chromium`)

./scripts/update-iplegence.sh  # fetch Superior-IP.mmdb into ./data/geo (D24; needs `gh auth login`,
                               # or set IPLEGENCE_MMDB_URL / IPLEGENCE_DIST_DIR) — optional, the app
                               # starts and degrades to null enrichment without it
./scripts/dev-up.sh            # compose stack: mssql, redis, clickhouse, grafana
./scripts/dev-seed.sh          # migrations + demo tenant/site key/campaign via MigrationRunner `provision`
dotnet run --project TelemetryGuard.Api                  # API on the host (deliberately not a compose service)
./scripts/dev-down.sh          # stop the stack (never do this while agents/tests are using it)
```

Tenant provisioning CLI: `dotnet run --project TelemetryGuard.MigrationRunner -- provision <create-tenant|issue-api-key|register-site|create-campaign> …` (reads `MIGRATIONS_CONNECTIONSTRING`); with no args it runs DbUp migrations.

## What this is

TelemetryGuard is a multi-tenant SaaS that detects and blocks ad fraud, click fraud, and bot traffic for PPC campaigns (Google Ads, Meta, TikTok) and lead-generation forms. It scores every click/visit 0–100 in real time, enforces allow/challenge/block decisions, and pushes confirmed-fraud sources back into ad-platform exclusion lists.

The full decision record — including every technology choice and the rationale/alternatives considered — lives in `doc/spec.md`. Read it before making architectural changes; it is the source of truth, and decisions are numbered (D1–D25) and amended by adding new numbered entries (D26+), never by editing history. A companion `fraud-signal-feature-spec.md` (not yet added) is intended to hold the detailed signal contract and ML.NET input vector.

## Core architectural constraints (do not violate silently)

- **No server-side scripting languages.** Python and Node are explicitly excluded from the backend by owner constraint. Backend is **.NET (C#) end-to-end** (D1). The one unavoidable exception is the browser: the client SDK is TypeScript compiled via esbuild to a static IIFE bundle (D2) — this does not introduce a server-side scripting runtime.
- **Abstract analytics by intent, not by query.** `IEventSink` / `IAnalyticsQueries` are narrow, intent-named interfaces implemented once per engine (ClickHouse now, Kusto later). Never build a generic cross-engine query language or LINQ-over-both — this was explicitly rejected (D7).
- **Never store the raw request.** Ad attribution lives on every event (utm_*, click ids incl. gbraid/wbraid, `attribution_channel`), but the raw `Cookie` header, arbitrary headers and arbitrary query values are excluded by allowlist (D25) — session/auth cookies and PII must never reach the event store. Non-marketing query parameters and headers are recorded by NAME only.
- **IP intelligence is a memory-mapped file, never a service call.** Datasets sit behind `IIpIntelligenceProvider` (D24), selected by `IpEnrichment:Provider` — `Iplegence` (default, one merged `Superior-IP.mmdb`) or `MaxMind` (GeoLite2 + IP2Proxy). iplegence ships an HTTP API and a Docker image: **do not call them from the request path.** Enrichment runs on every click and beacon inside the 50 ms budget, and a network hop would force a fail-open/fail-closed choice on every timeout. iplegence is a build-time data producer fetched by `scripts/update-iplegence.sh`; no Go runs in production (D1).
- **Multi-tenancy correctness must not depend on developer discipline.** Shared database/shared schema with `TenantId` on every row, enforced primarily via SQL Server Row-Level Security (RLS) keyed on `SESSION_CONTEXT('TenantId')`, not just application-level `WHERE` clauses (D11). Repositories must only obtain connections through `TenantConnectionFactory`, which stamps the session context — there is no path to an unscoped connection.
- **Dapper, not EF Core**, for all SQL Server data access (D9) — one consistent pattern (explicit SQL behind narrow, intent-named repositories) instead of ORM-here/raw-SQL-there. Migrations are versioned SQL scripts run by DbUp/Grate (D10), not EF migrations.
- **Real-time scoring budget is < 50 ms.** The risk engine's model inference runs in-process inside the same ASP.NET Core service as ingestion, tracking, and decision (D3) — no network hop for scoring in the hot path.
- **Rules can only raise a score, never lower one.** Deterministic T1 rules set a floor pre-model.
- **Missing signal ≠ zero.** No-beacon (non-JS / pixel-mode) sessions carry `NaN` for SDK-derived features rather than 0; LightGBM branches on NaN natively. `has_js_beacon` is itself a feature, not just a gating flag.

## System components (six parts, two deliberately redundant capture paths)

1. **Client JS SDK** (`TelemetryGuard.Sdk`) — TypeScript/esbuild bundle embedded on tenant pages; collects behavioral timing, FingerprintJS + Botd signals, honeypots, storage age. Ships via `sendBeacon`. Tenants unable to add script tags use **web-pixel mode** instead (HTTP-only signals, D22) — pixel mode must degrade gracefully to `NaN` SDK features rather than failing.
2. **Click/redirect tracker** — server endpoint the ad destination URL points to; logs HTTP-layer signals and 302-redirects. Exists specifically because many bots never execute JavaScript, so this path must work independently of the SDK.
3. **Ingestion API** — receives beacons + tracker hits, extracts HTTP-layer signals (IP, headers, UA/Client Hints, TLS fingerprint via Cloudflare), joins beacon to click data by session ID.
4. **Enrichment + risk engine** (`TelemetryGuard.RiskEngine`) — IP intelligence behind `IIpIntelligenceProvider` (D24: **iplegence's merged `Superior-IP.mmdb` by default**, GeoLite2 + IP2Proxy as the rollback), Redis velocity counters, derived features, scoring via `IScorer` (heuristic at MVP, swappable to ML.NET LightGBM / ONNX Runtime later without a rewrite — D18).
5. **Decision/enforcement layer** — maps score to allow (0–30) / challenge via Cloudflare Turnstile (31–70) / block + exclude (71–100); syncs confirmed-fraud sources to Google Ads and Meta exclusion APIs, honoring the per-tenant `EnforcementMode` (`AutoEnforce` vs `ApprovalQueue`, D21).
6. **Event store + offline side** — every raw event and verdict persisted to ClickHouse; feeds retraining, false-positive review, dashboards. The real-time path never blocks on this.

## Data split (do not mix these up — D8/D23)

- **ClickHouse**: raw click/event rows only, `tenant_id` first in every table's `ORDER BY`, per-tenant TTL via `retention_days` denormalized onto each row at ingest (D20, default proposed 90 days, 30–180 range).
- **SQL Server** (Azure SQL in prod, `mssql` container locally): tenants, users, API keys, campaign config, scoring rules, verdict summaries, exclusion-sync state, and rollup summary/breakdown tables materialized from ClickHouse on a schedule. Portals/APIs read these small RLS-protected aggregate tables and never query the event store directly.
- **Redis**: sliding-window velocity counters, HyperLogLogs, dedupe, challenge tokens, per-tenant quotas — accessed only via the Redis protocol (no provider abstraction; switching Redis/Garnet/Azure Cache is a connection-string change, D5). Keys are prefixed `t:{tenantId}:…`.
- PostgreSQL was evaluated and explicitly dropped (D23) — do not reintroduce it as a "simpler" alternative without revisiting that decision.

## Solution shape

```
TelemetryGuard.sln
├── TelemetryGuard.Api                     ASP.NET Core: tracker, ingestion, decision, tenant middleware
├── TelemetryGuard.RiskEngine              feature extraction, rules, ML.NET/ONNX scoring
├── TelemetryGuard.RiskEngine.Contracts    FraudFeatureVector, verdict DTOs
├── TelemetryGuard.Analytics.Abstractions  IEventSink, IAnalyticsQueries, DTOs
├── TelemetryGuard.Analytics.ClickHouse    provider impl + /schema/*.sql
├── TelemetryGuard.Analytics.Kusto         (deferred) provider impl + /schema/*.kql
├── TelemetryGuard.Data                    Dapper repositories, TenantConnectionFactory, /migrations/*.sql (DbUp)
├── TelemetryGuard.Integrations            Google Ads client, Meta HttpClient wrapper, Turnstile verify
├── TelemetryGuard.Sdk                     TypeScript source + esbuild → static bundle artifact
└── tests/
    ├── TelemetryGuard.Tests.Contracts     shared analytics contract suite (runs per provider)
    ├── TelemetryGuard.Tests.Integration   Testcontainers: SQL Server + ClickHouse + Redis
    └── TelemetryGuard.Tests.Unit
```

Also present beyond the spec sketch: `TelemetryGuard.Core` (FND-04 primitives), `TelemetryGuard.MigrationRunner` (DbUp + provisioning CLI), `ops/grafana` provisioning, `scripts/` dev helpers.

## Testing strategy

- **Analytics contract tests**: one shared xUnit suite asserting `IAnalyticsQueries`/`IEventSink` behavior, run against every provider (ClickHouse via Testcontainers now, Kusto emulator later). This is what makes the multi-provider abstraction (D7) safe — a new provider without passing this suite should be treated as incomplete.
- **Repository integration tests**: Testcontainers SQL Server + DbUp migrations + one execution of every Dapper method, including a deliberate cross-tenant read that must return empty (proves RLS is actually enforcing isolation).
- **SDK tests**: Playwright-driven fixture pages asserting beacon shape; these fixtures double as a source of known-bot telemetry for early model labels.

## Phasing (affects what's "in scope" right now)

MVP (Phase 1) ships **without a trained ML model** — T1 deterministic rules plus a heuristic scorer behind `IScorer` only (D18). Do not add ML.NET/LightGBM training code or assume a trained model exists unless work has explicitly moved into Phase 1.5. See `doc/spec.md` §10 for the full phase breakdown before adding functionality that belongs to a later phase (e.g., Kusto provider, Kafka-protocol streaming, ONNX trajectory model, customer portal, elastic-pool tenant isolation).
