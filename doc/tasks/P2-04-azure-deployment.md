---
id: P2-04
title: Azure deployment
phase: 2
workstream: infra
depends_on: [FND-02, FND-03, API-01, DAT-01, SDK-08, INT-05]
size: M
spec_refs: [D16, D13, D8, D6, D5, D10, D22, D1, "§10 Phase 2"]
detail_level: full
---

# P2-04: Azure deployment

## Objective

Produce the **deployable artifacts** that move the FND-02 Compose topology onto Azure per D16, with Cloudflare kept as the outermost edge per D13:

1. A repo-root **multi-stage `Dockerfile`** (+ `.dockerignore`) that builds the SDK bundle with esbuild, replicates the `CopySdkBundle` MSBuild target so `wwwroot/sdk/tg*.js` is inside the image, publishes **both** `TelemetryGuard.Api` and `TelemetryGuard.MigrationRunner` into one image (one tag → app and migrations can never drift), and runs as a non-root user on port 8080.
2. **Bicep** templates under `infra/azure/` for Container Apps (app + two migration jobs), Azure SQL serverless, Azure Cache for Redis, a **self-hosted single-node ClickHouse VM** on a private subnet, Key Vault + user-assigned managed identity, ACR, Log Analytics + Application Insights (fed by an OpenTelemetry Collector sidecar — no app code change), and an optional Azure Managed Grafana.
3. A **gated deploy workflow** `.github/workflows/deploy.yml` (GitHub OIDC, no long-lived cloud secrets) that no-ops when the Azure secrets are absent, plus two **ungated** CI jobs (`image`, `iac`) that build the image and compile the Bicep offline on every PR.
4. An operator runbook `doc/runbooks/azure-deployment.md` with the Compose→Azure mapping table, first-deploy order, custom-domain/Cloudflare binding, rollback, secret seeding, cost notes, and the manual Azure-SQL RLS re-proof.

**Hard constraint for the implementer: this environment has no Azure credentials, and no test or CI job may make a real cloud call.** Everything here is authored and validated offline. The acceptance test is `docker build` (podman with the docker CLI aliased) plus `az bicep build` — never `az deployment`.

**Zero application-code changes.** No `.cs` file, no `appsettings*.json`, no `.csproj`, no `TelemetryGuard.sln` entry is touched by this task. Every deployed setting is an environment variable over the existing configuration binder. If you find yourself needing a code change, stop and escalate — it is a design smell (the one place this rule bites is documented in step 2.4 and solved inside the Dockerfile).

## Spec context (self-contained)

- **D16 — Hosting**: Container Apps first (AKS only when measurably outgrown); Application Insights via OpenTelemetry; Key Vault for secrets; Azure Managed Grafana.
- **D13 — Edge (hard)**: Cloudflare stays the outermost edge *even in front of Azure*, because Azure Front Door does not pass JA3/JA4 to origin. Front Door is **not** deployed by this task. Origin lockdown = Container Apps ingress `ipSecurityRestrictions` limited to Cloudflare's published ranges, which the repo already carries verbatim at `TelemetryGuard.Api/Edge/cloudflare-ips.txt` (refreshed by `scripts/update-cloudflare-ips.sh`).
- **D8/D23 — Relational tier**: Azure SQL, same T-SQL surface as the local `mcr.microsoft.com/mssql/server:2022-latest` container; migrations run unchanged through DbUp (D10). PostgreSQL is closed (D23).
- **D6 — Analytics**: ClickHouse stays the provider at this phase (Kusto is P2-05). Of D6's Azure options this task pins **self-hosted single-node ClickHouse on a VM** — see the decision table below.
- **D5 — Redis**: the protocol is the seam; Azure Cache for Redis is a connection-string change only. No provider abstraction, no code.
- **D22 — SDK delivery**: the API origin-serves `/sdk/tg.js` and `/sdk/tg-<version>.js` from `wwwroot/sdk` (SDK-08); Cloudflare is the CDN in front. If the bundle is missing from the image, `/sdk/*` silently 404s and **every tenant page loses its JS beacon** — hence the build-time assertion in step 2.4.
- **D1 — No server-side Python/Node**: the esbuild/npm stage is build-time only and never ships into the runtime image (same exception class as D2). Deploy tooling is `az`/Bicep/bash — no Node or Python runtime in any job.
- **D3/D11/D9** are unaffected: same process hosts scoring and the workers, RLS via `SESSION_CONTEXT` behaves identically on Azure SQL, Dapper over `Microsoft.Data.SqlClient` is unchanged.

### Decisions pinned here (the outline's open questions, resolved)

| # | Question | Pinned answer | Why |
|---|---|---|---|
| 1 | ClickHouse hosting | **Self-hosted single node**: Ubuntu 22.04 VM, no public IP, running the *same pinned image* `clickhouse/clickhouse-server:24.8` as `docker-compose.yml`, data on a Premium SSD mounted at `/var/lib/clickhouse` | Cheapest; identical engine version to dev and CI; one Bicep deployment owns the whole stack; ClickHouse Cloud on Marketplace is a SaaS offer that cannot be provisioned sensibly from Bicep and adds third-party billing. Swapping later is a connection-string change (D6/D7 — `Analytics__ClickHouse__ConnectionString`) |
| 2 | IaC tool | **Bicep** (not azd, not Terraform) | First-party, no state file, **compiles fully offline** (`az bicep build`) which is the only validation possible without credentials; azd would take over image build/push conventions and hide the deploy sequencing that the migration gate depends on |
| 3 | Region / residency | Single region, `location` defaults to `resourceGroup().location`; dev param file sets the RG's region and nothing is geo-replicated | All tenant data (SQL, Redis, ClickHouse disk, App Insights) stays in one region; the runbook states this plainly so a residency claim can be made honestly |
| 4 | Front Door | **Omitted** | D13: Cloudflare is already the WAF/bot layer and is the only JA3/JA4 source; Front Door would add cost and a second edge with no signal Cloudflare does not already give us |
| 5 | Worker split | **All workers stay in the API container** (D3), and the API app runs **`minReplicas: 1, maxReplicas: 1`** | `VerdictFinalizerService` documents itself as *single-instance MVP* — it claims due sessions with `ZREM` and explicitly defers multi-instance claiming. Scale-to-zero would also stop the 1-second grace sweep, the 15-minute rollup, and both exclusion syncs. Splitting workers into their own Container App requires a worker-only host (code) → out of scope; raising `maxReplicas` requires atomic Lua claiming (backlog) → out of scope |
| 6 | App Insights ingestion | **OpenTelemetry Collector sidecar** (`azuremonitor` exporter) in the same Container App, app points at `http://localhost:4317` | Zero code change and zero preview API surface. The Container Apps *managed* OTel agent would be three lines but lives on a preview API version of `managedEnvironments`; a sidecar is stable and verifiable |
| 7 | Azure SQL auto-pause | **Disabled (`autoPauseDelay: -1`)** | Honest correction to the outline: the readiness probe runs `SqlHealthCheck` (`SELECT 1`) on every poll and `RollupService` hits SQL every 15 minutes, so a 60-minute auto-pause window can never elapse. The saving comes from the low `minCapacity` floor, not from pausing |

## Parallel-execution seams (read before you touch a shared file)

P2-01..P2-05 are written to be implemented **in parallel by separate agents**. This task is the most isolated of the five — it adds no C#, no project, and no migration — and shares exactly two files:

| Shared file | Also touched by | Rule |
|---|---|---|
| `.github/workflows/ci.yml` | P2-05 | Both **append** jobs to the end of the `jobs:` map and leave `build-test`, `integration`, `sdk` byte-for-byte unchanged. This task appends `image` and `iac`; P2-05 appends `kusto-contracts`. Append at the end of the file; if a sibling already added its job, add yours after it rather than reflowing the file. |
| `.gitignore` | (none) | Only this task edits it (two IaC lines). P2-01/02/03/05 all declare it untouched. |

Everything else this task creates (`Dockerfile`, `.dockerignore`, `infra/azure/**`, `.github/workflows/deploy.yml`, `doc/runbooks/azure-deployment.md`) is **new and unshared**.

Two facts about siblings that change what you write here — both are covered in detail below, listed up front so they are not discovered late:

- **P2-03's portal is a separate web app and is NOT deployed by this task** (see step 2 note 2.7 and *Out of scope*). Do not add a portal stage, image, Container App, or hostname.
- **P2-01 and P2-02 add SQL migrations `0008` and `0009`.** They arrive through the existing wildcard `EmbeddedResource` glob with no image or template change — so never enumerate migration numbers in a Dockerfile, template, workflow, or assertion.

## Prerequisites — the exact contracts this task builds on

Read these files; every path, key, and command below is copied from them.

**Build inputs**
- `TelemetryGuard.Api/TelemetryGuard.Api.csproj` — the `CopySdkBundle` target the image must replicate:
  ```xml
  <ItemGroup>
    <SdkBundle Include="..\TelemetryGuard.Sdk\dist\tg*.js;..\TelemetryGuard.Sdk\dist\tg*.js.map" />
  </ItemGroup>
  <Target Name="CopySdkBundle" BeforeTargets="Build" Condition="@(SdkBundle) != ''">
    <Copy SourceFiles="@(SdkBundle)" DestinationFolder="$(MSBuildProjectDirectory)\wwwroot\sdk" SkipUnchangedFiles="true" />
  </Target>
  ```
  Also `<EmbeddedResource Include="Edge\cloudflare-ips.txt" .../>` (INT-05) — so the whole `TelemetryGuard.Api/` directory must be in the build context, not just `.cs` files.
