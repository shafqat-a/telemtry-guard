// ---------------------------------------------------------------------------
// OpenTelemetry export is configured via the STANDARD environment variables —
// deliberately NOT via appsettings (API-01 step 8):
//   OTEL_EXPORTER_OTLP_ENDPOINT   e.g. http://localhost:4317 (also the default when unset)
//   OTEL_SERVICE_NAME             e.g. telemetry-guard-api
// When no collector is listening, export failures are non-fatal (spans/metrics
// are dropped) — acceptable for local dev.
// ---------------------------------------------------------------------------

using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StackExchange.Redis;
using TelemetryGuard.Api.Analytics;
using TelemetryGuard.Api.Edge;
using TelemetryGuard.Api.Endpoints;
using TelemetryGuard.Api.Health;
using TelemetryGuard.Api.Middleware;
using TelemetryGuard.Api.Options;
using TelemetryGuard.Api.Services;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Core.Time;
using TelemetryGuard.Data;
using TelemetryGuard.Integrations.GoogleAds;
using TelemetryGuard.Integrations.Meta;
using TelemetryGuard.Integrations.Turnstile;
using TelemetryGuard.RiskEngine.Pipeline;
using TelemetryGuard.RiskEngine.Velocity;

var builder = WebApplication.CreateBuilder(args);

// ---------- JSON console logging: one JSON object per line ----------
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.UseUtcTimestamp = true;
    o.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z' ";
});

// ---------- Core services ----------
builder.Services.AddProblemDetails();   // RFC 7807 bodies for UseExceptionHandler/UseStatusCodePages
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var connectionString = builder.Configuration.GetConnectionString("Redis")
        ?? throw new InvalidOperationException("ConnectionStrings:Redis missing");
    var options = ConfigurationOptions.Parse(connectionString);
    // Never fail DI resolution when Redis is down: the multiplexer keeps
    // reconnecting in the background and RedisHealthCheck reports Unhealthy
    // instead of the whole app (or /ready) crashing.
    options.AbortOnConnectFail = false;
    return ConnectionMultiplexer.Connect(options);
});

// FND-04 core primitives: scoped set-once tenant context + testable clock.
// The concrete TenantContext is what TenantResolutionMiddleware writes to;
// ITenantContext is the read-side consumed by SQL/Redis/analytics layers.
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
builder.Services.AddSingleton<IClock>(SystemClock.Instance);

// DAT-03/04: connection factories + ITenantResolver (repositories extend this).
builder.Services.AddTelemetryGuardData();

// ANA-05: config-driven analytics provider switch (D6/D7) — fails fast on an
// unknown/missing provider in the "Analytics" config section and validates
// options at startup.
builder.Services.AddTelemetryGuardAnalytics(builder.Configuration);

// RSK-03: Redis velocity store + click-id dedupe (scoped; reuses the
// multiplexer registered above via TryAddSingleton).
builder.Services.AddVelocityStore(builder.Configuration);

// ANA-07: D23 rollup job — every 15 min RollupService materializes per-tenant/
// per-campaign/per-day verdict aggregates from ClickHouse into the SQL summary
// tables. The ONLY bridge between the event store and the relational tier.
builder.Services.Configure<TelemetryGuard.Api.Workers.RollupOptions>(builder.Configuration.GetSection("Rollup"));
builder.Services.AddHostedService<TelemetryGuard.Api.Workers.RollupService>();

// API-02: tracker + retention option sections (layout owned by API-01's appsettings).
builder.Services.Configure<TrackerOptions>(builder.Configuration.GetSection("Tracker"));
builder.Services.Configure<RetentionOptions>(builder.Configuration.GetSection("Retention"));

// API-04: beacon ingestion options with startup validation — an empty/short
// HmacSecret would silently HMAC with nothing and make storage_sig_ok
// meaningless. The base appsettings.json deliberately ships it blank; the
// dev-only value (>= 32 chars) lives in appsettings.Development.json.
builder.Services.AddOptions<BeaconOptions>()
    .Bind(builder.Configuration.GetSection("Beacon"))
    .Validate(
        o => !string.IsNullOrEmpty(o.HmacSecret) && o.HmacSecret.Length >= 32,
        "Beacon:HmacSecret must be set and at least 32 characters (API-04).")
    .ValidateOnStart();

