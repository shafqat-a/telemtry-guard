---
id: API-01
title: API skeleton, middleware order, OpenTelemetry
phase: 1
workstream: api
depends_on: [FND-01, FND-04, DAT-04]
size: M
spec_refs: [D3, D11, D13, D16, "§6", "§8"]
detail_level: full
---

# API-01: API skeleton, middleware order, OpenTelemetry

## Objective

Turn `TelemetryGuard.Api` into a runnable ASP.NET Core minimal-API host with the exact middleware order the rest of the API workstream builds on: ExceptionHandler → ForwardedHeaders (trusting only configured proxy CIDRs) → OpenTelemetry enrichment → TenantResolution (DAT-04) → per-tenant token-bucket rate limiter → endpoint groups. Includes OTel traces + metrics exported via OTLP (env-var configured), `/healthz` and `/ready` health endpoints (SQL `SELECT 1`, Redis `PING`, ClickHouse `/ping`), JSON console logging, and the complete `appsettings.json` layout that every later API/RSK/ANA task binds its options from.

## Spec context (self-contained)

- Backend is .NET (C#) end-to-end; ASP.NET Core 8 minimal APIs on Kestrel. Ingestion, click tracker, scoring, and decision all live in **one service** — scoring runs in-process, no network hop (spec D3). Never introduce server-side Python or Node.
- Multi-tenancy: every request that touches tenant data must flow through `TenantResolutionMiddleware` (built by DAT-04), which populates a scoped `ITenantContext` from either an API key (`X-Api-Key` header) or the site key (`k` query parameter). All Redis keys are prefixed `t:{tenantId}:…`. `TenantId` is never an optional parameter anywhere (spec D11).
- Cloudflare fronts the service in production (spec D13). The origin must only honor `X-Forwarded-For`/`X-Forwarded-Proto` from explicitly configured proxy ranges — otherwise any bot could spoof its IP and defeat velocity/enrichment signals. The real Cloudflare CIDR list is wired in by INT-05; this task makes the trusted ranges config-driven with an empty default (local dev sees real socket IPs).
- Observability: OpenTelemetry traces + metrics, OTLP exporter, configured via standard env vars (`OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_SERVICE_NAME`) so the same build works against local collectors and Azure Application Insights later (spec D16).
- Rate limiting is per-tenant (token bucket keyed on `TenantId`) — Redis-based per-tenant quotas exist conceptually (D11) but the HTTP-layer limiter here is in-process `System.Threading.RateLimiting`; unresolved-tenant requests are keyed by remote IP.
- Endpoint surface (later tasks): `GET /c` (API-02), `GET /p.gif` (API-03), `GET /i/init` + `POST /i` (API-04), `POST /decide` (API-05), `/admin/*` (API-07). This task maps only health endpoints and leaves clearly marked registration points.
- Errors are RFC 7807 `application/problem+json`.

## Prerequisites

After the depends_on tasks complete, the repo contains:

- **FND-01**: `TelemetryGuard.sln` with the `TelemetryGuard.Api` ASP.NET Core project (net8.0), plus `TelemetryGuard.Data`, `TelemetryGuard.Analytics.Abstractions`, test projects `tests/TelemetryGuard.Tests.Unit` and `tests/TelemetryGuard.Tests.Integration`.
- **FND-04**: core primitives in `TelemetryGuard.Core` — `TenantId` (readonly record struct over `Guid`), `ITenantContext` + mutable `TenantContext` (scoped) in namespace `TelemetryGuard.Core.Tenancy`, and `IClock` in `TelemetryGuard.Core.Time` — see `doc/tasks/FND-04-core-primitives.md` for exact members and the DI registration extension.
- **DAT-04**: `TelemetryGuard.Api/Tenancy/TenantResolutionMiddleware.cs` plus `ITenantResolver` (`TelemetryGuard.Data.Tenancy`), registered via `services.AddTelemetryGuardData()` (DAT-03's extension). Behavior (see `doc/tasks/DAT-04-tenant-resolution-middleware.md`): resolves `X-Api-Key` header first, then `?k=` on `/c` and `/p.gif`; **passes `/i` and `/i/init` through unresolved** (those endpoints resolve via `ITenantResolver` themselves — API-04); unknown keys get anti-probing responses (404 on `/c`, the GIF on `/p.gif`, 401 problem+json on admin routes); exempts `/healthz` and `/alive` via its `IsExempt` helper.

## Implementation steps

1. **Add NuGet packages** to `TelemetryGuard.Api/TelemetryGuard.Api.csproj` (latest stable versions):
   - `OpenTelemetry.Extensions.Hosting`
   - `OpenTelemetry.Instrumentation.AspNetCore`
   - `OpenTelemetry.Instrumentation.Http`
   - `OpenTelemetry.Instrumentation.Runtime`
   - `OpenTelemetry.Exporter.OpenTelemetryProtocol`
   - `StackExchange.Redis`
   - `Microsoft.Data.SqlClient`

2. **Write `TelemetryGuard.Api/Program.cs`** (full composition — adjust the two FND-04/DAT-04 registration lines to whatever extension names those tasks actually created):

   ```csharp
   using System.Threading.RateLimiting;
   using Microsoft.AspNetCore.HttpOverrides;
   using OpenTelemetry.Metrics;
   using OpenTelemetry.Resources;
   using OpenTelemetry.Trace;
   using StackExchange.Redis;
   using TelemetryGuard.Api.Health;
   using TelemetryGuard.Api.Middleware;
   // + usings for FND-04 primitives and DAT-04 middleware — check those projects

   var builder = WebApplication.CreateBuilder(args);

   // ---------- JSON console logging ----------
   builder.Logging.ClearProviders();
   builder.Logging.AddJsonConsole(o =>
   {
       o.IncludeScopes = true;
       o.UseUtcTimestamp = true;
       o.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z' ";
   });

   // ---------- Core services ----------
   builder.Services.AddProblemDetails();
   builder.Services.AddMemoryCache();
   builder.Services.AddHttpClient();
   builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
       ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Redis")
           ?? throw new InvalidOperationException("ConnectionStrings:Redis missing")));

   // FND-04 + DAT-03/04 registrations (verify exact extension names in those projects):
   // builder.Services.AddTelemetryGuardCore();   // FND-04: TenantContext/ITenantContext (scoped), IClock
   // builder.Services.AddTelemetryGuardData();   // DAT-03/04: TenantConnectionFactory, repos, ITenantResolver

   // ---------- OpenTelemetry (OTLP endpoint/headers come from standard env vars) ----------
   builder.Services.AddOpenTelemetry()
       .ConfigureResource(r => r.AddService(
           Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "telemetry-guard-api"))
       .WithTracing(t => t
           .AddAspNetCoreInstrumentation(o => o.Filter = ctx =>
               ctx.Request.Path != "/healthz" && ctx.Request.Path != "/ready")
           .AddHttpClientInstrumentation()
           .AddOtlpExporter())
       .WithMetrics(m => m
           .AddAspNetCoreInstrumentation()
           .AddHttpClientInstrumentation()
           .AddRuntimeInstrumentation()
           .AddOtlpExporter());

   // ---------- ForwardedHeaders: trust ONLY configured proxy CIDRs ----------
   builder.Services.Configure<ForwardedHeadersOptions>(o =>
   {
       o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
       o.ForwardLimit = 1;
       o.KnownNetworks.Clear();
       o.KnownProxies.Clear();
       var cidrs = builder.Configuration.GetSection("ForwardedHeaders:TrustedProxyCidrs")
                       .Get<string[]>() ?? [];
       foreach (var cidr in cidrs)
       {
           var parts = cidr.Split('/');
           o.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(
               System.Net.IPAddress.Parse(parts[0]), int.Parse(parts[1])));
       }
   });

   // ---------- Per-tenant token-bucket rate limiter ----------
   var tokensPerSecond = builder.Configuration.GetValue("RateLimiting:TokensPerSecond", 100);
   var bucketSize      = builder.Configuration.GetValue("RateLimiting:BucketSize", 200);
   builder.Services.AddRateLimiter(o =>
   {
       o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
       o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
       {
           var path = ctx.Request.Path;
           if (path.StartsWithSegments("/healthz") || path.StartsWithSegments("/ready"))
               return RateLimitPartition.GetNoLimiter("health");
           var tenant = ctx.RequestServices.GetRequiredService<ITenantContext>();
           var key = tenant.IsResolved
               ? "t:" + tenant.TenantId.Value.ToString("D")
               : "ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");
           return RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
           {
               TokenLimit = bucketSize,
               TokensPerPeriod = tokensPerSecond,
               ReplenishmentPeriod = TimeSpan.FromSeconds(1),
               QueueLimit = 0,
               AutoReplenishment = true
           });
       });
   });

   // ---------- Health checks ----------
   builder.Services.AddHealthChecks()
       .AddCheck<SqlHealthCheck>("sql", tags: ["ready"])
       .AddCheck<RedisHealthCheck>("redis", tags: ["ready"])
       .AddCheck<ClickHouseHealthCheck>("clickhouse", tags: ["ready"]);

   var app = builder.Build();

   // ================= MIDDLEWARE ORDER — FIXED, DO NOT REORDER =================
   app.UseExceptionHandler();                       // 1. RFC 7807 for unhandled exceptions
   app.UseForwardedHeaders();                       // 2. before anything reads RemoteIpAddress
   app.UseMiddleware<OtelEnrichmentMiddleware>();   // 3. Activity tags
   app.UseMiddleware<TelemetryGuard.Api.Tenancy.TenantResolutionMiddleware>(); // 4. DAT-04
   app.UseRateLimiter();                            // 5. keyed on resolved TenantId
   // note: /i, /i/init (and /decide, see API-05) pass through step 4 unresolved and are
   // therefore rate-limited by client IP — expected; those endpoints resolve tenants themselves.
   // ============================================================================

   app.MapHealthChecks("/healthz", new() { Predicate = _ => false });          // liveness only
   app.MapHealthChecks("/ready",   new() { Predicate = c => c.Tags.Contains("ready") });

   // ---- ENDPOINT GROUPS: later tasks add ONE line each here ----
   // app.MapTrackerEndpoints();    // API-02  GET /c
   // app.MapPixelEndpoints();      // API-03  GET /p.gif
   // app.MapBeaconEndpoints();     // API-04  GET /i/init, POST /i
   // app.MapDecisionEndpoints();   // API-05  POST /decide
   // app.MapAdminEndpoints();      // API-07  /admin/*

   app.Run();

   public partial class Program { } // exposes Program to WebApplicationFactory tests
   ```

3. **Create `TelemetryGuard.Api/Middleware/OtelEnrichmentMiddleware.cs`**:

   ```csharp
   using System.Diagnostics;

   namespace TelemetryGuard.Api.Middleware;

   public sealed class OtelEnrichmentMiddleware(RequestDelegate next)
   {
       public async Task InvokeAsync(HttpContext ctx)
       {
           var activity = Activity.Current;
           if (activity is not null)
           {
               activity.SetTag("tg.client_ip", ctx.Connection.RemoteIpAddress?.ToString());
               if (ctx.Request.Query.TryGetValue("k", out var k))
                   activity.SetTag("tg.site_key", k.ToString());
           }
           await next(ctx);
           if (activity is not null)
           {
               var tenant = ctx.RequestServices.GetService<ITenantContext>();
               if (tenant?.IsResolved == true)
                   activity.SetTag("tg.tenant_id", tenant.TenantId.Value.ToString("D"));
           }
       }
   }
   ```
   (Add the correct `using` for `ITenantContext` from FND-04's project.)

4. **Create the three health checks** in `TelemetryGuard.Api/Health/`:

   ```csharp
   // SqlHealthCheck.cs — opens ConnectionStrings:Main, runs SELECT 1.
   public sealed class SqlHealthCheck(IConfiguration cfg) : IHealthCheck
   {
       public async Task<HealthCheckResult> CheckHealthAsync(
           HealthCheckContext context, CancellationToken ct = default)
       {
           try
           {
               await using var conn = new Microsoft.Data.SqlClient.SqlConnection(
                   cfg.GetConnectionString("Main"));
               await conn.OpenAsync(ct);
               await using var cmd = conn.CreateCommand();
               cmd.CommandText = "SELECT 1";
               await cmd.ExecuteScalarAsync(ct);
               return HealthCheckResult.Healthy();
           }
           catch (Exception ex) { return HealthCheckResult.Unhealthy("sql", ex); }
       }
   }

   // RedisHealthCheck.cs — IConnectionMultiplexer.GetDatabase().PingAsync().
   public sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
   {
       public async Task<HealthCheckResult> CheckHealthAsync(
           HealthCheckContext context, CancellationToken ct = default)
       {
           try { await redis.GetDatabase().PingAsync(); return HealthCheckResult.Healthy(); }
           catch (Exception ex) { return HealthCheckResult.Unhealthy("redis", ex); }
       }
   }

   // ClickHouseHealthCheck.cs — pings the host/port from Analytics:ClickHouse:ConnectionString
   // (ANA-03/ANA-05 own that section — there is deliberately NO separate "ClickHouse" config section).
   public sealed class ClickHouseHealthCheck(IHttpClientFactory http, IConfiguration cfg) : IHealthCheck
   {
       public async Task<HealthCheckResult> CheckHealthAsync(
           HealthCheckContext context, CancellationToken ct = default)
       {
           try
           {
               // Parse "Host=...;Port=...;Database=...;..." (ClickHouse.Client format, ANA-03).
               var cs = cfg["Analytics:ClickHouse:ConnectionString"] ?? "";
               var parts = cs.Split(';', StringSplitOptions.RemoveEmptyEntries)
                   .Select(p => p.Split('=', 2))
                   .Where(kv => kv.Length == 2)
                   .ToDictionary(kv => kv[0].Trim(), kv => kv[1].Trim(), StringComparer.OrdinalIgnoreCase);
               var host = parts.GetValueOrDefault("Host", "localhost");
               var port = parts.GetValueOrDefault("Port", "8123");
               var resp = await http.CreateClient("clickhouse-health")
                   .GetAsync($"http://{host}:{port}/ping", ct);
               return resp.IsSuccessStatusCode
                   ? HealthCheckResult.Healthy()
                   : HealthCheckResult.Unhealthy($"clickhouse status {(int)resp.StatusCode}");
           }
           catch (Exception ex) { return HealthCheckResult.Unhealthy("clickhouse", ex); }
       }
   }
   ```
   The SQL health check is the ONLY place in the API allowed to open a raw (non-tenant-stamped) `SqlConnection` — it touches no tenant tables. Every repository call goes through `TenantConnectionFactory` (DAT-03).

5. **Write `TelemetryGuard.Api/appsettings.json`** — the canonical aggregation point for every options section other tasks bind. If a dependency task's file names a key differently, that task's name wins; update this file to match, never fork a second section:

   ```json
   {
     "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
     "ConnectionStrings": {
       "Main": "Server=localhost,1433;Database=TelemetryGuard;User Id=sa;Password=DevPassw0rd!;TrustServerCertificate=true",
       "Redis": "localhost:6379"
     },
     "Analytics": {
       "Provider": "ClickHouse",
       "ClickHouse": {
         "ConnectionString": "Host=localhost;Port=8123;Database=telemetry_guard;Username=default;Password=",
         "EventQueueCapacity": 100000,
         "EventMaxBatchSize": 5000,
         "EventMaxBatchAgeSeconds": 2,
         "LabelQueueCapacity": 10000,
         "LabelMaxBatchSize": 500,
         "LabelMaxBatchAgeSeconds": 5,
         "FlushMaxRetries": 3,
         "FlushRetryBaseDelayMs": 200,
         "ShutdownDrainTimeoutSeconds": 10
       }
     },
     "GeoIp": {
       "CityDbPath": "data/GeoLite2-City.mmdb",
       "AsnDbPath": "data/GeoLite2-ASN.mmdb",
       "ProxyDbPath": "data/IP2LOCATION-LITE-PX.BIN"
     },
     "Scoring": { "BudgetMs": 50, "Bands": { "AllowMax": 30, "ChallengeMax": 70 } },
     "Turnstile": { "SiteKey": "", "SecretKey": "", "VerifyUrl": "https://challenges.cloudflare.com/turnstile/v0/siteverify" },
     "RateLimiting": { "TokensPerSecond": 100, "BucketSize": 200 },
     "ForwardedHeaders": { "TrustedProxyCidrs": [] },
     "Tracker": { "GraceSeconds": 10, "SessionCookieName": "tg_sid", "CampaignCacheSeconds": 60, "SessionTtlSeconds": 1800 },
     "Beacon": {
       "HmacSecret": "",
       "MaxBodyBytes": 65536,
       "NonceTtlSeconds": 900,
       "MaxClockSkewMs": 120000,
       "SessionTtlSeconds": 1800,
       "SinkEveryNthBeacon": 10
     },
     "Retention": { "DefaultDays": 90 },
     "Enforcement": { "DefaultMode": "AutoEnforce" }
   }
   ```

   Section ownership (documented here so implementers of later tasks know where to bind): `Analytics` (incl. the nested `Analytics:ClickHouse` — the values above mirror ANA-05's section shape exactly; there is NO standalone top-level `ClickHouse` section, and `Database=telemetry_guard` matches the FND-02/OPS-01 stack) → ANA-03/ANA-05 · `GeoIp` → RSK-02 · `Scoring` → RSK-07/API-05 · `Turnstile` → INT-01/API-05 · `Tracker` → API-02/API-03/API-06 · `Beacon` → API-04 · `Retention` → API-02/03/04/06 event stamping · `Enforcement` → API-06/INT-02 · `RateLimiting`/`ForwardedHeaders` → this task (INT-05 later fills `TrustedProxyCidrs` with Cloudflare ranges).

6. **Write `TelemetryGuard.Api/appsettings.Development.json`** overriding log levels (`Default: Debug` for `TelemetryGuard.*`) and supplying the dev-only beacon secret: `"Beacon": { "HmacSecret": "dev-only-secret-change-me-0123456789ab" }` (≥32 chars — API-04 adds startup validation that rejects empty/short secrets, so the base file deliberately ships it blank). The base file already points at the FND-02 Docker Compose ports.

7. **Extend DAT-04's exemption list**: DAT-04's `TenantResolutionMiddleware.IsExempt` covers `/healthz` and `/alive` but not this task's `/ready` — add `|| p.StartsWithSegments("/ready")` to that helper in `TelemetryGuard.Api/Tenancy/TenantResolutionMiddleware.cs` (one line; without it `/ready` would 401).

8. **OTLP via environment**: do not put OTLP endpoints in appsettings. Document in a comment at the top of `Program.cs`: set `OTEL_EXPORTER_OTLP_ENDPOINT` (e.g. `http://localhost:4317`) and `OTEL_SERVICE_NAME=telemetry-guard-api`. When unset, the OTLP exporter defaults to `http://localhost:4317` and failures to export are non-fatal (spans dropped) — acceptable for dev.

## Files to create or modify

- `TelemetryGuard.Api/TelemetryGuard.Api.csproj` (add packages)
- `TelemetryGuard.Api/Program.cs` (full rewrite as above)
- `TelemetryGuard.Api/Middleware/OtelEnrichmentMiddleware.cs`
- `TelemetryGuard.Api/Health/SqlHealthCheck.cs`
- `TelemetryGuard.Api/Health/RedisHealthCheck.cs`
- `TelemetryGuard.Api/Health/ClickHouseHealthCheck.cs`
- `TelemetryGuard.Api/Tenancy/TenantResolutionMiddleware.cs` (add `/ready` to `IsExempt`)
- `TelemetryGuard.Api/appsettings.json`
- `TelemetryGuard.Api/appsettings.Development.json`
- `tests/TelemetryGuard.Tests.Unit/Api/PipelineTests.cs`

## Acceptance criteria

- `dotnet build TelemetryGuard.sln` succeeds with zero warnings-as-errors regressions.
- `dotnet run --project TelemetryGuard.Api` starts; log output is one JSON object per line (verify a line parses with `python -m json.tool`-equivalent or by eye: `{"Timestamp":...,"LogLevel":...}`).
- `curl -s -o /dev/null -w "%{http_code}" http://localhost:<port>/healthz` → `200` even with SQL/Redis/ClickHouse down.
- With the FND-02 compose stack up, `/ready` → `200`; with it down → `503`.
- Middleware order in `Program.cs` is exactly: ExceptionHandler, ForwardedHeaders, OtelEnrichmentMiddleware, TenantResolution, RateLimiter — verifiable by reading the file top-to-bottom.
- A request carrying `X-Forwarded-For: 1.2.3.4` from a non-configured source does NOT change `RemoteIpAddress` (asserted in tests).
- Burst of `BucketSize + 10` concurrent requests to any tenant endpoint yields at least one `429`.
- `/healthz` and `/ready` are exempt from rate limiting and tenant resolution.
- Unhandled exceptions return `application/problem+json` with status 500 and no stack trace in the body.

## Testing

- `tests/TelemetryGuard.Tests.Unit/Api/PipelineTests.cs` using `Microsoft.AspNetCore.Mvc.Testing` (`WebApplicationFactory<Program>`):
  - `/healthz` returns 200 with no backing services (liveness predicate excludes all checks).
  - Spoofed `X-Forwarded-For` is ignored when `ForwardedHeaders:TrustedProxyCidrs` is empty (add a test endpoint or assert via a response header echo in test-only configuration).
  - With `RateLimiting:TokensPerSecond=1, BucketSize=1` overridden via `WithWebHostBuilder(ConfigureAppConfiguration)`, two rapid requests → second is 429.
  - Unhandled-exception route (map a throwing endpoint in the test host) → 500 + `application/problem+json`.
- No Testcontainers here; `/ready` against real stores is covered by DAT-08/ANA-06 harnesses and manual compose verification.

## Out of scope / guardrails

- **No endpoints beyond health** — `/c`, `/p.gif`, `/i`, `/decide`, `/admin` are API-02..API-07; leave only the commented registration lines.
- **No EF Core.** All future SQL goes through Dapper repositories on tenant-stamped connections (`TenantConnectionFactory`, DAT-03). The health check's raw connection is the sole sanctioned exception and must never query tenant tables. RLS (`SESSION_CONTEXT('TenantId')`) is the primary isolation mechanism — nothing in this task may create an alternative connection path for repositories.
- **No server-side Python/Node** anywhere, including tooling invoked at runtime.
- **Do not hardcode Cloudflare IP ranges** — `TrustedProxyCidrs` stays config-driven and empty by default; INT-05 owns populating it.
- **No scoring, no analytics writes** here — the <50 ms scoring budget and the non-blocking `IEventSink` are later tasks' concerns; this task must not add middleware that does I/O per request (the enrichment middleware only sets in-memory Activity tags).
- **No generic query/abstraction layers** — do not add a health-check abstraction over "any analytics engine"; the ClickHouse ping is deliberately ClickHouse-specific (spec D7).
- `TenantId` must never be optional or defaulted; unresolved tenants are keyed by IP in the limiter and rejected by DAT-04's middleware for tenant endpoints.