- `TelemetryGuard.Api` project references: `TelemetryGuard.Core`, `TelemetryGuard.Data`, `TelemetryGuard.RiskEngine`, `TelemetryGuard.RiskEngine.Contracts`, `TelemetryGuard.Analytics.Abstractions`, `TelemetryGuard.Analytics.ClickHouse`, `TelemetryGuard.Integrations`. `TelemetryGuard.MigrationRunner` references `TelemetryGuard.Data` + `TelemetryGuard.Analytics.ClickHouse`. Those nine directories plus `Directory.Build.props` are the entire .NET build input — `TelemetryGuard.Training` and `tests/` are **not** needed.
- `Directory.Build.props` sets `TreatWarningsAsErrors=true` — the image build inherits it, so a warning fails `docker build`.
- `TelemetryGuard.Sdk/package.json` scripts: `build` → `node scripts/build.mjs` (esbuild → `dist/tg.js`, `dist/tg.js.map`, `dist/tg-0.1.0.js`, `dist/tg-0.1.0.js.map`, `dist/meta.json`), `size` → `node scripts/check-size.mjs` (30 KB gzip gate, D2). `npm ci` needs devDependencies (esbuild, typescript) — never `--omit=dev`.
- `.gitignore` already ignores `TelemetryGuard.Sdk/dist/` and `TelemetryGuard.Api/wwwroot/sdk/` — both are build outputs, so the image must build them, never expect them in the context.

**Runtime contracts (`TelemetryGuard.Api/Program.cs`, `appsettings.json`)**
- Ports/health: `/healthz` is liveness (`Predicate = _ => false`, no dependency touched); `/ready` runs the `"ready"`-tagged checks `SqlHealthCheck`, `RedisHealthCheck`, `ClickHouseHealthCheck`. Both are exempt from the rate limiter.
- Startup **fails** unless `Beacon:HmacSecret` is set and ≥ 32 chars (`.ValidateOnStart()`), and unless `Analytics:Provider` is `"ClickHouse"` with a non-empty `Analytics:ClickHouse:ConnectionString`. A container started without `Beacon__HmacSecret` crash-loops — that is by design (API-04), and every `docker run` in this task passes one.
- Config keys consumed at deploy time (ASP.NET `__` env override syntax): `ConnectionStrings__Main`, `ConnectionStrings__Redis`, `Analytics__Provider`, `Analytics__ClickHouse__ConnectionString`, `Beacon__HmacSecret`, `Edge__Provider`, `FeatureExtraction__TlsUaMismatchEnabled`, `Turnstile__SiteKey`, `Turnstile__SecretKey`, `Synthetic__Enabled`.
- OTel is configured **only** by the standard env vars (`OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_SERVICE_NAME`), deliberately not appsettings — `AddOtlpExporter()` is called with no arguments.
- `Edge:Provider = "Cloudflare"` merges the embedded Cloudflare CIDRs into `ForwardedHeaders.KnownNetworks` **and** switches on `X-TG-*` intake (`EdgeSignalReader`, gated a second time on the direct peer being a Cloudflare address). Local dev stays `"None"` (INT-05 runbook §7).
- `IpEnrichment` (RSK-02) binds `DataDir` default `./data/geo`; **all GeoIP files are optional at runtime — missing files degrade enrichment to null, never a startup failure**. The image ships without them (licensed downloads, `data/geo/` is git-ignored). Mounting them is a documented follow-up, not part of this task.
- `Scoring:ModelPath` is unset ⇒ pure heuristic scorer (D18). No model artifact goes into the image.
- `Logging` is `AddJsonConsole` — one JSON object per line, which is exactly what Container Apps ships to Log Analytics (`ContainerAppConsoleLogs_CL`). No log agent needed.

**Migration contract (`TelemetryGuard.MigrationRunner/Program.cs`)**
- No args → `Migrations.RunSqlServer(env MIGRATIONS_CONNECTIONSTRING)`; DbUp `EnsureDatabase` + **every** embedded `TelemetryGuard.Data/migrations/*.sql` (`<EmbeddedResource Include="migrations\**\*.sql" />` — a wildcard, so the set grows without a code or image change; `0007_meta_sync.sql` is the highest today, and P2-01/P2-02 add `0008`/`0009` in parallel), journal `dbo.SchemaVersions`, transaction per script, applied in name order. Exit code 0/1, or 2 when the env var is missing. **Never enumerate the script numbers in a Dockerfile, Bicep template, workflow, or assertion** — the count is not this task's business and would go stale the moment a sibling merges.
- `--clickhouse` → `Migrations.RunClickHouseAsync(env CLICKHOUSE_CONNECTIONSTRING)` → ANA-02's `SchemaMigrator` (journal `tg_schema_migrations`).
- `provision <create-tenant|issue-api-key|register-site|create-campaign>` → `ProvisionCommand`, reads `MIGRATIONS_CONNECTIONSTRING`.
- Env var names are **plain** (`MIGRATIONS_CONNECTIONSTRING`, `CLICKHOUSE_CONNECTIONSTRING`) — they are read with `Environment.GetEnvironmentVariable`, not through the configuration binder, so no `__` translation.
- ClickHouse connection-string shape (ClickHouse.Client 7.2.2): `Host=…;Port=8123;Database=telemetry_guard;Username=tg;Password=…`.

**Topology reference (`docker-compose.yml`, `scripts/dev-*.sh`)** — services `mssql`, `redis`, `clickhouse` (image `clickhouse/clickhouse-server:24.8`, env `CLICKHOUSE_DB`/`CLICKHOUSE_USER`/`CLICKHOUSE_PASSWORD`, `nofile` ulimit 262144), `grafana` (provisioning from `ops/grafana/`). The API is deliberately **not** a compose service; this task is the first time it is containerized.

**CI reference (`.github/workflows/ci.yml`)** — house patterns to copy: `actions/checkout@v4`, `actions/setup-dotnet@v4` (8.0.x), NuGet cache keyed on `hashFiles('**/*.csproj', 'Directory.Build.props')`, and — importantly — the **gate-step-with-output** idiom already used by the `sdk` job (`steps.gate.outputs.exists`), which is the only way to branch on a secret's presence (the `secrets` context is not available in `jobs.<id>.if`).

**INT-05 (`doc/runbooks/cloudflare-fronting.md`)** — origin lockdown to Cloudflare ranges is mandatory, `Edge:Provider=Cloudflare` is set only in the fronted environment, and `FeatureExtraction:TlsUaMismatchEnabled` flips to `true` only once JA3/JA4 actually flow (Enterprise Bot Management). The worker at `infra/cloudflare/tg-edge-worker.js` forwards the request **unchanged apart from headers** — the `Host` header stays the tenant-facing hostname, which is why the Container App needs a custom-domain binding (step 6.3).

## Implementation steps

### 1. `.dockerignore` (repo root, new)

Keeps the context small and — critically — keeps *host* build outputs from leaking into the image, so the bundle in the image is always the one this build produced.

```gitignore
# Build outputs and VCS
.git/
**/bin/
**/obj/
artifacts/
TestResults/
coverage/

# Node + SDK build outputs: the image builds these itself (stage `sdk`).
TelemetryGuard.Sdk/node_modules/
TelemetryGuard.Sdk/dist/
TelemetryGuard.Sdk/test-results/
# NEVER ship a host-built bundle: stage `sdk` is the only source of wwwroot/sdk.
TelemetryGuard.Api/wwwroot/sdk/

# Not needed to build the API or the migrator
tests/
TelemetryGuard.Training/
doc/
ops/
infra/
.github/
data/
*.md
.env
```

> Do **not** add `TelemetryGuard.Api/wwwroot/` — only the `sdk/` subdirectory. Any *committed* static asset under the **API's** `wwwroot/` must reach the image. (P2-03's portal assets live under `TelemetryGuard.Portal/wwwroot/`, which no stage copies — see note 2.7.)
>
> `TelemetryGuard.Portal/` (P2-03) and `TelemetryGuard.Analytics.Kusto/` (P2-05) are **not** listed above on purpose: neither is `COPY`-ed by any stage, so neither enters the image whether or not it exists in the tree. Do not add ignore entries for them — a `.dockerignore` line for a directory that may not exist yet is noise, and the build must not care either way.

### 2. `Dockerfile` (repo root, new)

Three stages. **Plain Dockerfile syntax only — no BuildKit heredocs, no `RUN --mount`** — because the acceptance test is `podman build` via the aliased `docker` CLI.

