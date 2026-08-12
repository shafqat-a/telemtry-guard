---
id: FND-03
title: CI pipeline (GitHub Actions)
phase: 0
workstream: foundation
depends_on: [FND-01]
size: M
spec_refs: [D2, D9, D10, "§9 Testing strategy"]
detail_level: full
---

# FND-03: CI pipeline (GitHub Actions)

## Objective
Add a single GitHub Actions workflow (`.github/workflows/ci.yml`) with three jobs: **build-test** (restore/build/unit tests with NuGet caching), **integration** (Testcontainers-based integration + analytics contract tests, which work on `ubuntu-latest` because Docker is preinstalled), and **sdk** (Node 20, `npm ci`, build, gzip bundle-size gate) that self-guards to a no-op until `TelemetryGuard.Sdk/package.json` exists. Triggers on pull requests and pushes to `main`.

## Spec context (self-contained)
- The test strategy this pipeline must execute (spec §9):
  - **Repository integration tests** — Testcontainers spins up SQL Server, DbUp applies migrations, every Dapper repository method executes once, including a deliberate cross-tenant read that must return empty (RLS proof). These live in `tests/TelemetryGuard.Tests.Integration` (populated by DAT-08 and later tasks; the project exists now and passes trivially with zero tests).
  - **Analytics contract tests** — one shared xUnit suite run against each analytics provider (ClickHouse via Testcontainers now). These live in `tests/TelemetryGuard.Tests.Contracts` (populated by ANA-06).
  - **Unit tests** — `tests/TelemetryGuard.Tests.Unit`.
- The **SDK bundle must stay ~20–30 KB gzipped** (spec D2: plain TS, no framework, static IIFE bundle). The CI size gate enforces a hard ceiling of **30 KB gzipped (30720 bytes)** on the built bundle. The SDK npm project does not exist until task SDK-01; until then the sdk job must succeed as a no-op (skipped steps), NOT fail.
- Backend is .NET 8 (LTS). The solution file is `TelemetryGuard.sln` at the repo root; test projects are under `tests/` (created by FND-01).
- SDK build contract (consumed from SDK-01 when it lands): `TelemetryGuard.Sdk/package.json` with scripts `build` (esbuild → `dist/tg.js`) and `test`; lockfile `TelemetryGuard.Sdk/package-lock.json`. The size gate reads `TelemetryGuard.Sdk/dist/tg.js`.

## Prerequisites
FND-01 is complete: `TelemetryGuard.sln`, `Directory.Build.props`, and the three test projects (`tests/TelemetryGuard.Tests.Unit`, `tests/TelemetryGuard.Tests.Integration`, `tests/TelemetryGuard.Tests.Contracts`) exist, build cleanly, and `dotnet test` passes. `TelemetryGuard.Sdk/` exists as a placeholder directory without `package.json`.

## Implementation steps

1. **Create `.github/workflows/ci.yml`** with exactly this content:

```yaml
name: CI

on:
  push:
    branches: [main]
  pull_request:

concurrency:
  group: ci-${{ github.ref }}
  cancel-in-progress: true

env:
  DOTNET_NOLOGO: "true"
  DOTNET_CLI_TELEMETRY_OPTOUT: "true"

jobs:
  build-test:
    name: Build & unit tests
    runs-on: ubuntu-latest
    timeout-minutes: 15
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET 8
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 8.0.x

      - name: Cache NuGet packages
        uses: actions/cache@v4
        with:
          path: ~/.nuget/packages
          key: nuget-${{ runner.os }}-${{ hashFiles('**/*.csproj', 'Directory.Build.props') }}
          restore-keys: |
            nuget-${{ runner.os }}-

      - name: Restore
        run: dotnet restore TelemetryGuard.sln

      - name: Build (Release, warnings are errors)
        run: dotnet build TelemetryGuard.sln -c Release --no-restore

      - name: Unit tests
        run: >
          dotnet test tests/TelemetryGuard.Tests.Unit/TelemetryGuard.Tests.Unit.csproj
          -c Release --no-build
          --logger "trx;LogFileName=unit.trx"
          --results-directory TestResults

      - name: Upload test results
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: unit-test-results
          path: TestResults/*.trx
          if-no-files-found: ignore

  integration:
    name: Integration & contract tests (Testcontainers)
    runs-on: ubuntu-latest
    timeout-minutes: 30
    needs: build-test
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET 8
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 8.0.x

      - name: Cache NuGet packages
        uses: actions/cache@v4
        with:
          path: ~/.nuget/packages
          key: nuget-${{ runner.os }}-${{ hashFiles('**/*.csproj', 'Directory.Build.props') }}
          restore-keys: |
            nuget-${{ runner.os }}-

      - name: Restore
        run: dotnet restore TelemetryGuard.sln

      - name: Build (Release)
        run: dotnet build TelemetryGuard.sln -c Release --no-restore

      - name: Integration tests (SQL Server + Redis + ClickHouse via Testcontainers)
        run: >
          dotnet test tests/TelemetryGuard.Tests.Integration/TelemetryGuard.Tests.Integration.csproj
          -c Release --no-build
          --logger "trx;LogFileName=integration.trx"
          --results-directory TestResults

      - name: Analytics contract tests (per-provider suite)
        run: >
          dotnet test tests/TelemetryGuard.Tests.Contracts/TelemetryGuard.Tests.Contracts.csproj
          -c Release --no-build
          --logger "trx;LogFileName=contracts.trx"
          --results-directory TestResults

      - name: Upload test results
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: integration-test-results
          path: TestResults/*.trx
          if-no-files-found: ignore

  sdk:
    name: SDK build & size gate
    runs-on: ubuntu-latest
    timeout-minutes: 10
    steps:
      - uses: actions/checkout@v4

      - name: Check whether the SDK project exists yet
        id: gate
        run: |
          if [ -f TelemetryGuard.Sdk/package.json ]; then
            echo "exists=true" >> "$GITHUB_OUTPUT"
          else
            echo "exists=false" >> "$GITHUB_OUTPUT"
            echo "TelemetryGuard.Sdk/package.json not found — SDK job is a no-op until task SDK-01 lands."
          fi

      - name: Setup Node 20
        if: steps.gate.outputs.exists == 'true'
        uses: actions/setup-node@v4
        with:
          node-version: 20
          cache: npm
          cache-dependency-path: TelemetryGuard.Sdk/package-lock.json

      - name: Install dependencies
        if: steps.gate.outputs.exists == 'true'
        working-directory: TelemetryGuard.Sdk
        run: npm ci

      - name: Build bundle
        if: steps.gate.outputs.exists == 'true'
        working-directory: TelemetryGuard.Sdk
        run: npm run build

      - name: SDK tests (if present)
        if: steps.gate.outputs.exists == 'true'
        working-directory: TelemetryGuard.Sdk
        run: |
          if node -e "process.exit(require('./package.json').scripts?.test ? 0 : 1)"; then
            npm test
          else
            echo "No test script defined yet — skipping."
          fi

      - name: Gzip size gate (30 KB hard ceiling — spec D2)
        if: steps.gate.outputs.exists == 'true'
        run: |
          BUNDLE="TelemetryGuard.Sdk/dist/tg.js"
          if [ ! -f "$BUNDLE" ]; then
            echo "::error::Expected bundle at $BUNDLE after npm run build"
            exit 1
          fi
          RAW=$(wc -c < "$BUNDLE")
          GZ=$(gzip -9 -c "$BUNDLE" | wc -c)
          LIMIT=30720
          echo "Bundle: ${RAW} bytes raw, ${GZ} bytes gzipped (limit ${LIMIT})"
          if [ "$GZ" -gt "$LIMIT" ]; then
            echo "::error::SDK bundle is ${GZ} bytes gzipped — exceeds the ${LIMIT}-byte (30 KB) budget from spec D2"
            exit 1
          fi
```