// P2-02: registry-driven model selection (D18). When Scoring:ModelSource is
// "Registry" (the shipped default), the promoted dbo.ModelRegistry row — never
// appsettings — decides which model this process loads and whether it ENFORCES
// ('active') or only shadows ('shadow'). Resolution writes the same Scoring:*
// keys RSK-08's AddScoringPipeline already understands, so the scoring DI switch
// is untouched. An empty registry, an unreachable SQL Server, or a failed artifact
// verification all leave the heuristic enforcing — never the other way round.
var useRegistry = string.Equals(
    builder.Configuration["Scoring:ModelSource"], "Registry", StringComparison.OrdinalIgnoreCase);

var modelState = useRegistry
    ? TelemetryGuard.Api.Startup.ModelRegistryBootstrap.Resolve(
        builder.Configuration, TelemetryGuard.RiskEngine.Contracts.FraudFeatureVector.FeatureSetVersion,
        TimeSpan.FromSeconds(5))
    : new TelemetryGuard.Api.Startup.ServingModelState(null, null, null,
        ["Scoring:ModelSource != Registry — Scoring:ModelPath from configuration is authoritative (RSK-08 pilot mode)."]);

if (useRegistry)
{
    builder.Configuration.AddInMemoryCollection(
        TelemetryGuard.Api.Startup.ModelRegistryBootstrap.ToScoringOverrides(modelState));
}
builder.Services.AddSingleton(modelState);   // what THIS process actually loaded — the watcher compares against it
builder.Services.Configure<ModelRegistryOptions>(
    builder.Configuration.GetSection(ModelRegistryOptions.SectionName));
builder.Services.AddHostedService<TelemetryGuard.Api.Workers.ModelRegistryWatcher>();

// RSK-07: in-process scoring pipeline (whitelist short-circuit -> prefetch ->
// extract -> rules -> scorer -> Math.Max floor fold -> band). API-05's /decide
// is the first API-host consumer.
builder.Services.AddScoringPipeline(builder.Configuration);

// INT-01: Cloudflare Turnstile server-side verification, consumed only by
// API-05's /decide challenge round-trip (never the scoring hot path).
builder.Services.AddTurnstileVerification(builder.Configuration);

// API-05: score-to-band thresholds (spec §6.3) — never hardcode AllowMax/ChallengeMax.
builder.Services.Configure<ScoringBandOptions>(
    builder.Configuration.GetSection(ScoringBandOptions.SectionName));

// INT-02: "Enforcement:DefaultMode" documented fallback label — see
// EnforcementOptions's doc comment for why nothing on the request path reads it
// (per-tenant dbo.Tenants.EnforcementMode via TenantRecord is authoritative).
builder.Services.Configure<TelemetryGuard.Api.Options.EnforcementOptions>(
    builder.Configuration.GetSection(TelemetryGuard.Api.Options.EnforcementOptions.SectionName));

// API-06: real verdict finalizer (verdict persistence, exclusion-queue writes,
// EnforcementMode handling, summary MERGEs) — replaces API-05's build-order
// stub. Scoped: it consumes scoped tenant-bound repositories and must be
// resolved from a scope (API-05's per-request scope or the grace worker's
// manual scope below both satisfy this).
builder.Services.AddScoped<IVerdictFinalizer, VerdictFinalizer>();

// API-06: grace-period worker — every second, finalizes sessions whose ~10 s
// beacon grace period (API-02/API-03) expired with no beacon ever arriving.
builder.Services.AddHostedService<VerdictFinalizerService>();

// INT-03: Google Ads exclusion sync — every GoogleAds:SyncIntervalMinutes
// minutes, pushes approved dbo.ExclusionQueue rows (Platform='google') to
// Google Ads as negative campaign criteria. Dry-run ON by default (see
// GoogleAdsOptions.EffectiveDryRun) until real credentials are configured.
builder.Services.AddGoogleAdsGateway(builder.Configuration);
builder.Services.AddHostedService<TelemetryGuard.Api.Workers.GoogleAdsExclusionSyncService>();

// INT-04: Meta Marketing API exclusion sync — every Meta:SyncIntervalMinutes
// minutes, marks Platform='meta'/SourceType='ip' rows Unsupported (Meta has no
// IP-exclusion API, D15) and pushes approved 'placement' rows into a per-tenant
// publisher block list. Dry-run ON by default (see MetaOptions.EffectiveDryRun)
// until a system-user token is configured.
builder.Services.AddMetaMarketingClient(builder.Configuration);
builder.Services.AddHostedService<TelemetryGuard.Api.Workers.MetaExclusionSyncService>();

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