```dockerfile
# TelemetryGuard API image (P2-04).
#   stage 1 `sdk`     — esbuild bundle (D2/D22); build-time Node only, never shipped (D1)
#   stage 2 `build`   — dotnet publish of TelemetryGuard.Api AND TelemetryGuard.MigrationRunner
#   stage 3 `runtime` — aspnet:8.0, non-root, port 8080
# ONE image, TWO entrypoints: the API (default) and the migrator (command override in
# the Container Apps jobs). One tag can never deploy app vN against schema vN-1.
# Build from the repo ROOT: docker build -t telemetryguard-api:local .

# ---------- stage 1: SDK bundle ----------
FROM docker.io/library/node:20-bookworm-slim AS sdk
WORKDIR /sdk
COPY TelemetryGuard.Sdk/package.json TelemetryGuard.Sdk/package-lock.json ./
RUN npm ci
COPY TelemetryGuard.Sdk/ ./
# `npm run build` = node scripts/build.mjs -> dist/tg.js + dist/tg-<version>.js (+ maps)
# `npm run size`  = the 30 KB gzip gate (spec D2). Failing it fails the image build.
RUN npm run build && npm run size

# ---------- stage 2: .NET publish ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# csproj-only layer so NuGet restore caches independently of source edits.
COPY Directory.Build.props ./
COPY TelemetryGuard.Api/TelemetryGuard.Api.csproj                                       TelemetryGuard.Api/
COPY TelemetryGuard.Core/TelemetryGuard.Core.csproj                                     TelemetryGuard.Core/
COPY TelemetryGuard.Data/TelemetryGuard.Data.csproj                                     TelemetryGuard.Data/
COPY TelemetryGuard.RiskEngine/TelemetryGuard.RiskEngine.csproj                         TelemetryGuard.RiskEngine/
COPY TelemetryGuard.RiskEngine.Contracts/TelemetryGuard.RiskEngine.Contracts.csproj     TelemetryGuard.RiskEngine.Contracts/
COPY TelemetryGuard.Analytics.Abstractions/TelemetryGuard.Analytics.Abstractions.csproj TelemetryGuard.Analytics.Abstractions/
COPY TelemetryGuard.Analytics.ClickHouse/TelemetryGuard.Analytics.ClickHouse.csproj     TelemetryGuard.Analytics.ClickHouse/
COPY TelemetryGuard.Integrations/TelemetryGuard.Integrations.csproj                     TelemetryGuard.Integrations/
COPY TelemetryGuard.MigrationRunner/TelemetryGuard.MigrationRunner.csproj               TelemetryGuard.MigrationRunner/
RUN dotnet restore TelemetryGuard.Api/TelemetryGuard.Api.csproj \
 && dotnet restore TelemetryGuard.MigrationRunner/TelemetryGuard.MigrationRunner.csproj

# Sources (the whole project directories: Edge/cloudflare-ips.txt is an EmbeddedResource,
# migrations/*.sql and schema/*.sql are EmbeddedResources too).
COPY TelemetryGuard.Api/                     TelemetryGuard.Api/
COPY TelemetryGuard.Core/                    TelemetryGuard.Core/
COPY TelemetryGuard.Data/                    TelemetryGuard.Data/
COPY TelemetryGuard.RiskEngine/              TelemetryGuard.RiskEngine/
COPY TelemetryGuard.RiskEngine.Contracts/    TelemetryGuard.RiskEngine.Contracts/
COPY TelemetryGuard.Analytics.Abstractions/  TelemetryGuard.Analytics.Abstractions/
COPY TelemetryGuard.Analytics.ClickHouse/    TelemetryGuard.Analytics.ClickHouse/
COPY TelemetryGuard.Integrations/            TelemetryGuard.Integrations/
COPY TelemetryGuard.MigrationRunner/         TelemetryGuard.MigrationRunner/

# SDK-08 / CopySdkBundle: the <SdkBundle Include="..\TelemetryGuard.Sdk\dist\tg*.js…"/>
# item is evaluated when MSBuild LOADS the project, so the files must already exist
# on disk before `dotnet publish` starts. This COPY is what makes the target fire.
COPY --from=sdk /sdk/dist/ TelemetryGuard.Sdk/dist/

RUN dotnet publish TelemetryGuard.Api/TelemetryGuard.Api.csproj \
        -c Release --no-restore -o /out/app
RUN dotnet publish TelemetryGuard.MigrationRunner/TelemetryGuard.MigrationRunner.csproj \
        -c Release --no-restore -o /out/app/migrator

# The default wwwroot/** publish glob is evaluated BEFORE CopySdkBundle runs, so files
# that target creates during Build are NOT in the publish output. Copy them explicitly
# and FAIL THE BUILD if they are missing — a silently bundle-less image means /sdk/*
# 404s and every tenant page loses its beacon (D22).
RUN mkdir -p /out/app/wwwroot/sdk \
 && cp TelemetryGuard.Api/wwwroot/sdk/tg*.js     /out/app/wwwroot/sdk/ \
 && cp TelemetryGuard.Api/wwwroot/sdk/tg*.js.map /out/app/wwwroot/sdk/ \
 && test -f /out/app/wwwroot/sdk/tg.js \
 && test -f /out/app/wwwroot/sdk/tg.js.map \
 && ls -1 /out/app/wwwroot/sdk

# ---------- stage 3: runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
ARG GIT_SHA=unknown
LABEL org.opencontainers.image.title="TelemetryGuard API" \
      org.opencontainers.image.revision="$GIT_SHA"
WORKDIR /app
COPY --from=build /out/app ./
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    OTEL_SERVICE_NAME=telemetry-guard-api
EXPOSE 8080
# APP_UID (1654) and the `app` user are defined by the .NET 8 base image.
USER $APP_UID
# ContentRootPath is /app, so Program.cs resolves wwwroot/sdk relative to it.
ENTRYPOINT ["dotnet", "/app/TelemetryGuard.Api.dll"]
```

Notes the implementer must not "optimize" away:

- **2.1** `npm ci` runs before the SDK sources are copied purely for layer caching; `.dockerignore` excludes `node_modules/`, so the later `COPY TelemetryGuard.Sdk/ ./` cannot clobber it.
- **2.2** The `sdk` stage is discarded — no Node binary exists in the runtime image (D1).
- **2.3** `TreatWarningsAsErrors=true` comes from `Directory.Build.props`; it must be copied before restore or the build silently differs from CI.
- **2.4** The explicit `cp` after publish is the only correct fix that needs no code change. Adding a `ResolvedFileToPublish` target to `TelemetryGuard.Api.csproj` would also work and was **rejected**: it would edit shipped application build config for a deployment concern, and it would make `CopySdkBundle` no longer the single definition of "which files are the bundle".
- **2.5** No `HEALTHCHECK` instruction: Container Apps ignores it and uses the probes declared in Bicep (step 5.8).
- **2.6** Do not add `--no-build`, `-r linux-x64`, `PublishTrimmed`, or `PublishSingleFile`. The migrator relies on `ProvisionCommand` reflection-free DI but trimming is untested here and buys nothing.
- **2.7 The portal (P2-03) is NOT in this image, and that is deliberate.** P2-03 is a fully specified sibling task that may land in parallel. Its shape, copied from its task file so you do not have to guess: a **separate `TelemetryGuard.Portal` ASP.NET Core Razor Pages project** with **zero npm, zero bundler, zero package.json** (its one static asset is a hand-written `TelemetryGuard.Portal/wwwroot/css/portal.css`), which is a pure HTTP client of `/admin/*` and holds no connection string. Consequences, all binding on this task:
  - **Do not add a `portal` stage, a Node stage, or a `COPY TelemetryGuard.Portal/ …` line.** There is nothing to bundle, and the portal is a *second web app* — it needs its own image, its own Container App and its own ingress/hostname, not a slot inside the API image. Bolting it into this image would put the operator UI behind the Cloudflare-only `ipSecurityRestrictions` allow-list built for the tracker origin (step 5.9), which is exactly wrong.
  - **Portal deployment is a separate follow-up task**, not this one — P2-03's own guardrails say the same, and this task's *Out of scope* repeats it. Say so plainly in the runbook's mapping table (one row: *"portal — not deployed by this task"*) so an operator is never left wondering whether it shipped.
  - `.dockerignore` needs **no** portal entry: `TelemetryGuard.Portal/` simply is not `COPY`-ed by any stage, so it never enters the image even when it is present in the context.
  - What this Dockerfile *does* guarantee is the general mechanism for **API-owned** static assets: `COPY TelemetryGuard.Api/ TelemetryGuard.Api/` plus `dotnet publish` carries the whole of `TelemetryGuard.Api/wwwroot/**` (everything except the git-ignored `sdk/` subdirectory, which stage `sdk` supplies) into `/app/wwwroot`. That covers any future asset committed under the **API's** `wwwroot/`; it does not and must not reach into another project's `wwwroot/`.

### 3. Local acceptance run (this is the task's acceptance test)

```bash
source ~/.tg-env                 # DOCKER_HOST=podman socket
cd /home/shafqat/git/shafqat/telemtry-guard

docker build -t telemetryguard-api:local --build-arg GIT_SHA="$(git rev-parse --short HEAD)" .

# 1. the SDK bundle is inside the image (D22)
docker run --rm telemetryguard-api:local ls -1 /app/wwwroot/sdk
#   expect: tg-0.1.0.js  tg-0.1.0.js.map  tg.js  tg.js.map

# 2. the migrator is inside the same image and refuses to run unconfigured (exit 2)
docker run --rm telemetryguard-api:local dotnet /app/migrator/TelemetryGuard.MigrationRunner.dll; echo "exit=$?"
#   expect: "MIGRATIONS_CONNECTIONSTRING environment variable is not set." exit=2

# 3. the API starts with no backing services and answers liveness
docker run -d --name tg-smoke -p 18080:8080 \
  -e Beacon__HmacSecret=local-only-not-a-real-secret-0123456789 \
  telemetryguard-api:local
sleep 8
curl -fsS  http://localhost:18080/healthz ; echo          # expect 200 Healthy
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:18080/ready   # expect 503 (no SQL/Redis/CH) — correct
curl -sI   http://localhost:18080/sdk/tg.js | head -5     # expect 200 + text/javascript + max-age=300
curl -sI   http://localhost:18080/sdk/tg-0.1.0.js | head -5  # expect 200 + immutable
docker rm -f tg-smoke
```

`/ready` returning 503 with no databases is the **expected** result, not a failure — it is what makes Container Apps hold a revision back until SQL, Redis and ClickHouse are all reachable.

### 4. `infra/azure/` layout

```
infra/azure/
  bicepconfig.json            # linter rules raised to errors (house TreatWarningsAsErrors analogue)
  main.bicep                  # resourceGroup-scoped orchestrator
  modules/network.bicep
  modules/observability.bicep
  modules/registry.bicep
  modules/keyvault.bicep
  modules/sql.bicep
  modules/redis.bicep
  modules/clickhouse-vm.bicep
  modules/containerapps.bicep
  modules/grafana.bicep
  cloud-init/clickhouse.yaml
  params.dev.json
  params.prod.json
  verify-rls.sql
```

`bicepconfig.json`:

```json
{
  "analyzers": {
    "core": {
      "enabled": true,
      "verbose": false,
      "rules": {
        "no-hardcoded-env-urls":            { "level": "error" },
        "outputs-should-not-contain-secrets":{ "level": "error" },
        "secure-parameter-default":         { "level": "error" },
        "no-unused-params":                 { "level": "error" },
        "no-unused-vars":                   { "level": "error" }
      }
    }
  }
}
```

