---
id: P2-04
title: Azure deployment (outline)
phase: 2
workstream: foundation
depends_on: [FND-03]
size: M
spec_refs: [D13, D16, D8, D6, "§10 Phase 2"]
detail_level: outline
---

# P2-04: Azure deployment (outline)

> **OUTLINE ONLY — expand into a full task file before implementation.** The compose-to-Azure mapping and layering rules below are the fixed points; sizing, region, and IaC tooling are open decisions.

## Objective

Move the Docker Compose stack to Azure per D16: Container Apps for the API (+ background workers + migration job), Azure SQL serverless, managed Redis, a ClickHouse hosting choice, Key Vault for secrets, Application Insights fed by the existing OpenTelemetry wiring (no code change), Azure Managed Grafana — with **Cloudflare kept in front of everything** (D13), because Azure Front Door does not pass JA3/JA4 to origin.

## Spec context (self-contained)

- D16: Container Apps first (scale-to-zero, simple) → AKS only if outgrown; Front Door + WAF; App Insights via OpenTelemetry (API-01 already exports OTLP via env vars); Key Vault; Azure Managed Grafana.
- **D13 layering (hard)**: Cloudflare remains the outermost edge even on Azure — it is the only source of TLS fingerprints (INT-05). Front Door Premium's bot-manager ruleset may be layered *behind* Cloudflare as an extra cheap signal, or omitted; it never replaces Cloudflare.
- D8: Azure SQL serverless tier (auto-pause, per-second billing) keeps early cost near zero; same T-SQL surface as the local mssql container — code and migrations run unchanged (DbUp runner, D10).
- D6: analytics stays ClickHouse at this phase (Kusto is P2-05, "later"); Azure options: self-hosted CH on VM/AKS (cheapest, most ops) vs ClickHouse Cloud on Azure Marketplace (third-party billing through Azure).
- D5: Redis protocol is the seam — Azure Cache for Redis / Azure Managed Redis is a connection-string change only.
- D1: no server-side Python/Node; deploy tooling is CLI/IaC, not runtime.

## Prerequisites

FND-03's GitHub Actions CI (build/test/publish images); FND-02's compose stack as the reference topology; API-01's env-var OTel configuration; DAT-01 migration runner (becomes a Container Apps job).

## Implementation steps (outline)

1. **Compose-to-Azure mapping table** (the deliverable's core — keep in the runbook):

   | Compose service | Azure target | Notes |
   |---|---|---|
   | `api` (ASP.NET) | Container Apps app | HTTP ingress ← Cloudflare; secrets via Key Vault refs; OTel env vars |
   | migration runner | Container Apps **job** (pre-deploy) | DbUp against Azure SQL; CH schema with `--clickhouse` |
   | `mssql` | Azure SQL Database (serverless) | same connection-string key `ConnectionStrings:Main` |
   | `redis` | Azure Managed Redis / Azure Cache | connection-string change only (D5) |
   | `clickhouse` | CH Cloud on Azure Marketplace **or** self-hosted VM/AKS | open decision; provider config unchanged (D6) |
   | `grafana` | Azure Managed Grafana | dashboards re-provisioned from OPS-01 sources |
   | OTLP collector | Application Insights (OTLP ingest / azure exporter) | env-var only per API-01 |

2. Edge chain: Cloudflare (proxy, Worker, TLS fingerprints — INT-05 runbook) → optional Front Door + WAF → Container Apps ingress; origin lockdown now = Container Apps IP restrictions to Cloudflare ranges.
3. Key Vault + managed identity for: SQL/Redis/CH connection strings, Turnstile secret, Google/Meta credentials (INT-01/03/04), beacon HMAC secret.
4. CI deploy job (GitHub Actions, OIDC federation — no long-lived cloud secrets in the repo), environments dev/prod, migration job gate before app rollout.
5. Ops runbook `doc/runbooks/azure-deployment.md` with the mapping table, rollback, and cost notes (SQL serverless auto-pause; Container Apps scale-to-zero for workers).

## Files to create or modify (outline)

- `infra/azure/` (Bicep or azd — open decision), `.github/workflows/deploy.yml`, `doc/runbooks/azure-deployment.md`; zero application-code changes expected (config/env only) — any required code change is a design smell to escalate.

## Acceptance criteria (outline)

- Full stack reachable through Cloudflare with `Edge:Provider=Cloudflare` signals working (INT-05 acceptance re-run against Azure); migrations applied by the job, app healthy (`/ready` 200); App Insights shows traces without code change; secrets only in Key Vault; teardown/redeploy reproducible from IaC.

## Testing (outline)

Smoke suite against the deployed dev environment (health, `/c` redirect, beacon roundtrip, RLS spot check); IaC validated in CI (what-if/lint); no unit-test changes.

## Out of scope / guardrails

- **Cloudflare stays outermost** (D13) — do not replace it with Front Door; losing JA3/JA4 silently degrades `tls_ua_mismatch` for every tenant.
- No Kusto migration here (P2-05, "later"); no Kafka/Event Hubs (D12 — only when volume demands); no AKS until Container Apps is measurably outgrown (D16).
- No PostgreSQL re-evaluation (D23 closed it); Azure SQL is final for the relational tier.
- Same-image promotion: the container that passed CI is what deploys — no Azure-specific build forks; no server-side Python/Node in any job (D1).
- RLS/tenancy semantics must be re-proven on Azure SQL (DAT-08 suite against an Azure SQL dev instance) — managed ≠ assumed.

## Open decisions (resolve when expanding)

1. ClickHouse hosting: Marketplace Cloud vs self-hosted (ops budget vs bill shape).
2. IaC tool: Bicep vs azd vs Terraform (team familiarity).
3. Region + data-residency implications for tenant data.
4. Front Door: include for WAF/bot-ruleset layering or skip at first (cost vs marginal signal).
5. Worker split: keep INT-03/INT-04/ANA-07 workers in the api app vs separate Container Apps with scale-to-zero.
