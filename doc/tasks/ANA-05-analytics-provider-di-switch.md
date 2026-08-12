---
id: ANA-05
title: Analytics provider DI switch
phase: 1
workstream: analytics
depends_on: [ANA-03, ANA-04]
size: S
spec_refs: [D6, D7]
detail_level: full
---

# ANA-05: Analytics provider DI switch

## Objective

Provide the single config-driven registration entry point `AddTelemetryGuardAnalytics(IServiceCollection, IConfiguration)` that wires the analytics provider selected by `Analytics:Provider`, validates options at startup, and fails fast on an unknown provider — the D7 switch, adapted only where the runtime demands it.

## Spec context (self-contained)

- **D7 defines the switch verbatim:**
  ```csharp
  // appsettings.json → "Analytics": { "Provider": "ClickHouse" }
  cfg["Analytics:Provider"] switch
  {
      "Kusto"      => s.AddSingleton<IEventSink, KustoEventSink>()
                       .AddSingleton<IAnalyticsQueries, KustoAnalyticsQueries>(),
      "ClickHouse" => s.AddSingleton<IEventSink, ClickHouseEventSink>()
                       .AddSingleton<IAnalyticsQueries, ClickHouseAnalyticsQueries>(),
      var p => throw new InvalidOperationException($"Unknown analytics provider '{p}'")
  };
  ```
  Two deliberate adaptations, both documented in code comments:
  1. The Kusto provider does not exist yet (deferred to P2-05) — its case throws `NotSupportedException` instead of registering nonexistent types, preserving the fail-fast contract and the switch shape.
  2. `IAnalyticsQueries` is registered **scoped**, not singleton, because `ClickHouseAnalyticsQueries` consumes the scoped `ITenantContext` (D11) — a singleton would capture a stale/absent tenant. Sinks stay singletons: `ClickEvent`/`LabelEvent` carry `TenantId` explicitly.
- **D6/D7:** exactly two providers will ever exist. The switch is the ONLY place provider selection happens; nothing else may branch on provider name.
- Unknown or missing `Analytics:Provider` must abort startup with a clear message — silent defaults hide misconfiguration.

## Prerequisites

- ANA-03: `ClickHouseEventSink`, `ClickHouseLabelSink` (both `IHostedService` for start/drain), `ClickHouseAnalyticsOptions` (properties listed in `doc/tasks/ANA-03-clickhouse-event-sink.md` step 1).
- ANA-04: `ClickHouseAnalyticsQueries` (scoped; consumes `ITenantContext`, `IOptions<ClickHouseAnalyticsOptions>`, `IClock`).
- ANA-01: `IEventSink`, `IAnalyticsQueries`, `ILabelSink` in `TelemetryGuard.Analytics.Abstractions`.

## Implementation steps

1. Add NuGet `Microsoft.Extensions.Options.ConfigurationExtensions` and `Microsoft.Extensions.DependencyInjection.Abstractions` to `TelemetryGuard.Analytics.ClickHouse.csproj` (if not already transitively present).

2. **Create `TelemetryGuard.Analytics.ClickHouse/AnalyticsServiceCollectionExtensions.cs`:**
   ```csharp
   using Microsoft.Extensions.Configuration;
   using Microsoft.Extensions.DependencyInjection;
   using Microsoft.Extensions.Hosting;
   using TelemetryGuard.Analytics.Abstractions;

   namespace TelemetryGuard.Analytics.ClickHouse;

   public static class AnalyticsServiceCollectionExtensions
   {
       /// <summary>
       /// D7 provider switch. Lives in the ClickHouse project while it is the only
       /// implemented provider; when Kusto (P2-05) lands, move this switch to the
       /// composition root so neither provider references the other.
       /// </summary>
       public static IServiceCollection AddTelemetryGuardAnalytics(
           this IServiceCollection s, IConfiguration cfg)
       {
           s.AddOptions<ClickHouseAnalyticsOptions>()
               .Bind(cfg.GetSection("Analytics:ClickHouse"))
               .Validate(o => cfg["Analytics:Provider"] != "ClickHouse"
                              || !string.IsNullOrWhiteSpace(o.ConnectionString),
                   "Analytics:ClickHouse:ConnectionString is required when Analytics:Provider is 'ClickHouse'.")
               .Validate(o => o.EventQueueCapacity > 0 && o.EventMaxBatchSize > 0
                              && o.EventMaxBatchAgeSeconds > 0 && o.FlushMaxRetries >= 0,
                   "Analytics:ClickHouse sink tuning values must be positive.")
               .ValidateOnStart();

           _ = cfg["Analytics:Provider"] switch
           {
               // Kusto is deferred (D6 / P2-05); fail fast rather than register nothing.
               "Kusto" => throw new NotSupportedException(
                   "Analytics provider 'Kusto' is not implemented yet (spec D6, task P2-05). Use 'ClickHouse'."),

               "ClickHouse" => s
                   .AddSingleton<ClickHouseEventSink>()
                   .AddSingleton<IEventSink>(sp => sp.GetRequiredService<ClickHouseEventSink>())
                   .AddSingleton<IHostedService>(sp => sp.GetRequiredService<ClickHouseEventSink>())
                   .AddSingleton<ClickHouseLabelSink>()
                   .AddSingleton<ILabelSink>(sp => sp.GetRequiredService<ClickHouseLabelSink>())
                   .AddSingleton<IHostedService>(sp => sp.GetRequiredService<ClickHouseLabelSink>())
                   // Scoped (not singleton as in the D7 sketch): consumes scoped ITenantContext (D11).
                   .AddScoped<IAnalyticsQueries, ClickHouseAnalyticsQueries>(),

               var p => throw new InvalidOperationException($"Unknown analytics provider '{p}'")
           };
           return s;
       }
   }
   ```
   Notes: registering the concrete sink once and forwarding `IEventSink`/`IHostedService` to the SAME instance is required — two instances would split the queue from the flusher. A missing `Analytics:Provider` yields `p == null` and hits the unknown-provider throw with `'‎'` → acceptable fail-fast (message shows empty).

