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
# TelemetryGuard.Analytics.Kusto: NOT in this task's original prerequisite list (P2-05
# lands the Kusto provider in parallel and is out of scope here — see "Out of scope").
# It is included ONLY because TelemetryGuard.Api.csproj / TelemetryGuard.MigrationRunner.csproj
# now carry a ProjectReference to it (drift vs. the task doc — the actual .csproj graph
# wins per this task's own ground rule; recorded in the P2-04 upstream_fixes). Copying
# and restoring/building it does not add any Kusto behavior, config, or Bicep support —
# it is purely what `dotnet restore`/`publish` requires to resolve the reference.
COPY TelemetryGuard.Analytics.Kusto/TelemetryGuard.Analytics.Kusto.csproj               TelemetryGuard.Analytics.Kusto/
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
COPY TelemetryGuard.Analytics.Kusto/         TelemetryGuard.Analytics.Kusto/
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

# ---------- portal publish (P2-03; only reached via `--target portal`) ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS portal-build
WORKDIR /src
COPY Directory.Build.props ./
COPY TelemetryGuard.Portal/TelemetryGuard.Portal.csproj TelemetryGuard.Portal/
RUN dotnet restore TelemetryGuard.Portal/TelemetryGuard.Portal.csproj
COPY TelemetryGuard.Portal/ TelemetryGuard.Portal/
RUN dotnet publish TelemetryGuard.Portal/TelemetryGuard.Portal.csproj \
        -c Release --no-restore -o /out/portal

# ---------- portal runtime (docker build --target portal) ----------
# Kept ABOVE the API runtime stage on purpose: the LAST stage is the default build
# target, and CI's plain `docker build .` must keep producing the API image.
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS portal
WORKDIR /app
COPY --from=portal-build /out/portal ./
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
CMD ["dotnet", "/app/TelemetryGuard.Portal.dll"]

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
# CMD (not ENTRYPOINT): with an exec-form ENTRYPOINT, `docker run <image> <cmd>`
# APPENDS <cmd> as arguments to the entrypoint instead of replacing it (verified
# against podman) — that would break both the migrator smoke test below and the
# CI `image` job, which rely on `docker run <image> <other-command>` fully
# replacing the default process. Container Apps' own `command`/`args` job fields
# (step 5.9) already override this at the platform level regardless of CMD vs
# ENTRYPOINT, so CMD-only loses nothing there.
CMD ["dotnet", "/app/TelemetryGuard.Api.dll"]
