---
id: FND-01
title: Solution and project scaffold
phase: 0
workstream: foundation
depends_on: []
size: M
spec_refs: [D1, D3, D7, D9, D10, "§8 Solution shape"]
detail_level: full
---

# FND-01: Solution and project scaffold

## Objective
Create the complete .NET solution skeleton for TelemetryGuard: `TelemetryGuard.sln`, every project from spec §8 (except the deferred Kusto provider), one additional cross-cutting project `TelemetryGuard.Core`, a `Directory.Build.props` with strict compiler settings, all project-to-project references, all Phase-0/Phase-1 NuGet packages pinned to versions, and a `.gitignore` covering .NET and Node. After this task, `dotnet build` and `dotnet test` succeed on a clean checkout with zero code beyond templates.

## Spec context (self-contained)
- Backend is **.NET (C#) end-to-end** (spec D1). No Python, no Node server-side — the only TypeScript in the repo is the browser SDK (`TelemetryGuard.Sdk`), compiled by esbuild to a static IIFE bundle (D2). At scaffold time the SDK is an **empty placeholder directory only** — the npm project is created later by task SDK-01.
- Target framework: **net8.0** (spec D3 allows ASP.NET Core 8/9; 8 is chosen because it is LTS).
- Data access is **Dapper over Microsoft.Data.SqlClient, never EF Core** (D9). Migrations are numbered SQL scripts run by **DbUp** (D10), so `dbup-sqlserver` goes into `TelemetryGuard.Data`.
- Analytics is abstracted **by intent, not by query** (D7): `TelemetryGuard.Analytics.Abstractions` holds only interfaces + DTOs; `TelemetryGuard.Analytics.ClickHouse` is the only provider implemented now. **`TelemetryGuard.Analytics.Kusto` is deferred — do NOT create it** (it belongs to a "later" phase, task P2-05).
- The MVP ships **without a trained ML model** (D18): do **not** add `Microsoft.ML` / `Microsoft.ML.LightGbm` packages now. They are Phase-1.5-only (task RSK-08). Note them in a comment, nothing more.
- Solution shape from spec §8 (projects at repo root, tests under `tests/`):
  - `TelemetryGuard.Api` — ASP.NET Core: tracker, ingestion, decision, tenant middleware
  - `TelemetryGuard.RiskEngine` — feature extraction, rules, scoring
  - `TelemetryGuard.RiskEngine.Contracts` — FraudFeatureVector, verdict DTOs
  - `TelemetryGuard.Analytics.Abstractions` — IEventSink, IAnalyticsQueries, DTOs
  - `TelemetryGuard.Analytics.ClickHouse` — provider impl + `/schema/*.sql`
  - `TelemetryGuard.Data` — Dapper repositories, TenantConnectionFactory, `/migrations/*.sql`
  - `TelemetryGuard.Integrations` — Google Ads client, Meta HttpClient wrapper, Turnstile verify
  - `TelemetryGuard.Sdk` — TypeScript source (placeholder dir for now)
  - `tests/TelemetryGuard.Tests.Contracts`, `tests/TelemetryGuard.Tests.Integration`, `tests/TelemetryGuard.Tests.Unit`
- **PLAN ADDITION (candidate decision D24):** `TelemetryGuard.Core` is a new cross-cutting project **not present in spec §8**. Rationale: `ITenantContext`, `TenantId`, and `IClock` (task FND-04) are consumed by Api, Data, Analytics, and RiskEngine alike, and the spec places them nowhere; without a shared base project they would be duplicated or force wrong-direction references. Record this as a candidate amendment "D24 — TelemetryGuard.Core cross-cutting project" when the spec is next amended; do not edit `doc/spec.md` history in this task.

## Prerequisites
None. The repository contains only `CLAUDE.md` and `doc/`. Run all commands from the repo root: `/home/shafqat/git/shafqat/telemtry-guard`.

## Implementation steps

1. **Create `.gitignore` at the repo root** with this exact content:

```gitignore
# .NET
bin/
obj/
artifacts/
TestResults/
*.user
*.suo
.vs/
.vscode/
!.vscode/extensions.json
.idea/
*.DotSettings.user
*.trx
coverage/
*.coverage
BenchmarkDotNet.Artifacts/

# Local secrets / env
.env
appsettings.*.local.json
*.pfx

# Node / SDK
node_modules/
TelemetryGuard.Sdk/dist/
npm-debug.log*
.npm/

# OS
.DS_Store
Thumbs.db
```

2. **Create `Directory.Build.props` at the repo root** (applies to every project; the per-project `<TargetFramework>` stays in each csproj):

```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <LangVersion>latest</LangVersion>
    <InvariantGlobalization>false</InvariantGlobalization>
  </PropertyGroup>
  <!-- NOTE: Microsoft.ML / Microsoft.ML.LightGbm are Phase-1.5-only (task RSK-08, spec D18).
       Do not add them before the label pipeline exists. -->
</Project>
```

3. **Create the solution and projects.** Run exactly (from repo root):

```bash
dotnet new sln -n TelemetryGuard

dotnet new web      -n TelemetryGuard.Api                    -o TelemetryGuard.Api                    -f net8.0
dotnet new classlib -n TelemetryGuard.Core                   -o TelemetryGuard.Core                   -f net8.0
dotnet new classlib -n TelemetryGuard.RiskEngine             -o TelemetryGuard.RiskEngine             -f net8.0
dotnet new classlib -n TelemetryGuard.RiskEngine.Contracts   -o TelemetryGuard.RiskEngine.Contracts   -f net8.0
dotnet new classlib -n TelemetryGuard.Analytics.Abstractions -o TelemetryGuard.Analytics.Abstractions -f net8.0
dotnet new classlib -n TelemetryGuard.Analytics.ClickHouse   -o TelemetryGuard.Analytics.ClickHouse   -f net8.0
dotnet new classlib -n TelemetryGuard.Data                   -o TelemetryGuard.Data                   -f net8.0
dotnet new classlib -n TelemetryGuard.Integrations           -o TelemetryGuard.Integrations           -f net8.0

dotnet new xunit -n TelemetryGuard.Tests.Unit        -o tests/TelemetryGuard.Tests.Unit        -f net8.0
dotnet new xunit -n TelemetryGuard.Tests.Integration -o tests/TelemetryGuard.Tests.Integration -f net8.0
dotnet new xunit -n TelemetryGuard.Tests.Contracts   -o tests/TelemetryGuard.Tests.Contracts   -f net8.0

dotnet sln TelemetryGuard.sln add \
  TelemetryGuard.Api/TelemetryGuard.Api.csproj \
  TelemetryGuard.Core/TelemetryGuard.Core.csproj \
  TelemetryGuard.RiskEngine/TelemetryGuard.RiskEngine.csproj \
  TelemetryGuard.RiskEngine.Contracts/TelemetryGuard.RiskEngine.Contracts.csproj \
  TelemetryGuard.Analytics.Abstractions/TelemetryGuard.Analytics.Abstractions.csproj \
  TelemetryGuard.Analytics.ClickHouse/TelemetryGuard.Analytics.ClickHouse.csproj \
  TelemetryGuard.Data/TelemetryGuard.Data.csproj \
  TelemetryGuard.Integrations/TelemetryGuard.Integrations.csproj \
  tests/TelemetryGuard.Tests.Unit/TelemetryGuard.Tests.Unit.csproj \
  tests/TelemetryGuard.Tests.Integration/TelemetryGuard.Tests.Integration.csproj \
  tests/TelemetryGuard.Tests.Contracts/TelemetryGuard.Tests.Contracts.csproj
```

Delete the template `Class1.cs` from every classlib project (empty class libraries compile fine).

4. **Add project references.** The dependency direction is: everything may reference `Core`; nothing references `Api`. Run exactly:

```bash
dotnet add TelemetryGuard.RiskEngine.Contracts   reference TelemetryGuard.Core/TelemetryGuard.Core.csproj
dotnet add TelemetryGuard.RiskEngine             reference TelemetryGuard.Core/TelemetryGuard.Core.csproj TelemetryGuard.RiskEngine.Contracts/TelemetryGuard.RiskEngine.Contracts.csproj
dotnet add TelemetryGuard.Analytics.Abstractions reference TelemetryGuard.Core/TelemetryGuard.Core.csproj
dotnet add TelemetryGuard.Analytics.ClickHouse   reference TelemetryGuard.Core/TelemetryGuard.Core.csproj TelemetryGuard.Analytics.Abstractions/TelemetryGuard.Analytics.Abstractions.csproj
dotnet add TelemetryGuard.Data                   reference TelemetryGuard.Core/TelemetryGuard.Core.csproj
dotnet add TelemetryGuard.Integrations           reference TelemetryGuard.Core/TelemetryGuard.Core.csproj
dotnet add TelemetryGuard.Api reference \
  TelemetryGuard.Core/TelemetryGuard.Core.csproj \
  TelemetryGuard.Data/TelemetryGuard.Data.csproj \
  TelemetryGuard.RiskEngine/TelemetryGuard.RiskEngine.csproj \
  TelemetryGuard.RiskEngine.Contracts/TelemetryGuard.RiskEngine.Contracts.csproj \
  TelemetryGuard.Analytics.Abstractions/TelemetryGuard.Analytics.Abstractions.csproj \
  TelemetryGuard.Analytics.ClickHouse/TelemetryGuard.Analytics.ClickHouse.csproj \
  TelemetryGuard.Integrations/TelemetryGuard.Integrations.csproj

dotnet add tests/TelemetryGuard.Tests.Unit reference \
  TelemetryGuard.Core/TelemetryGuard.Core.csproj \
  TelemetryGuard.RiskEngine/TelemetryGuard.RiskEngine.csproj \
  TelemetryGuard.RiskEngine.Contracts/TelemetryGuard.RiskEngine.Contracts.csproj \
  TelemetryGuard.Analytics.Abstractions/TelemetryGuard.Analytics.Abstractions.csproj \
  TelemetryGuard.Data/TelemetryGuard.Data.csproj \
  TelemetryGuard.Integrations/TelemetryGuard.Integrations.csproj \
  TelemetryGuard.Api/TelemetryGuard.Api.csproj

dotnet add tests/TelemetryGuard.Tests.Integration reference \
  TelemetryGuard.Core/TelemetryGuard.Core.csproj \
  TelemetryGuard.Data/TelemetryGuard.Data.csproj \
  TelemetryGuard.Analytics.Abstractions/TelemetryGuard.Analytics.Abstractions.csproj \
  TelemetryGuard.Analytics.ClickHouse/TelemetryGuard.Analytics.ClickHouse.csproj \
  TelemetryGuard.RiskEngine/TelemetryGuard.RiskEngine.csproj \
  TelemetryGuard.Api/TelemetryGuard.Api.csproj

dotnet add tests/TelemetryGuard.Tests.Contracts reference \
  TelemetryGuard.Core/TelemetryGuard.Core.csproj \
  TelemetryGuard.Analytics.Abstractions/TelemetryGuard.Analytics.Abstractions.csproj \
  TelemetryGuard.Analytics.ClickHouse/TelemetryGuard.Analytics.ClickHouse.csproj
```

Reference-edge summary (for review): `Core` → nothing; `RiskEngine.Contracts` → Core; `RiskEngine` → Core, RiskEngine.Contracts; `Analytics.Abstractions` → Core; `Analytics.ClickHouse` → Core, Analytics.Abstractions; `Data` → Core; `Integrations` → Core; `Api` → all of the above except tests; test projects reference what they test.

5. **Add NuGet packages** (versions pinned; if NuGet reports an exact version unavailable, take the newest patch of the same minor and note it in the commit message):

```bash
# Data layer (Dapper, SqlClient, DbUp — spec D9/D10)
dotnet add TelemetryGuard.Data package Dapper                    --version 2.1.35
dotnet add TelemetryGuard.Data package Microsoft.Data.SqlClient  --version 5.2.2
dotnet add TelemetryGuard.Data package dbup-sqlserver            --version 5.0.41

# ClickHouse provider (spec D6/D7)
dotnet add TelemetryGuard.Analytics.ClickHouse package ClickHouse.Client --version 7.2.2

# Risk engine: GeoIP enrichment + Redis velocity (spec D3/D5)
dotnet add TelemetryGuard.RiskEngine package MaxMind.GeoIP2       --version 5.2.0
dotnet add TelemetryGuard.RiskEngine package StackExchange.Redis  --version 2.8.16

# Test projects: align xunit stack and add Testcontainers
for p in tests/TelemetryGuard.Tests.Unit tests/TelemetryGuard.Tests.Integration tests/TelemetryGuard.Tests.Contracts; do
  dotnet add $p package xunit                     --version 2.9.2
  dotnet add $p package xunit.runner.visualstudio --version 2.8.2
  dotnet add $p package Microsoft.NET.Test.Sdk    --version 17.11.1
done

dotnet add tests/TelemetryGuard.Tests.Integration package Testcontainers.MsSql      --version 3.10.0
dotnet add tests/TelemetryGuard.Tests.Integration package Testcontainers.Redis      --version 3.10.0
dotnet add tests/TelemetryGuard.Tests.Integration package Testcontainers.ClickHouse --version 3.10.0
dotnet add tests/TelemetryGuard.Tests.Contracts   package Testcontainers.ClickHouse --version 3.10.0
```

Do NOT add: `Microsoft.ML`, `Microsoft.ML.LightGbm` (Phase 1.5, RSK-08), `Google.Ads.GoogleAds` (Phase 1.5, INT-03), any EF Core package (forbidden, D9), `Npgsql` (PostgreSQL dropped, D23).

6. **SDK placeholder.** Create directory `TelemetryGuard.Sdk/` containing only a `README.md`:

```markdown
# TelemetryGuard.Sdk

TypeScript browser SDK (spec D2). The npm project (package.json, esbuild config, src/)
is created by task SDK-01. Until then this directory is an intentional placeholder so
the solution shape matches doc/spec.md §8. CI's sdk job no-ops while package.json is absent.
```

Do not create `package.json` here — CI (FND-03) gates on its existence.

7. **Placeholder schema/migration folders** so later tasks have stable paths: create empty directories with a `.gitkeep` file in each:
   - `TelemetryGuard.Data/migrations/.gitkeep` (DbUp scripts land here — DAT-01/DAT-02)
   - `TelemetryGuard.Analytics.ClickHouse/schema/.gitkeep` (ClickHouse DDL lands here — ANA-02)

8. **Verify:** `dotnet build TelemetryGuard.sln -c Release` succeeds with zero warnings (TreatWarningsAsErrors is on), and `dotnet test TelemetryGuard.sln -c Release` passes (template tests).

## Files to create or modify
- `/home/shafqat/git/shafqat/telemtry-guard/.gitignore`
- `/home/shafqat/git/shafqat/telemtry-guard/Directory.Build.props`
- `/home/shafqat/git/shafqat/telemtry-guard/TelemetryGuard.sln`
- `/home/shafqat/git/shafqat/telemtry-guard/TelemetryGuard.Api/` (project, `Program.cs` from template)
- `/home/shafqat/git/shafqat/telemtry-guard/TelemetryGuard.Core/`
- `/home/shafqat/git/shafqat/telemtry-guard/TelemetryGuard.RiskEngine/`
- `/home/shafqat/git/shafqat/telemtry-guard/TelemetryGuard.RiskEngine.Contracts/`
- `/home/shafqat/git/shafqat/telemtry-guard/TelemetryGuard.Analytics.Abstractions/`
- `/home/shafqat/git/shafqat/telemtry-guard/TelemetryGuard.Analytics.ClickHouse/` (+ `schema/.gitkeep`)
- `/home/shafqat/git/shafqat/telemtry-guard/TelemetryGuard.Data/` (+ `migrations/.gitkeep`)
- `/home/shafqat/git/shafqat/telemtry-guard/TelemetryGuard.Integrations/`
- `/home/shafqat/git/shafqat/telemtry-guard/TelemetryGuard.Sdk/README.md`
- `/home/shafqat/git/shafqat/telemtry-guard/tests/TelemetryGuard.Tests.Unit/`
- `/home/shafqat/git/shafqat/telemtry-guard/tests/TelemetryGuard.Tests.Integration/`
- `/home/shafqat/git/shafqat/telemtry-guard/tests/TelemetryGuard.Tests.Contracts/`

## Acceptance criteria
- `dotnet build TelemetryGuard.sln -c Release` exits 0 with no warnings.
- `dotnet test TelemetryGuard.sln -c Release` exits 0.
- `dotnet sln TelemetryGuard.sln list` shows exactly the 11 projects listed above; no `TelemetryGuard.Analytics.Kusto` project exists.
- `grep -ri "entityframework" --include=*.csproj .` returns nothing; `grep -ri "Microsoft.ML" --include=*.csproj .` returns nothing.
- Every csproj targets `net8.0`; `Directory.Build.props` sets Nullable, ImplicitUsings, TreatWarningsAsErrors, LangVersion=latest.
- `TelemetryGuard.Sdk/` exists with README only (no package.json).
- `git status` after `dotnet build` shows no `bin/`/`obj/` files staged (gitignore works).

## Testing
No new tests. The template xunit test in each test project must run green via `dotnet test`. This proves the reference graph and package restore are sound — which is the deliverable.

## Out of scope / guardrails
- **No EF Core, ever** — Dapper only (D9). Do not add any `Microsoft.EntityFrameworkCore.*` package.
- **No ML packages** — heuristic scorer ships first (D18); `Microsoft.ML`/`Microsoft.ML.LightGbm` arrive only in RSK-08 (Phase 1.5).
- **No Kusto project** — deferred (D6/D7, P2-05). No generic cross-engine query layer either.
- **No server-side Python/Node** — the SDK dir stays a placeholder; no package.json, no node tooling in this task.
- Do not write any application code (no ITenantContext — that is FND-04; no endpoints — API-01; no schema — DAT-02/ANA-02).
- Do not create docker-compose.yml (FND-02) or CI workflow (FND-03).
- `TelemetryGuard.Core` is a flagged plan addition (candidate D24) — mention it in the commit message; do not edit `doc/spec.md`.