3. **appsettings section shape** — add to `TelemetryGuard.Api/appsettings.json` (and `appsettings.Development.json` with local values) if the Api project exists in the checkout; otherwise document it in the extension's XML doc and ANA-07/API tasks will add it:
   ```json
   {
     "Analytics": {
       "Provider": "ClickHouse",
       "ClickHouse": {
         "ConnectionString": "Host=localhost;Port=8123;Database=default;Username=default;Password=",
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
     }
   }
   ```
   Match the ClickHouse host/port/credentials to FND-02's compose file.

4. If `TelemetryGuard.Api/Program.cs` exists, add `builder.Services.AddTelemetryGuardAnalytics(builder.Configuration);` (plus a project reference Api → Analytics.ClickHouse). If API-01 has not landed, skip — API-01/API-02 consume this extension.

## Files to create or modify

- `TelemetryGuard.Analytics.ClickHouse/AnalyticsServiceCollectionExtensions.cs`
- `TelemetryGuard.Analytics.ClickHouse/TelemetryGuard.Analytics.ClickHouse.csproj` (packages)
- (conditional) `TelemetryGuard.Api/appsettings.json`, `appsettings.Development.json`, `Program.cs`

## Acceptance criteria

- `dotnet build` passes.
- Unit tests prove, using a real `ServiceCollection` + `ConfigurationBuilder.AddInMemoryCollection`:
  - Provider `"ClickHouse"` + valid connection string → `IEventSink`, `ILabelSink` resolvable as singletons; `GetServices<IHostedService>()` contains both sink instances (same references as the resolved sinks); `IAnalyticsQueries` resolvable from a scope (with stub `ITenantContext`/`IClock` registered) and NOT from the root provider as a singleton lifetime.
  - Provider `"Kusto"` → `NotSupportedException` thrown at registration.
  - Provider `"Postgres"` (or any other string) → `InvalidOperationException` containing the provider name.
  - Provider missing → exception at registration (fail fast).
  - Provider `"ClickHouse"` with empty `ConnectionString` → building the host and starting it fails via `ValidateOnStart` (`OptionsValidationException`).
- Exactly one `switch` over `Analytics:Provider` exists in the codebase (`grep -rn "Analytics:Provider" TelemetryGuard.*/ --include="*.cs"` returns only the extension class).

## Testing

- `tests/TelemetryGuard.Tests.Unit/Analytics/AnalyticsRegistrationTests.cs` covering all five bullets above. Stub `ITenantContext`/`IClock` are 3-line fakes. For the `ValidateOnStart` case use `Host.CreateApplicationBuilder` with in-memory config, register the extension plus stubs, and assert `host.StartAsync()` throws.
- No container needed — this task is pure wiring; behavior against real ClickHouse is covered by ANA-03/ANA-04/ANA-06 tests.

## Out of scope / guardrails

- **Do not implement or reference Kusto types** — the case throws until P2-05.
- **No generic cross-engine layer (D7):** the switch selects whole implementations; never add per-query branching, capability flags, or a provider-neutral query facade.
- **Tenant is never optional:** do not "fix" the scoped-queries lifetime by giving `ITenantContext` a default tenant or making it a singleton.
- Do not register RiskEngine, Data, or Redis services here — this extension owns analytics only.
- Silent fallback to a default provider is forbidden; misconfiguration must stop startup.
- No EF Core; no server-side Python/Node; scoring budget (<50 ms) implies the sink registrations must keep the single-instance queue/flusher pattern above — do not re-register `IEventSink` transiently.