// INT-05: Cloudflare edge signal intake — config gate + reader. Registered
// unconditionally (cheap, options-only); EdgeSignalReader itself no-ops when
// Edge:Provider != "Cloudflare" or the direct peer isn't a Cloudflare address.
builder.Services.Configure<EdgeOptions>(builder.Configuration.GetSection(EdgeOptions.SectionName));
builder.Services.AddSingleton<IEdgeSignalReader, EdgeSignalReader>();

// ---------- ForwardedHeaders: trust ONLY configured proxy CIDRs (spec D13) ----------
// Empty by default so local dev sees real socket IPs; INT-05 fills in the
// Cloudflare ranges. Never hardcode proxy CIDRs here.
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
    // INT-05: when Cloudflare-fronted, merge the embedded Cloudflare ranges into the
    // trusted-proxy set (config-driven activation — the ranges themselves are never
    // hardcoded at this call site). Runs BEFORE the empty-trust sentinel guard below
    // so a Cloudflare deployment with no extra TrustedProxyCidrs still ends up with a
    // non-empty KnownNetworks and skips the sentinel.
    if (builder.Configuration.GetValue<string>("Edge:Provider")?.Equals("Cloudflare",
            StringComparison.OrdinalIgnoreCase) == true)
    {
        foreach (var (addr, prefix) in CloudflareIpRanges.Load())
            o.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(addr, prefix));
    }
    if (o.KnownNetworks.Count == 0 && o.KnownProxies.Count == 0)
    {
        // Footgun guard: ForwardedHeadersMiddleware treats EMPTY Known* lists as
        // "trust every proxy". An empty TrustedProxyCidrs must mean "trust NO
        // proxy", so pin an unmatchable sentinel to force the source check.
        o.KnownProxies.Add(System.Net.IPAddress.None); // 255.255.255.255 — matches nothing
    }
});

// ---------- Per-tenant token-bucket rate limiter (in-process, D11) ----------
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
// The "clickhouse"/"kusto" readiness check is registered inside the D7 provider
// switch (AddTelemetryGuardAnalytics, TelemetryGuard.Api/Analytics) — AddHealthChecks()
// is additive and may be called from both places (P2-05 step 9).
builder.Services.AddHealthChecks()
    .AddCheck<SqlHealthCheck>("sql", tags: ["ready"])
    .AddCheck<RedisHealthCheck>("redis", tags: ["ready"]);

var app = builder.Build();

// P2-02: the model-registry bootstrap ran before logging existed — flush its log
// lines through the real logger now.
foreach (var line in modelState.Log) app.Logger.LogInformation("{ModelRegistryBootstrap}", line);

// ================= MIDDLEWARE ORDER — FIXED, DO NOT REORDER =================
app.UseExceptionHandler();                       // 1. RFC 7807 for unhandled exceptions
app.UseForwardedHeaders();                       // 2. before anything reads RemoteIpAddress
app.UseMiddleware<OtelEnrichmentMiddleware>();   // 3. Activity tags

// 3.5 — SDK-08: /sdk bundle delivery (D22). The API origin serves the esbuild
// artifacts that CopySdkBundle placed in wwwroot/sdk; Cloudflare in front
// (INT-05) supplies the CDN layer. This is a NON-REJOINING branch registered
// BEFORE tenant resolution and the rate limiter: /sdk/* is tenant-agnostic
// (one artifact for all tenants — no per-tenant data may ever be inlined), and
// misses (unknown file, directory URL, traversal) terminate the branch with a
// plain 404 instead of falling through to a 401. Only this one directory is
// exposed — never widen the mapping. "Sdk:BundleRoot" exists for tests only.
var sdkBundleRoot = app.Configuration["Sdk:BundleRoot"]
    ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot", "sdk");