> **API versions below are the ones this task was written against.** `az bicep build` reports an unknown type/version immediately and offline — if your Bicep CLI disagrees, bump to the newest **stable (non-preview)** version for that type and note it in the runbook. Never introduce a preview API version for a resource this task deploys.

**Why ARM parameter JSON and not `.bicepparam`:** `az deployment group create` refuses to combine a `.bicepparam` file with inline `-p key=value` overrides, and this deploy *must* override `containerImage`, `cloudflareIpv4Ranges` and the secure passwords per run. Plain `--parameters @params.dev.json -p key=value …` supports both.

### 5. `main.bicep` and modules

**5.1 `main.bicep` — parameters and wiring**

```bicep
targetScope = 'resourceGroup'

@description('dev | prod — suffixes every resource name and selects sizing defaults.')
@allowed(['dev', 'prod'])
param environmentName string

param location string = resourceGroup().location

@description('Lowercase alphanumeric prefix, 3-8 chars, used to build globally unique names.')
@minLength(3)
@maxLength(8)
param namePrefix string = 'tguard'

@description('Full image reference incl. tag, e.g. acr.azurecr.io/telemetryguard-api:<sha>. Supplied per deploy — never a moving tag.')
param containerImage string

@description('OpenTelemetry Collector image, imported into ACR by the deploy workflow.')
param otelCollectorImage string

@description('Cloudflare IPv4 CIDRs allowed to reach ingress (D13 origin lockdown). Generated from TelemetryGuard.Api/Edge/cloudflare-ips.txt by the deploy workflow.')
param cloudflareIpv4Ranges array

@description('Set false for phase A of a deploy (infra + migration jobs only), true for phase B (roll the app).')
param deployApiApp bool = true

@description('Turnstile / Google Ads / Meta secrets have been seeded in Key Vault (runbook step 8) — until then the app runs with the inert appsettings defaults.')
param integrationSecretsEnabled bool = false

param deployGrafana bool = false

@description('Operator workstation IPv4 allowed through the SQL firewall; empty = none.')
param opsClientIp string = ''

param sqlAdminLogin string = 'tgadmin'
@secure()
param sqlAdminPassword string
@secure()
param clickHousePassword string
@secure()
param beaconHmacSecret string

@description('Custom domain bound to ingress (the Cloudflare-proxied hostname). Empty on the first deploy — see runbook step 6.')
param customDomainName string = ''
@description('Resource id of a managedEnvironments/certificates resource. Required when customDomainName is set.')
param customDomainCertificateId string = ''
```

Derived names (`var`): `containerAppEnvName = '${namePrefix}-cae-${environmentName}'`, `sqlServerName = '${namePrefix}-sql-${environmentName}'`, `databaseName = 'TelemetryGuard'`, `redisName = '${namePrefix}-redis-${environmentName}'`, `keyVaultName = '${namePrefix}kv${environmentName}${uniqueString(resourceGroup().id)}'` (24-char limit — truncate with `substring`), `acrName = '${namePrefix}acr${environmentName}${uniqueString(resourceGroup().id)}'` (alphanumeric only), `clickHousePrivateIp = '10.20.2.4'`, `clickHouseDb = 'telemetry_guard'`, `clickHouseUser = 'tg'`.

`main.bicep` instantiates the modules in this order and passes outputs forward: `network` → `observability` → `registry` → `keyvault` → (`sql`, `redis`, `clickhouseVm`) → `containerapps` → optional `grafana`. It **outputs** `apiFqdn`, `acrLoginServer`, `sqlServerFqdn`, `keyVaultName`, `containerAppName`, `migrateSqlJobName`, `migrateClickHouseJobName` — and **no secrets** (the `outputs-should-not-contain-secrets` linter rule is set to error for exactly this reason).

**5.2 `modules/network.bicep`** — `Microsoft.Network/virtualNetworks@2023-11-01`

- VNet `10.20.0.0/16`.
- `snet-infra` `10.20.0.0/23`, **delegated to `Microsoft.App/environments`** (required for a workload-profile Container Apps environment).
- `snet-data` `10.20.2.0/24` for the ClickHouse VM.
- NSG on `snet-data` (`Microsoft.Network/networkSecurityGroups@2023-11-01`): inbound `Allow` TCP `8123,9000` from source `10.20.0.0/23` (priority 100); inbound `Deny *` from `Internet` (priority 4096 is the platform default deny — add an explicit `DenyInternetInbound` at 200 anyway so the intent is readable in the portal). No SSH rule: the VM has no public IP and is administered with `az vm run-command`.
- Outputs: `infraSubnetId`, `dataSubnetId`.

**5.3 `modules/observability.bicep`** — `Microsoft.OperationalInsights/workspaces@2023-09-01` (PerGB2018, `retentionInDays: 30`) and `Microsoft.Insights/components@2020-02-02` (`Application_Type: 'web'`, `WorkspaceResourceId` = the workspace, i.e. workspace-based App Insights — classic is retired). Outputs: `workspaceId`, `workspaceCustomerId`, `workspaceSharedKey` (`@secure()` output, consumed only by the Container Apps environment), `appInsightsConnectionString` (`@secure()` output).

**5.4 `modules/registry.bicep`** — `Microsoft.ContainerRegistry/registries@2023-07-01`, SKU `Basic`, `adminUserEnabled: false` (pull is via managed identity, never admin creds). Creates the user-assigned identity here or in `main` (`Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31`, name `${namePrefix}-id-${environmentName}`) and an `AcrPull` role assignment:

```bicep
resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, uami.id, 'AcrPull')
  scope: acr
  properties: {
    principalId: uami.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d') // AcrPull
  }
}
```

**5.5 `modules/keyvault.bicep`** — `Microsoft.KeyVault/vaults@2023-07-01`, `enableRbacAuthorization: true`, `enableSoftDelete: true`, `softDeleteRetentionInDays: 7` (dev) / `90` (prod), `publicNetworkAccess: 'Enabled'`. Role assignment `Key Vault Secrets User` (`4633458b-17de-408a-b874-0445c86b69e6`) for the UAMI, scoped to the vault. Creates these `vaults/secrets` children from values Bicep can compute:

| Secret name | Value |
|---|---|
| `sql-connection-string` | `Server=tcp:${sqlFqdn},1433;Initial Catalog=TelemetryGuard;User ID=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;` |
| `redis-connection-string` | `${redisHostName}:6380,password=${redisPrimaryKey},ssl=True,abortConnect=False` |
| `clickhouse-connection-string` | `Host=${clickHousePrivateIp};Port=8123;Database=telemetry_guard;Username=tg;Password=${clickHousePassword}` |
| `beacon-hmac-secret` | `${beaconHmacSecret}` |
| `appinsights-connection-string` | the App Insights connection string |

Third-party secrets (`turnstile-secret-key`, `turnstile-site-key`, `googleads-developer-token`, `googleads-oauth-client-id`, `googleads-oauth-client-secret`, `googleads-oauth-refresh-token`, `meta-system-user-token`) are **seeded by the operator** with `az keyvault secret set` (runbook step 8) and are only referenced when `integrationSecretsEnabled` is `true`. This is safe because the shipped `appsettings.json` defaults keep Turnstile fail-closed and both sync workers in `EffectiveDryRun` — a fresh environment is inert, never accidentally mutating live ad accounts.

**5.6 `modules/sql.bicep`** — `Microsoft.Sql/servers@2021-11-01` + `servers/databases@2021-11-01`:

```bicep
resource sqlServer 'Microsoft.Sql/servers@2021-11-01' = {
  name: sqlServerName
  location: location
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource db 'Microsoft.Sql/servers/databases@2021-11-01' = {
  parent: sqlServer
  name: 'TelemetryGuard'
  location: location
  sku: { name: 'GP_S_Gen5', tier: 'GeneralPurpose', family: 'Gen5', capacity: environmentName == 'prod' ? 4 : 2 }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    minCapacity: json('0.5')                       // serverless floor (D8)
    autoPauseDelay: -1                             // disabled — see decision 7
    maxSizeBytes: 34359738368                      // 32 GB
    zoneRedundant: false
    requestedBackupStorageRedundancy: 'Local'
  }
}

// Container Apps outbound reaches SQL over the Azure backbone.
resource allowAzure 'Microsoft.Sql/servers/firewallRules@2021-11-01' = {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}
```

Plus a conditional `opsClientIp` firewall rule (`condition: !empty(opsClientIp)`) so an operator can run `sqlcmd`/`verify-rls.sql`. Output `sqlServerFqdn = sqlServer.properties.fullyQualifiedDomainName`.

Notes that matter for correctness: `sp_set_session_context` and the `rls.TenantIsolationPolicy` security policy from migration `0002_rls_policy.sql` behave identically on Azure SQL — no code or migration change. Session context is cleared by `sp_reset_connection` when a pooled connection is reused, exactly as locally, so `TenantConnectionFactory` remains correct. `EnsureDatabase.For.SqlDatabase` in DbUp finds the database already created by Bicep and no-ops.

**5.7 `modules/redis.bicep`** — `Microsoft.Cache/redis@2023-08-01`:

```bicep
sku: environmentName == 'prod'
  ? { name: 'Standard', family: 'C', capacity: 1 }
  : { name: 'Basic',    family: 'C', capacity: 0 }
properties: {
  enableNonSslPort: false
  minimumTlsVersion: '1.2'
  publicNetworkAccess: 'Enabled'
  redisConfiguration: { 'maxmemory-policy': 'volatile-lru' }   // matches docker-compose.yml
}
```