2. **Notes on choices embedded above (keep them):**
   - Testcontainers needs no service containers or docker-in-docker setup on `ubuntu-latest`; the hosted runner's Docker daemon is used directly.
   - `integration` is a separate job from `build-test` so unit feedback stays fast; it `needs: build-test` to avoid burning container minutes on code that doesn't compile.
   - The sdk job is **step-guarded rather than job-skipped via `paths`**, so the workflow file needs no edits when SDK-01 lands — steps flip on automatically once `package.json` exists, and the job shows green (no-op) until then.
   - The size gate uses `gzip -9` on the built artifact; 30720 bytes = 30 KB, the top of the spec's 20–30 KB band. SDK-01 additionally enforces its own gate at build time; CI is the backstop.
   - `--no-build` on test steps keeps each job to a single compile.

3. **Verify locally before pushing** (optional but recommended): run the same commands the workflow runs — `dotnet restore`, `dotnet build -c Release --no-restore`, the three `dotnet test` invocations — from the repo root. All must exit 0 (integration/contract projects currently contain only template tests, which is fine).

4. **Push and confirm**: open a PR (or push to `main`) and confirm all three jobs pass; the sdk job must show the "no-op" log line and green status.

## Files to create or modify
- `/home/shafqat/git/shafqat/telemtry-guard/.github/workflows/ci.yml`

## Acceptance criteria
- Workflow triggers on `pull_request` (any branch) and `push` to `main` only.
- `build-test` job: restores with a NuGet cache keyed on csproj hashes, builds Release with `--no-restore`, runs unit tests with `--no-build`, uploads `.trx` results even on failure (`if: always()`).
- `integration` job: runs both `tests/TelemetryGuard.Tests.Integration` and `tests/TelemetryGuard.Tests.Contracts`, `needs: build-test`, 30-minute timeout.
- `sdk` job succeeds as a no-op on the current repo state (no `package.json`), with an explanatory log line; every Node step carries `if: steps.gate.outputs.exists == 'true'`.
- Size-gate step fails the build if `dist/tg.js` gzips to more than 30720 bytes, and fails with a clear error if the bundle is missing after a build.
- `concurrency` cancels superseded runs on the same ref.
- A real CI run on GitHub shows all three jobs green.

## Testing
The pipeline is its own test: one green run on the FND-01 scaffold state is the required proof. To pre-validate YAML syntax locally without pushing: `docker run --rm -v "$PWD":/repo rhysd/actionlint:latest -color /repo/.github/workflows/ci.yml` (or skip if Docker is unavailable — GitHub validates on push).

## Out of scope / guardrails
- **No deployment/publish jobs** — Azure deployment is Phase 2 (P2-04). No container image builds, no registry pushes, no release tagging.
- **No server-side Node**: Node appears in CI exclusively to compile the browser SDK bundle (the one allowed TS exception, spec D2). Do not add Node-based backend tooling, and do not scaffold the SDK project here — that is SDK-01.
- **Do not weaken the gates**: do not raise the 30 KB SDK ceiling, do not add `continue-on-error` to test steps, do not filter out the RLS/contract test projects. The contract suite is what keeps the analytics provider model (D7) safe; the integration suite is the replacement for EF's compile-time checking (D9) — both must stay blocking.
- **No matrix over analytics providers yet** — the Kusto provider and its CI matrix arrive with P2-05. Single-provider runs only.
- Keep `.NET 8.0.x` — do not bump to 9 (FND-01 pinned net8.0 LTS).