app.MapWhen(ctx => ctx.Request.Path.StartsWithSegments("/sdk"), sdk =>
{
    // Fresh clone / SDK never built: no directory, no middleware — every /sdk/*
    // request 404s, startup succeeds (honest and harmless in dev).
    if (!Directory.Exists(sdkBundleRoot)) return;

    // Restrictive content-type map: only .js and .js.map can leave this directory
    // (.map is absent from the default provider and must be added explicitly).
    var sdkContentTypes = new FileExtensionContentTypeProvider(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".js"] = "text/javascript",
            [".map"] = "application/json"
        });

    sdk.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(sdkBundleRoot),
        RequestPath = "/sdk",
        ServeUnknownFileTypes = false,
        ContentTypeProvider = sdkContentTypes,
        OnPrepareResponse = ctx =>
        {
            var response = ctx.Context.Response;
            // D22 cache contract — NEVER swap these two policies:
            //   pinned tg-<semver>.js[.map]  -> immutable (content never changes;
            //                                   SDK-01 bumps package.json instead);
            //   latest tg.js[.map]           -> 5-min TTL so a detection fix
            //                                   reaches every tenant page in minutes.
            response.Headers.CacheControl = PinnedSdkBundleRegex().IsMatch(ctx.File.Name)
                ? "public, max-age=31536000, immutable"
                : "public, max-age=300, stale-while-revalidate=60";
            response.ContentType = ctx.File.Name.EndsWith(".map", StringComparison.OrdinalIgnoreCase)
                ? "application/json"
                : "text/javascript; charset=utf-8";
            response.Headers.AccessControlAllowOrigin = "*";              // loaded from third-party tenant pages
            response.Headers["Cross-Origin-Resource-Policy"] = "cross-origin";
            response.Headers.XContentTypeOptions = "nosniff";
        }
    });
    // Deliberately no terminal middleware: an unserved request ends the branch as 404.
});

app.UseMiddleware<TelemetryGuard.Api.Tenancy.TenantResolutionMiddleware>(); // 4. DAT-04
app.UseRateLimiter();                            // 5. keyed on resolved TenantId
// note: /i, /i/init (and /decide, see API-05) pass through step 4 unresolved and are
// therefore rate-limited by client IP — expected; those endpoints resolve tenants themselves.
// ============================================================================

app.MapHealthChecks("/healthz", new() { Predicate = _ => false });          // liveness only
app.MapHealthChecks("/ready",   new() { Predicate = c => c.Tags.Contains("ready") });

// ---- TEST-HOST-ONLY diagnostics: enabled solely by the WebApplicationFactory
// pipeline tests via the "TestHost:EnableDiagnostics" flag, which no shipped
// appsettings file ever sets. Not part of the endpoint surface. ----
if (app.Configuration.GetValue("TestHost:EnableDiagnostics", false))
{
    app.MapGet("/__test/ip", (HttpContext ctx) =>
        Results.Text(ctx.Connection.RemoteIpAddress?.ToString() ?? "none"));
    // INT-05: echoes IEdgeSignalReader.Read(ctx) so EdgeSignalsTests can assert the
    // gate + spoof-defense behavior over the REAL Program composition (ForwardedHeaders
    // included) without standing up a Cloudflare-fronted endpoint of its own.
    app.MapGet("/__test/edge-signals", (HttpContext ctx, IEdgeSignalReader edgeSignals) =>
    {
        var s = edgeSignals.Read(ctx);
        return Results.Json(new { s.Ja3, s.Ja4, s.Asn, s.BotScore });
    });
    app.MapGet("/__test/throw", (HttpContext _) =>
    {
        throw new InvalidOperationException("Deliberate test-host failure.");
#pragma warning disable CS0162 // unreachable — gives the lambda a Result return type
        return Results.Ok();
#pragma warning restore CS0162
    });
}

// ---- ENDPOINT GROUPS: later tasks add ONE line each here ----
app.MapTrackerEndpoints();       // API-02  GET /c
app.MapPixelEndpoints();         // API-03  GET /p.gif
app.MapBeaconEndpoints();        // API-04  GET /i/init, POST /i
app.MapDecisionEndpoints();      // API-05  POST /decide
app.MapAdminEndpoints();         // API-07  /admin/*
app.MapEnforcementAdminEndpoints(); // INT-02  /admin/enforcement/*
app.Run();

public partial class Program // exposes Program to WebApplicationFactory tests
{
    // SDK-08: version-pinned bundle filenames (tg-<semver>.js / .js.map, incl.
    // prerelease suffixes) get the immutable cache policy; everything else in
    // wwwroot/sdk (tg.js, tg.js.map — "latest") gets the 5-minute TTL.
    [GeneratedRegex(@"^tg-\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?\.js(\.map)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PinnedSdkBundleRegex();
}