`maxmemory-policy: volatile-lru` mirrors the compose flags exactly — velocity counters, dedupe keys and challenge tokens all carry TTLs, so only volatile keys are evicted. The primary key is read with `redis.listKeys().primaryKey` inside the module and returned as a `@secure()` output for the Key Vault secret. **Do not** add a provider abstraction (D5): this is a connection-string change and nothing more. Record in the runbook that Microsoft has announced the retirement of the Basic/Standard/Premium tiers in favour of Azure Managed Redis (`Microsoft.Cache/redisEnterprise`) — verify the current date before choosing for prod; the migration is, again, a connection-string change.

**5.8 `modules/clickhouse-vm.bicep`** — `Microsoft.Compute/virtualMachines@2023-09-01`:

- Size `Standard_D2as_v5` (prod: `Standard_D4as_v5`), Ubuntu 22.04 LTS (`Canonical / 0001-com-ubuntu-server-jammy / 22_04-lts-gen2`).
- OS disk 64 GB Premium SSD; **data disk** `Premium_LRS`, 256 GB, `lun: 0`, `createOption: 'Empty'`, `caching: 'None'`.
- NIC in `snet-data` with a **static private IP** = `clickHousePrivateIp`, **no public IP**, no Bastion.
- Admin: `disablePasswordAuthentication: true` and an SSH key parameter is *not* required — administration is `az vm run-command invoke`. If you do want SSH, add the key as a parameter and a Bastion; both are out of scope here.
- `osProfile.customData` = `base64(loadTextContent('../cloud-init/clickhouse.yaml'))` — but the file needs the password substituted, so build it as `base64(replace(replace(loadTextContent('../cloud-init/clickhouse.yaml'), '__CH_PASSWORD__', clickHousePassword), '__CH_DB__', clickHouseDb))`. `customData` is not returned by ARM `GET`; it is readable as root inside the VM at `/var/lib/cloud/instance/user-data.txt` — state that in the runbook.

`cloud-init/clickhouse.yaml`:

```yaml
#cloud-config
package_update: true
packages:
  - docker.io
runcmd:
  - set -euo pipefail
  - |
    DEV=/dev/disk/azure/scsi1/lun0
    if ! blkid "$DEV"; then mkfs.ext4 -F "$DEV"; fi
    mkdir -p /var/lib/clickhouse
    grep -q "$DEV" /etc/fstab || echo "$DEV /var/lib/clickhouse ext4 defaults,nofail 0 2" >> /etc/fstab
    mount -a
  - systemctl enable --now docker
  - |
    docker run -d --name clickhouse --restart=always \
      -p 8123:8123 -p 9000:9000 \
      -e CLICKHOUSE_DB=__CH_DB__ \
      -e CLICKHOUSE_USER=tg \
      -e CLICKHOUSE_PASSWORD=__CH_PASSWORD__ \
      -e CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT=1 \
      -v /var/lib/clickhouse:/var/lib/clickhouse \
      --ulimit nofile=262144:262144 \
      clickhouse/clickhouse-server:24.8
```

Same image tag, same env vars, same ulimit as `docker-compose.yml` — dev, CI (Testcontainers) and prod all run ClickHouse 24.8, which is what makes the ANA-06 contract suite meaningful for the deployed environment. It binds to the VM's private IP only, reachable solely from `snet-infra` per the NSG.

**5.9 `modules/containerapps.bicep`** — the substantial one.

Environment (`Microsoft.App/managedEnvironments@2024-03-01`):

```bicep
resource env 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: containerAppEnvName
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: { customerId: workspaceCustomerId, sharedKey: workspaceSharedKey }
    }
    vnetConfiguration: { infrastructureSubnetId: infraSubnetId, internal: false }
    workloadProfiles: [ { name: 'Consumption', workloadProfileType: 'Consumption' } ]
    zoneRedundant: false
  }
}
```

API app (`Microsoft.App/containerApps@2024-03-01`, `condition: deployApiApp`):

- `identity: { type: 'UserAssigned', userAssignedIdentities: { '${uamiId}': {} } }`
- `configuration.registries: [{ server: acrLoginServer, identity: uamiId }]`
- `configuration.secrets`: one entry per Key Vault secret, e.g.
  ```bicep
  { name: 'sql-connection-string', keyVaultUrl: '${keyVaultUri}secrets/sql-connection-string', identity: uamiId }
  ```
  (versionless URL so a rotated secret is picked up by the next revision).
- `configuration.ingress`:
  ```bicep
  ingress: {
    external: true
    targetPort: 8080
    transport: 'auto'
    allowInsecure: false
    ipSecurityRestrictions: [for (cidr, i) in cloudflareIpv4Ranges: {
      name: 'cloudflare-${i}'
      description: 'D13 origin lockdown — Cloudflare published IPv4 ranges'
      ipAddressRange: cidr
      action: 'Allow'
    }]
    customDomains: empty(customDomainName) ? [] : [
      { name: customDomainName, certificateId: customDomainCertificateId, bindingType: 'SniEnabled' }
    ]
  }
  ```
  Any `Allow` rule makes everything else denied — that IS the origin lockdown. IPv6 ranges from `cloudflare-ips.txt` are deliberately excluded: Container Apps ingress is IPv4, so Cloudflare reaches the origin over IPv4. Health probes are executed inside the replica and are unaffected by these rules.
- `template.scale: { minReplicas: 1, maxReplicas: 1 }` — **do not change this without reading decision 5 above.** Add the reason as a Bicep comment: `VerdictFinalizerService` claims sessions with `ZREM` and is documented single-instance; a second replica double-runs the rollup and races the grace sweep.
- `template.containers[0]` — the API:
  - `image: containerImage`, `name: 'api'`, `resources: { cpu: json('0.75'), memory: '1.5Gi' }`
  - `probes`:
    ```bicep
    probes: [
      { type: 'Liveness',  httpGet: { path: '/healthz', port: 8080 }, initialDelaySeconds: 10, periodSeconds: 30, failureThreshold: 3 }
      { type: 'Readiness', httpGet: { path: '/ready',   port: 8080 }, initialDelaySeconds: 10, periodSeconds: 15, failureThreshold: 6 }
      { type: 'Startup',   httpGet: { path: '/healthz', port: 8080 }, periodSeconds: 5, failureThreshold: 30 }
    ]
    ```
    Consequence to document: because `/ready` includes `ClickHouseHealthCheck`, a revision will not go healthy while the ClickHouse VM is down — that is the intended ordering guard, and it is why the runbook's first-deploy order puts the VM before the app.
  - `env` (exhaustive):
    | Name | Source |
    |---|---|
    | `ASPNETCORE_ENVIRONMENT` | `Production` |
    | `ASPNETCORE_HTTP_PORTS` | `8080` |
    | `OTEL_SERVICE_NAME` | `telemetry-guard-api` |
    | `OTEL_EXPORTER_OTLP_ENDPOINT` | `http://localhost:4317` |
    | `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` |
    | `ConnectionStrings__Main` | secretRef `sql-connection-string` |
    | `ConnectionStrings__Redis` | secretRef `redis-connection-string` |
    | `Analytics__Provider` | `ClickHouse` |
    | `Analytics__ClickHouse__ConnectionString` | secretRef `clickhouse-connection-string` |
    | `Beacon__HmacSecret` | secretRef `beacon-hmac-secret` |
    | `Edge__Provider` | `Cloudflare` (D13/INT-05 — this environment *is* Cloudflare-fronted) |
    | `FeatureExtraction__TlsUaMismatchEnabled` | `false` until JA3/JA4 actually arrive (INT-05 §7); flip via a param in a later deploy |
    | `Synthetic__Enabled` | `false` (explicit; the header is never trusted outside dev) |
    | `Turnstile__SiteKey`, `Turnstile__SecretKey`, `GoogleAds__DeveloperToken`, `GoogleAds__OAuthClientId`, `GoogleAds__OAuthClientSecret`, `GoogleAds__OAuthRefreshToken`, `Meta__SystemUserToken` | secretRefs, **only when `integrationSecretsEnabled`** (`concat()` the array conditionally) |
- `template.containers[1]` — the OTel collector sidecar:
  - `image: otelCollectorImage`, `name: 'otel'`, `resources: { cpu: json('0.25'), memory: '0.5Gi' }`, `args: ['--config=env:OTELCOL_CONFIG']`
  - `env`: `OTELCOL_CONFIG` = the YAML below (a Bicep multi-line `'''…'''` string — verbatim, so `${env:…}` is *not* interpreted by Bicep), `APPLICATIONINSIGHTS_CONNECTION_STRING` = secretRef `appinsights-connection-string`.
  ```yaml
  receivers:
    otlp:
      protocols:
        grpc:
          endpoint: 0.0.0.0:4317
  processors:
    memory_limiter:
      check_interval: 5s
      limit_percentage: 75
      spike_limit_percentage: 20
    batch: {}
  exporters:
    azuremonitor:
      connection_string: ${env:APPLICATIONINSIGHTS_CONNECTION_STRING}
  service:
    pipelines:
      traces:
        receivers: [otlp]
        processors: [memory_limiter, batch]
        exporters: [azuremonitor]
      metrics:
        receivers: [otlp]
        processors: [memory_limiter, batch]
        exporters: [azuremonitor]
  ```
  Container Apps containers in one replica share a network namespace, so `localhost:4317` reaches the sidecar. Total per replica: 1.0 vCPU / 2.0 GiB — a valid Consumption combination.

Migration jobs (`Microsoft.App/jobs@2024-03-01`, always deployed — never conditioned on `deployApiApp`):

```bicep
resource migrateSql 'Microsoft.App/jobs@2024-03-01' = {
  name: '${namePrefix}-migrate-sql-${environmentName}'
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${uamiId}': {} } }
  properties: {
    environmentId: env.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 900
      replicaRetryLimit: 0        // fail loudly; the workflow decides whether to re-run
      manualTriggerConfig: { parallelism: 1, replicaCompletionCount: 1 }
      registries: [ { server: acrLoginServer, identity: uamiId } ]
      secrets: [ { name: 'sql-connection-string', keyVaultUrl: '...', identity: uamiId } ]
    }
    template: {
      containers: [ {
        name: 'migrator'
        image: containerImage                       // SAME image as the app
        command: [ 'dotnet' ]
        args: [ '/app/migrator/TelemetryGuard.MigrationRunner.dll' ]
        resources: { cpu: json('0.5'), memory: '1Gi' }
        env: [ { name: 'MIGRATIONS_CONNECTIONSTRING', secretRef: 'sql-connection-string' } ]
      } ]
    }
  }
}
```

The ClickHouse job is identical except: name `…-migrate-clickhouse-…`, args `[ '/app/migrator/TelemetryGuard.MigrationRunner.dll', '--clickhouse' ]`, secret `clickhouse-connection-string`, env `CLICKHOUSE_CONNECTIONSTRING`.

**5.10 `modules/grafana.bicep`** (`condition: deployGrafana`, default **false**) — `Microsoft.Dashboard/grafana@2023-09-01`, SKU `Standard`, system-assigned identity, `zoneRedundancy: 'Disabled'`. Dashboards are **not** provisioned by Bicep: the runbook documents `az grafana dashboard create --definition @ops/grafana/dashboards/traffic-verdicts.json` (and `fraud-sources.json`) plus the ClickHouse datasource, and states plainly that Managed Grafana reaching a VNet-private ClickHouse VM needs either Managed Grafana VNet integration plus an NSG allow rule, or an operator-side tunnel. Default-off keeps the dev bill honest; internal dashboards can also be run from a laptop against the VM through `az vm run-command`/tunnel while the environment is small.

**5.11 `params.dev.json` / `params.prod.json`** — ARM parameters files holding only the stable, non-secret values (`environmentName`, `namePrefix`, `deployGrafana`, `integrationSecretsEnabled`, `opsClientIp`, `customDomainName`, `customDomainCertificateId`). `containerImage`, `otelCollectorImage`, `cloudflareIpv4Ranges`, `deployApiApp`, and the three `@secure()` values come from the workflow as `-p` overrides.

**5.12 `verify-rls.sql`** — the manual Azure-SQL RLS re-proof (the outline's "managed ≠ assumed"). It cannot be automated here: `SqlServerFixture` (DAT-08) is hard-wired to Testcontainers and pointing it at Azure would be both a code change and a cloud call in tests. So ship a standalone script the operator runs once per environment with `sqlcmd -S <fqdn> -d TelemetryGuard -U tgadmin -P … -i infra/azure/verify-rls.sql`:

```sql
-- P2-04: re-prove D11 RLS isolation on Azure SQL. Read-only except for two rows it
-- inserts under the SYSTEM sentinel and deletes at the end. Run once per environment.
DECLARE @A uniqueidentifier = 'aaaa1111-0000-0000-0000-00000000000a';
DECLARE @B uniqueidentifier = 'bbbb2222-0000-0000-0000-00000000000b';

-- dbo.Tenants (migration 0001): Status tinyint (0 = active), RetentionDays int
-- (CHECK 30..180), EnforcementMode tinyint (0 = AutoEnforce, 1 = ApprovalQueue).
-- These are NUMERIC columns — never pass the string 'AutoEnforce' here.
EXEC sp_set_session_context @key = N'TenantId', @value = '00000000-0000-0000-0000-000000000001';  -- SYSTEM
INSERT dbo.Tenants (TenantId, Name, Status, RetentionDays, EnforcementMode)
SELECT @A, N'rls-probe-A', 0, 90, 0 WHERE NOT EXISTS (SELECT 1 FROM dbo.Tenants WHERE TenantId = @A);
INSERT dbo.Tenants (TenantId, Name, Status, RetentionDays, EnforcementMode)
SELECT @B, N'rls-probe-B', 0, 90, 0 WHERE NOT EXISTS (SELECT 1 FROM dbo.Tenants WHERE TenantId = @B);

-- 1. Stamped as A: sees A, never B.
EXEC sp_set_session_context @key = N'TenantId', @value = @A;
SELECT 'expect 1' AS check_name, COUNT(*) AS n FROM dbo.Tenants WHERE TenantId = @A;
SELECT 'expect 0' AS check_name, COUNT(*) AS n FROM dbo.Tenants WHERE TenantId = @B;

-- 2. Unstamped session: sees nothing at all.
EXEC sp_set_session_context @key = N'TenantId', @value = NULL;
SELECT 'expect 0' AS check_name, COUNT(*) AS n FROM dbo.Tenants WHERE TenantId IN (@A, @B);

-- cleanup
EXEC sp_set_session_context @key = N'TenantId', @value = '00000000-0000-0000-0000-000000000001';
DELETE FROM dbo.Tenants WHERE TenantId IN (@A, @B);
```

Note the `CK_Tenants_NotSystemSentinel` constraint: the probe GUIDs above are deliberately not the SYSTEM sentinel. Re-read `TelemetryGuard.Data/migrations/0001_core_schema.sql` before shipping the script if migrations have moved on (`dbo.Tenants` is the authority; do not guess). Record the run and its output in the runbook's sign-off checklist.

### 6. `.github/workflows/deploy.yml` (new, gated)

```yaml
name: Deploy (Azure)

on:
  workflow_dispatch:
    inputs:
      environment:
        description: Target environment
        type: choice
        options: [dev, prod]
        default: dev
  push:
    branches: [main]

concurrency:
  group: deploy-${{ github.event.inputs.environment || 'dev' }}
  cancel-in-progress: false

permissions:
  id-token: write        # OIDC federation — no long-lived cloud secret in the repo
  contents: read

env:
  TG_ENV: ${{ github.event.inputs.environment || 'dev' }}
  IMAGE_NAME: telemetryguard-api
  OTEL_COLLECTOR_UPSTREAM: docker.io/otel/opentelemetry-collector-contrib:0.115.0

jobs:
  preflight:
    name: Are Azure credentials configured?
    runs-on: ubuntu-latest
    outputs:
      enabled: ${{ steps.gate.outputs.enabled }}
    steps:
      # The `secrets` context is NOT available in a job-level `if`, so gate through a
      # step output — the same idiom ci.yml's `sdk` job already uses.
      - id: gate
        env:
          AZURE_CLIENT_ID: ${{ secrets.AZURE_CLIENT_ID }}
          AZURE_TENANT_ID: ${{ secrets.AZURE_TENANT_ID }}
          AZURE_SUBSCRIPTION_ID: ${{ secrets.AZURE_SUBSCRIPTION_ID }}
          AZURE_RESOURCE_GROUP: ${{ secrets.AZURE_RESOURCE_GROUP }}
          TG_SQL_ADMIN_PASSWORD: ${{ secrets.TG_SQL_ADMIN_PASSWORD }}
          TG_CLICKHOUSE_PASSWORD: ${{ secrets.TG_CLICKHOUSE_PASSWORD }}
          TG_BEACON_HMAC_SECRET: ${{ secrets.TG_BEACON_HMAC_SECRET }}
        run: |
          missing=""
          for v in AZURE_CLIENT_ID AZURE_TENANT_ID AZURE_SUBSCRIPTION_ID AZURE_RESOURCE_GROUP \
                   TG_SQL_ADMIN_PASSWORD TG_CLICKHOUSE_PASSWORD TG_BEACON_HMAC_SECRET; do
            [ -n "${!v}" ] || missing="$missing $v"
          done
          if [ -n "$missing" ]; then
            echo "enabled=false" >> "$GITHUB_OUTPUT"
            echo "::notice::Azure deploy skipped — missing secrets:$missing"
          else
            echo "enabled=true" >> "$GITHUB_OUTPUT"
          fi
```

Remaining jobs, each `needs:` the previous and carrying `if: needs.preflight.outputs.enabled == 'true'`:

1. **`build-push`** — `azure/login@v2` (client-id/tenant-id/subscription-id, no client secret) → `az acr login -n $ACR` → `docker build -t $ACR/telemetryguard-api:${{ github.sha }} --build-arg GIT_SHA=${{ github.sha }} .` → `docker push` → also `az acr import --source $OTEL_COLLECTOR_UPSTREAM -t opentelemetry-collector-contrib:0.115.0 -n $ACR --force` (import once so replicas never pull from docker.io and never hit its rate limits). Outputs `image` and `collectorImage`.
2. **`infra`** (deploy **phase A**) — computes the Cloudflare parameter straight from the file that already exists, then deploys everything **except** the app:
   ```bash
   CF=$(grep -v '^#' TelemetryGuard.Api/Edge/cloudflare-ips.txt | grep -v ':' | grep -v '^$' | jq -R . | jq -sc .)
   az deployment group create -g "$RG" -n "tg-$TG_ENV-$GITHUB_RUN_ID-a" \
     -f infra/azure/main.bicep --parameters @infra/azure/params.$TG_ENV.json \
     -p containerImage="$IMAGE" -p otelCollectorImage="$COLLECTOR_IMAGE" \
     -p cloudflareIpv4Ranges="$CF" -p deployApiApp=false \
     -p sqlAdminPassword="$TG_SQL_ADMIN_PASSWORD" \
     -p clickHousePassword="$TG_CLICKHOUSE_PASSWORD" \
     -p beaconHmacSecret="$TG_BEACON_HMAC_SECRET"
   ```
   ARM incremental mode leaves the existing app revision untouched while the jobs are re-pointed at the new image.
3. **`migrate`** — starts both jobs **in order** and waits, because `az containerapp job start` returns immediately:
   ```bash
   run_job () {
     name="$1"
     exec_name=$(az containerapp job start -n "$name" -g "$RG" --query name -o tsv)
     for i in $(seq 1 60); do
       status=$(az containerapp job execution show -n "$name" -g "$RG" \
                  --job-execution-name "$exec_name" --query properties.status -o tsv)
       echo "$name/$exec_name: $status"
       case "$status" in
         Succeeded) return 0 ;;
         Failed|Degraded) echo "::error::$name failed"; return 1 ;;
       esac
       sleep 10
     done
     echo "::error::$name timed out"; return 1
   }
   run_job "$SQL_JOB" && run_job "$CH_JOB"
   ```
   Migrations are the deploy gate: if either job fails, the workflow stops **before** the app rolls out.
4. **`deploy`** (deploy **phase B**) — the same `az deployment group create` with `-p deployApiApp=true`, which creates/updates the Container App to the new image. Single-revision mode means Container Apps shifts traffic only once the new revision passes its probes.
5. **`smoke`** — deliberately does **not** curl the origin directly: ingress is locked to Cloudflare (D13) and poking a GitHub-runner-shaped hole in that list would defeat the point. Instead:
   - `az containerapp revision list -n $APP -g $RG --query "[?properties.active].{n:name,h:properties.healthState,p:properties.provisioningState}" -o table` must show `Healthy`/`Provisioned`;
   - if the optional secret `TG_SMOKE_BASE_URL` (the Cloudflare-proxied hostname) is set, `curl -fsS "$TG_SMOKE_BASE_URL/healthz"` and `curl -fsSI "$TG_SMOKE_BASE_URL/sdk/tg.js"` — through Cloudflare, exactly as a tenant page would.

Rollback is `az containerapp update -n $APP -g $RG --image $ACR/telemetryguard-api:<previous-sha>`; the runbook records it, along with the rule that **migrations are not rolled back** (DbUp is forward-only — an incompatible schema change must ship as a new additive migration).

### 7. `.github/workflows/ci.yml` (modify — two new jobs, nothing existing changes)

Append these; they need **no secrets** and therefore run on every PR, including from forks. Do not touch the `build-test`, `integration` or `sdk` jobs.

```yaml
  image:
    name: API container image (Dockerfile)
    runs-on: ubuntu-latest
    timeout-minutes: 25
    steps:
      - uses: actions/checkout@v4

      - name: Build image
        run: docker build -t telemetryguard-api:ci --build-arg GIT_SHA=${{ github.sha }} .

      - name: SDK bundle is inside the image (SDK-08 / D22)
        run: |
          docker run --rm telemetryguard-api:ci ls -1 /app/wwwroot/sdk > files.txt
          cat files.txt
          grep -qx 'tg.js'      files.txt || { echo "::error::tg.js missing from image"; exit 1; }
          grep -qx 'tg.js.map'  files.txt || { echo "::error::tg.js.map missing from image"; exit 1; }
          grep -qE '^tg-[0-9]'  files.txt || { echo "::error::version-pinned bundle missing from image"; exit 1; }

      - name: Migrator is inside the same image (DAT-01)
        run: |
          set +e
          out=$(docker run --rm telemetryguard-api:ci dotnet /app/migrator/TelemetryGuard.MigrationRunner.dll 2>&1)
          code=$?
          echo "$out"
          [ "$code" = "2" ] || { echo "::error::expected exit 2 (missing MIGRATIONS_CONNECTIONSTRING), got $code"; exit 1; }

      - name: Container starts and answers /healthz
        run: |
          docker run -d --name tg-smoke -p 18080:8080 \
            -e Beacon__HmacSecret=ci-only-not-a-real-secret-0123456789 \
            telemetryguard-api:ci
          ok=0
          for i in $(seq 1 30); do
            if curl -fsS http://localhost:18080/healthz >/dev/null 2>&1; then ok=1; break; fi
            sleep 2
          done
          docker logs tg-smoke
          docker rm -f tg-smoke
          [ "$ok" = "1" ] || { echo "::error::/healthz never returned 200"; exit 1; }

  iac:
    name: Bicep compile (offline — no Azure auth)
    runs-on: ubuntu-latest
    timeout-minutes: 10
    steps:
      - uses: actions/checkout@v4
      - name: Install Bicep
        run: az bicep install
      # No `az login` in this job, by design: compiling Bicep needs no credentials, and
      # CI must never make a real cloud call.
      - name: Compile templates (linter rules are errors via infra/azure/bicepconfig.json)
        run: az bicep build --file infra/azure/main.bicep --stdout > /dev/null
```

### 8. `.gitignore` (modify — two lines)

```gitignore
# Azure IaC: compiled ARM output and operator-local parameter files are never committed
infra/azure/**/*.json.arm
infra/azure/*.local.json
```

(The committed `params.*.json` and `bicepconfig.json` must keep matching nothing above — hence the deliberate `.json.arm` suffix convention for any local `az bicep build --outfile` output.)

### 9. `doc/runbooks/azure-deployment.md` (new)

Same voice as `doc/runbooks/cloudflare-fronting.md`: operator-facing, ordered, honest about what you actually get. Required sections:

1. **Compose → Azure mapping** (keep verbatim):

   | Compose service | Azure target | Notes |
   |---|---|---|
   | *(none — API runs on the host)* | Container App `tguard-api-<env>` | 1 replica, ingress 8080, Cloudflare-only IP allow-list, OTel sidecar |
   | migration runner | Container Apps **jobs** `…-migrate-sql`, `…-migrate-clickhouse` | same image, `dotnet /app/migrator/TelemetryGuard.MigrationRunner.dll [--clickhouse]`; run **before** the app rolls |
   | `mssql` | Azure SQL Database, `GP_S_Gen5` serverless, auto-pause disabled | `ConnectionStrings__Main`; migrations and RLS unchanged (D8/D10/D11) |
   | `redis` | Azure Cache for Redis (Basic C0 dev / Standard C1 prod), TLS-only 6380 | `ConnectionStrings__Redis`; connection-string change only (D5) |
   | `clickhouse` | Ubuntu VM in `snet-data`, no public IP, `clickhouse/clickhouse-server:24.8` on a Premium SSD | `Analytics__ClickHouse__ConnectionString`; provider code unchanged (D6/D7) |
   | `grafana` | Azure Managed Grafana (optional, `deployGrafana=false` by default) | dashboards imported from `ops/grafana/dashboards/*.json` |
   | OTLP collector | OTel Collector sidecar → Application Insights | env-var only, per API-01 (`OTEL_EXPORTER_OTLP_ENDPOINT`) |
   | *(logs)* | Log Analytics `ContainerAppConsoleLogs` | the app's `AddJsonConsole` output needs no agent |
   | *(none)* | **`TelemetryGuard.Portal` — NOT deployed by this task** | P2-03's operator UI is a separate Razor Pages web app needing its own image, Container App and hostname (deliberately *not* behind the tracker origin's Cloudflare-only IP allow-list). Run it on a host with `dotnet run --project TelemetryGuard.Portal` until the follow-up deployment task lands. |

2. **Edge chain**: Cloudflare (DNS proxied, Full (strict), Worker, cache-bypass rules — see the INT-05 runbook) → Container Apps ingress. **No Front Door.** Origin lockdown is the `ipSecurityRestrictions` allow-list, regenerated from `TelemetryGuard.Api/Edge/cloudflare-ips.txt` on every deploy; after running `scripts/update-cloudflare-ips.sh`, redeploy so both the app's embedded list and ingress agree.
3. **First-deploy order**: create RG → grant the OIDC identity **Owner on the resource group** (Contributor alone cannot create the role assignments in steps 5.4/5.5) → set GitHub secrets → run the workflow → wait for the ClickHouse VM to finish cloud-init (`az vm run-command invoke … --command-id RunShellScript --scripts "docker ps"`) → migration jobs → app.
4. **Custom domain + Cloudflare binding** (the step most likely to bite): the Worker forwards the request with the tenant-facing `Host` header, so the Container App must have that hostname bound or ingress returns 404. Procedure: temporarily set the DNS record to **DNS-only (grey cloud)** → add the `asuid.<host>` TXT record → bind the custom domain with a **Cloudflare Origin CA certificate** uploaded as a `managedEnvironments/certificates` resource (Full (strict) accepts Origin CA, and a BYO cert avoids managed-certificate renewal failing behind the proxy) → re-enable the orange cloud → redeploy with `customDomainName`/`customDomainCertificateId` set.
5. **Secrets**: what Bicep writes to Key Vault vs. what the operator seeds with `az keyvault secret set` (Turnstile, Google Ads, Meta), and the `integrationSecretsEnabled=true` flip. State clearly that **until Turnstile keys are seeded, INT-01 returns "unavailable" (fail-closed) and the 31–70 challenge band cannot complete**, and that both exclusion-sync workers stay in `EffectiveDryRun` until their credentials are seeded *and* `DryRun` is explicitly set false.
6. **Tenant provisioning in a deployed environment**: `az containerapp job start -n <sql-job> -g <rg> --command dotnet --args "/app/migrator/TelemetryGuard.MigrationRunner.dll provision create-tenant --tenant-id … --name … --retention-days 90 --enforcement-mode AutoEnforce"` (requires an `az` CLI new enough to support start-time overrides; the fallback is `az containerapp job update` with the args, run, then revert). Never hand-write rows.
7. **Rollback**: image rollback command; **migrations are forward-only**; ClickHouse VM rollback = restore the data disk from a snapshot.
8. **Cost notes** — a table of the exact SKUs deployed with an *indicative* monthly figure and the loud caveat that these are unverified estimates that drift by region and over time; price them in the Azure calculator before quoting anyone. Include the two honest corrections: Azure SQL **never auto-pauses** in this design (decision 7), and the API app **cannot scale to zero** because the workers live in it (decision 5).
9. **Sign-off checklist**: `/healthz` 200 through Cloudflare; `/ready` 200 (all three dependencies); `/sdk/tg.js` 200 with `max-age=300` and `/sdk/tg-<v>.js` `immutable`; `dbo.SchemaVersions` contains **one row per `.sql` file in `TelemetryGuard.Data/migrations/` at the deployed commit** (compare the count against `ls TelemetryGuard.Data/migrations/*.sql | wc -l` rather than naming numbers — sibling tasks add scripts); ClickHouse `tg_schema_migrations` populated; App Insights shows request traces without a code change; `X-TG-ASN` observed on origin requests (INT-05 acceptance re-run); `verify-rls.sql` output all-expected; no secret value present in any deployment output or workflow log.

## Files to create or modify

**Create**
- `Dockerfile`, `.dockerignore` (repo root)
- `infra/azure/bicepconfig.json`, `infra/azure/main.bicep`
- `infra/azure/modules/{network,observability,registry,keyvault,sql,redis,clickhouse-vm,containerapps,grafana}.bicep`
- `infra/azure/cloud-init/clickhouse.yaml`
- `infra/azure/params.dev.json`, `infra/azure/params.prod.json`
- `infra/azure/verify-rls.sql`
- `.github/workflows/deploy.yml`
- `doc/runbooks/azure-deployment.md`

**Modify**
- `.github/workflows/ci.yml` — append the `image` and `iac` jobs only; leave `build-test`, `integration`, `sdk` byte-for-byte unchanged (including the existing SDK-08 comment that points at this task).
- `.gitignore` — the two IaC lines in step 8.

**Explicitly NOT modified** (verify with `git status` before committing): `TelemetryGuard.sln` (a Dockerfile is not a project), `TelemetryGuard.Api/Program.cs`, any `appsettings*.json`, any `.csproj`, any `TelemetryGuard.Data/migrations/*.sql`, `docker-compose.yml`, `scripts/*`, `doc/plan.md`, `doc/spec.md`, `CLAUDE.md`.

## Acceptance criteria

- `docker build -t telemetryguard-api:local .` succeeds **under podman** from the repo root with zero warnings-as-errors failures, and `docker build` a second time is materially faster (restore layer cached).
- `docker run --rm telemetryguard-api:local ls -1 /app/wwwroot/sdk` prints exactly `tg-<version>.js`, `tg-<version>.js.map`, `tg.js`, `tg.js.map`. Deleting the `COPY --from=sdk … TelemetryGuard.Sdk/dist/` line makes the build **fail** (the `cp`/`test -f` guard fires) — verify this once, then restore the line.
- The image contains no Node runtime: `docker run --rm telemetryguard-api:local sh -c 'command -v node || echo none'` prints `none` (D1).
- `docker run … -e Beacon__HmacSecret=<32+ chars>` reaches `/healthz` 200 within 30 s with no SQL/Redis/ClickHouse present; `/ready` returns 503; `/sdk/tg.js` returns 200 `text/javascript` with `max-age=300`, `/sdk/tg-<version>.js` with `immutable` (D22 cache contract preserved end-to-end).
- Omitting `Beacon__HmacSecret` makes the container exit non-zero with the API-04 validation message — the fail-fast contract survives containerization.
- `dotnet /app/migrator/TelemetryGuard.MigrationRunner.dll` inside the image exits `2` with the missing-env-var message; with `--clickhouse` it exits `2` naming `CLICKHOUSE_CONNECTIONSTRING`. Same image, both entrypoints.
- `az bicep build --file infra/azure/main.bicep --stdout` succeeds offline with **no `az login`** and no linter errors; every module is reachable from `main.bicep` (no orphan files).
- `grep -rn "azurecontainerapps.io\|database.windows.net\|redis.cache.windows.net" infra/azure/*.bicep infra/azure/modules/*.bicep` finds only values derived from resource properties (`.properties.fullyQualifiedDomainName`, `.properties.hostName`, `environment().suffixes.*`) — no hardcoded endpoints (the `no-hardcoded-env-urls` rule enforces this).
- `main.bicep` has **no output** whose value is a secret; `sqlAdminPassword`, `clickHousePassword`, `beaconHmacSecret` are all `@secure()` with **no default**.
- The API Container App declares `minReplicas: 1, maxReplicas: 1` with the single-instance rationale in a comment; the migration jobs declare `triggerType: 'Manual'` and share `containerImage` with the app (grep proves one image parameter feeds all three).
- Ingress declares one `Allow` rule per IPv4 CIDR in `TelemetryGuard.Api/Edge/cloudflare-ips.txt` and nothing else; `Edge__Provider=Cloudflare` is set on the app.
- `.github/workflows/deploy.yml` with no secrets configured: the `preflight` job runs, emits the skip notice, and every downstream job is skipped — **zero `az` invocations, zero failures**. Prove it by pushing to a fork or by reading the run summary of the first push to a repo without the secrets.
- `.github/workflows/ci.yml` still contains the original three jobs unmodified, plus `image` and `iac`; neither new job references `secrets.*`. (If P2-05 landed first the file also carries a `kusto-contracts` job — leave it alone; "unmodified" refers to `build-test`/`integration`/`sdk`.)
- `git status` shows **no** change under `TelemetryGuard.Portal/`, `TelemetryGuard.Analytics.Kusto/`, `TelemetryGuard.Data/migrations/`, or any `*.cs`/`*.csproj`/`appsettings*.json` — the zero-application-code-change rule, verified rather than assumed.
- The runbook contains the mapping table, the custom-domain/Cloudflare procedure, the rollback commands, the cost caveats, and the sign-off checklist; `doc/runbooks/azure-deployment.md` links to `cloudflare-fronting.md` rather than duplicating it.

## Testing

- **No new unit, integration or contract tests.** This task adds no C#, so every pre-existing test must still pass **unchanged** — `dotnet build TelemetryGuard.sln` plus the three `dotnet test` runs from CLAUDE.md. Do **not** assert an absolute test count in the PR description or anywhere else: P2-01/P2-02/P2-03/P2-05 add tests in parallel, and the CLAUDE.md figures (478 unit / 123 integration / 8 contract) are a snapshot of the pre-Phase-2 tree, not a gate.
- **Image tests are the CI `image` job** (step 7): bundle presence, migrator presence and exit code, container liveness. They run in-process on the runner with no cloud dependency.
- **IaC validation is `az bicep build`** — a pure compile. `az deployment group what-if`/`validate` require authentication and therefore **must not** appear in any ungated job; they belong in the runbook as an optional operator step before a risky change.
- **The Azure-SQL RLS re-proof is manual** (`infra/azure/verify-rls.sql`, step 5.12) and recorded in the runbook checklist. Do not "fix" this by adding an Azure connection string to `SqlServerFixture`: that would make the integration suite depend on a cloud account and would break every offline run.
- **Nothing in this task may call Azure during tests or ungated CI.** If a step needs `az login`, it belongs behind the `preflight` gate.

## Out of scope / guardrails

- **Cloudflare stays outermost (D13).** No Front Door, no Front Door WAF, no swapping the edge. Losing JA3/JA4 silently degrades `tls_ua_mismatch` to null for every tenant — degraded-not-broken by design, but never an intentional trade.
- **Same-image promotion.** The image built and pushed from a commit is the artifact that deploys; no Azure-specific build fork, no rebuilding at deploy time, no moving tags (`:latest`) in the deployment parameter.
- **No application-code changes.** No `.cs`, no `appsettings*.json`, no `.csproj`, no migration, no `TelemetryGuard.sln` edit. If the deployment appears to require one, escalate instead of editing — the only near-miss (publish not picking up `wwwroot/sdk`) is solved inside the Dockerfile on purpose.
- **Do not raise `maxReplicas` above 1.** `VerdictFinalizerService` claims grace-expired sessions with `ZREM` and documents itself as single-instance MVP; `RollupService` would double-run. Multi-instance claiming (an atomic Lua pop) is an explicit backlog item, not this task.
- **No Kusto** (P2-05), **no Kafka/Event Hubs** (D12 — only when volume demands), **no AKS** (D16 — Container Apps must be measurably outgrown first), **no PostgreSQL** (D23 closed it).
- **The customer portal (P2-03) is out of scope, and this is a two-way contract.** This task deploys the API image only. It adds no portal image, no second Container App, no portal hostname, no Data Protection key-ring store, and no portal row to any Bicep module — even if `TelemetryGuard.Portal/` already exists in your tree. P2-03 correspondingly ships no Dockerfile, no compose service and no Bicep, and names a **separate follow-up task** (not this one) as the owner of portal deployment. If you feel pressure to add it here, that is the follow-up task asking to be written — do not absorb it.
- **The model registry (P2-02) changes nothing here.** P2-02 makes `Scoring:ModelSource=Registry` the default; with an empty `dbo.ModelRegistry` — which is what a fresh environment has — its bootstrap resolves to the pure heuristic and loads no model, exactly as this image behaves today. Do **not** add `Scoring__*` env vars, a model volume/blob mount, or a registry-seeding deploy step.
- **Backlog items stay in the backlog**: no IP-reputation store, no per-tenant Turnstile keys (platform-level keys only, INT-01), no SSO/user accounts, no multi-instance session claiming, no elastic-pool per-tenant isolation, no ONNX trajectory model.
- **No server-side Python or Node (D1).** npm/esbuild exist only in the discarded build stage; every deploy step is bash + `az` + Bicep. Do not introduce azd, Terraform, Pulumi, or a Node-based deploy script.
- **Analytics stays abstracted by intent (D7)**: the ClickHouse move is a connection string. Do not add a query layer, a second provider, or engine-conditional code anywhere in this task.
- **Secrets never leave Key Vault**: no secret in a Bicep output, in `params.*.json`, in a workflow log, in a container image layer, or in an `az` command echoed by `set -x`. `@secure()` on every sensitive parameter.
- **Rules only raise scores; NaN ≠ 0; ML stays behind `IScorer`.** Nothing in this task may set `Scoring:Scorer`, `Scoring:Mode`, or `Scoring:ModelPath` to anything other than the shipped defaults — promoting a model is a human decision made in configuration, not something a deployment template does.
- **Migrations are forward-only and run as a deploy gate.** Never run DbUp from the API process, never roll a schema back, never edit an applied migration.
