---
id: P2-02
title: "Retraining loop: model registry, promotion gate, rollback"
phase: 2
workstream: risk
depends_on: [RSK-08, DAT-03, SDK-06]
size: L
spec_refs: [D1, D3, D4, D9, D10, D11, D18, D19, "§10 Phase 2", "§11.4"]
detail_level: full
---

# P2-02: Retraining loop — model registry, promotion gate, rollback

## Objective

Turn RSK-08's one-shot `dotnet run --project TelemetryGuard.Training -- train` into a repeatable, auditable loop:

1. a **model registry** in SQL Server (`dbo.ModelRegistry`, migration `0009`) holding one row per training run with its window, label counts, metrics, artifact path + SHA-256, and a lifecycle status (`candidate | shadow | active | rejected | retired`);
2. a **`retrain` command** that rebuilds labels, retrains over the newest window, measures serving latency, compares the candidate against the incumbent, and registers the outcome — `candidate` on pass, `rejected` (never servable) on fail;
3. a **listen-only divergence report** (`divergence` command) comparing the shadow model's scores against the heuristic's enforced scores on real `tg_events` rows, including a rank-AUC comparison on the labeled subset;
4. **explicit human promotion** (`promote --status shadow|active`) and **`rollback`**, both refusing illegal transitions, both single SQL transactions, both requiring an API-host restart to take effect;
5. **serving-side registry resolution**: the API host reads the promoted row at startup and translates it into the *existing* RSK-08 `Scoring:*` keys, verifying artifact hash, `scorer_version`, and `feature_set_version` before loading anything; a background watcher reports (never applies) a pending promotion.

The heuristic (`heuristic-1`) remains the enforcing scorer until a human runs `promote --status active`. Nothing in this task can flip enforcement automatically.

## Spec context (self-contained)

- **D18 (cold start / swap mechanics)**: `IScorer` is the seam — `HeuristicScorer` ("heuristic-1", RSK-06) vs `MlNetScorer` ("lgbm-…", RSK-08). Every verdict is stamped `scorer_version` + `feature_set_version` so heuristic-era, listen-only-era, and each model generation are never confused. The pilot runs in **listen-only** (model scored and logged into `tg_events.shadow_score` / `shadow_scorer_version`, enforcing nothing) before any model takes over. **Promotion is a human decision, never automatic.**
- **D19**: the review-screen override loop doubles as the labeling loop — DAT-07's whitelist add with `Source='review_screen'` + a session id emits `label='legit', label_source='review_screen'` through `ILabelSink`. T1 rule hits are weak positives (API-06 writes them live); SDK-06's Playwright bot runs are guaranteed positives (`label_source='synthetic_bot'`, written at ingest under `Synthetic:Enabled=true`). Conversion negatives are still **deferred** (no producer task exists — RSK-08 step 3c).
- **D4**: ML.NET LightGBM, in-process, trained and served from the same .NET codebase. No Python/Node on the server (D1) — that includes the retraining loop: no notebooks, no sklearn, no ONNX conversion.
- **D3**: scoring is in-process inside a <50 ms budget. Retraining compute therefore stays in the **offline `TelemetryGuard.Training` console**, never inside the API host — the host only *reads* the registry (a single small SQL row) and loads a model file. Candidate serving latency is itself a promotion-gate criterion.
- **§6.3 / rules only raise**: the T1 floor is applied by `ScoringPipeline` via `Math.Max` identically in every mode. Retraining never changes rule semantics; the model fills the space under the floor. A shadow score is the *raw* model score with no floor applied — the divergence report must account for that (step 10).
- **§7 null semantics**: missing ≠ zero. NaN is preserved end-to-end (`HandleMissingValue = true`); a metric that cannot be computed (e.g. AUC with only one class present) is reported as `NaN`, never as 0.
- **§11.4**: the proposed listen-only window for the pilot is 2–4 weeks. Pinned here as `Training:Promotion:MinShadowHours = 336` (14 days), configurable.
- **D9/D10/D11**: Dapper + numbered DbUp SQL scripts; SQL Server connections come only from the DAT-03 factories; the SYSTEM sentinel is the only sanctioned cross-tenant path and is used by background/CLI code only.

## Parallel-execution seams (read before you touch a shared file)

P2-01..P2-05 are written to be implemented **in parallel by separate agents**. These are the only files this task shares with a sibling, and the rule for each:

| Shared file | Also touched by | Rule |
|---|---|---|
| `TelemetryGuard.Data/migrations/` | P2-01 | **P2-01 owns `0008`; this task owns `0009`.** No other P2 task adds a SQL migration. Never renumber, never edit `0001`–`0008`. |
| `TelemetryGuard.Data/migrations/README.md` | P2-01 | Both **append** one registry row each. Append-only. |
| `TelemetryGuard.Data/DataServiceCollectionExtensions.cs` | P2-01 | Both add **one** `TryAdd*` line inside `AddTelemetryGuardData()`. P2-01 adds `TryAddScoped<IPublisherSummaryRepository, …>`; this task adds `TryAddSingleton<IModelRegistryRepository, …>`. Append at the end of the method body so the diffs do not overlap. |
| `TelemetryGuard.Api/Program.cs` | P2-05 | This task **inserts a block** between `AddTelemetryGuardData()` (line ~70) and `AddScoringPipeline(...)` (line ~105), plus a log flush right after `var app = builder.Build();`. P2-05 swaps the `using` on **line 19** and **deletes** the `.AddCheck<ClickHouseHealthCheck>` line from the health-check block (~line 235). Different regions — but re-locate by **content, never by line number**, since a sibling's edit shifts them. |
| `TelemetryGuard.Api/appsettings.json` | P2-01, P2-05 | This task edits **only** the existing `"Scoring"` object. P2-01 edits only `"Rollup"`, P2-05 only `"Analytics"`. Stay inside your own section. |
| `tests/TelemetryGuard.Tests.Integration/…csproj` | P2-03 | Both add **one** `<ProjectReference>` to the same `<ItemGroup>` (this task: `TelemetryGuard.Training`; P2-03: `TelemetryGuard.Portal`). Additive. |

Nothing in this task depends on P2-01/P2-03/P2-04/P2-05 having landed, and nothing here blocks them beyond the `0009` reservation.

## Prerequisites (exact, from the shipped code)

**RSK-08 — `TelemetryGuard.Training` (console, `net8.0`, in the sln already):**

- `Program.cs` is top-level statements: builds `IConfiguration` from `appsettings.json` + environment variables, dispatches `args[0]` through a `switch` over `"build-labels"` / `"train"`, prints `PrintUsage()` for `--help`/`-h`/`help`/unknown, and documents *"Exit codes: 0 = success (or --help), 1 = bad args/config, 2 = train's AUC gate failed."* Helpers already present: `ParseWindowArgs(string[]) -> (DateOnly From, DateOnly To, string? Out)` (parses `--from`, `--to`, `--out`) and `RequireConnectionString(IConfiguration, string)`.
- `Trainer`:
  - `public TrainRunResult TrainAndExport(IReadOnlyList<MlFeatureRow> orderedRows, double minAuc, DateOnly windowFrom, DateOnly windowTo, string outDir, int droppedConflicts = 0)` — 80/20 time split, LightGBM with `HandleMissingValue = true`, exports `{outDir}/{scorerVersion}/model.zip` + `metadata.json`, `scorerVersion = $"lgbm-{yyyyMMdd}-{hash8}"`; **exports nothing when `Auc < minAuc`**.
  - `public async Task<int> RunFromDatabaseAsync(string clickHouseConnectionString, IReadOnlyList<Guid> tenantIds, DateOnly from, DateOnly to, double minAuc, float t1PositiveWeight, string outDir, CancellationToken ct = default)` — returns 0 / 1 (no rows) / 2 (gate failed). Its private `ReadLabelsAsync` / `ReadEventFeaturesAsync` are the ClickHouse reads this task refactors out (step 11).
  - `public sealed record TrainRunResult(bool GatePassed, TrainMetrics Metrics, int TrainRows, int ValidationRows, int Positives, int Negatives, int DroppedConflicts, string? ScorerVersion, string? ModelDir)` and `public sealed record TrainMetrics(double Auc, double Auprc, double F1)`.
- `LabelBuilder(string clickHouseConnectionString, string sqlConnectionString, Action<string> log)` with `public Task<LabelBuildResult> RunAsync(DateOnly from, DateOnly to, CancellationToken ct)` and `internal Task<IReadOnlyList<Guid>> ListActiveTenantIdsAsync(CancellationToken ct)` (SYSTEM-sentinel-stamped `SELECT TenantId FROM dbo.Tenants WHERE Status = 0;`). `internal` is reachable from the new files — they live in the same assembly.
- `LabelResolver.Resolve(IEnumerable<RawLabelRow> rows, float t1PositiveWeight) -> LabelResolveResult` with `RawLabelRow(string SessionId, string Label, string LabelSource, float Weight, DateTime CreatedAtUtc)`, `ResolvedLabel(string SessionId, bool Fraud, float Weight)`, `LabelResolveResult(IReadOnlyList<ResolvedLabel> Resolved, int DroppedConflicts)`.
- `TelemetryGuard.Training.csproj` references **only** `TelemetryGuard.RiskEngine.Contracts` + `TelemetryGuard.Analytics.Abstractions` (packages: `ClickHouse.Client` 7.2.2, `Dapper` 2.1.35, `Microsoft.Data.SqlClient` 5.2.2, `Microsoft.Extensions.Configuration[.Binder/.Json/.EnvironmentVariables]`, `Microsoft.ML` 4.0.2, `Microsoft.ML.LightGbm` 4.0.2). **Do not add a reference to `TelemetryGuard.RiskEngine` or `TelemetryGuard.Data`** — see step 12.6's note on the locally duplicated prediction DTO and step 8's note on the hand-rolled registry SQL.

**RSK-08 — serving side (`TelemetryGuard.RiskEngine/Scoring/`):**

- `ScoringOptions` (`SectionName = "Scoring"`): `string Scorer = "Heuristic"` (`"Heuristic"|"MlNet"`), `ScoringMode Mode = Enforce` (`Enforce|ListenOnly`), `string? ModelPath` (directory containing `model.zip` + `metadata.json`).
- `PipelineServiceCollectionExtensions.AddScoringPipeline(IServiceCollection, IConfiguration)` reads that section at composition time and: with a non-empty `ModelPath` calls `ModelMetadataReader.ReadScorerVersion(modelPath)`, registers `AddPredictionEnginePool<MlFeatureRow, MlPrediction>().FromFile(Path.Combine(modelPath, "model.zip"), watchForChanges: true)` and `MlNetScorer`; then `Mode == Enforce && Scorer == "MlNet"` → `Replace(ServiceDescriptor.Singleton<IScorer>(…MlNetScorer))`; `Mode == ListenOnly` → `TryAddSingleton<IShadowScorer>(new ShadowScorerAdapter(...))`. With an empty `ModelPath` **and** `Scorer == "MlNet"` it throws `InvalidOperationException`. **This task does not modify that method** — it feeds it different configuration values (step 5).
- `ModelMetadataReader.ReadScorerVersion(string modelDir)` reads `{modelDir}/metadata.json` and throws `InvalidOperationException` on a missing file/field.
- `ScoringPipeline` runs the shadow scorer (step 8½) only for non-whitelisted sessions, inside a try/catch, and stamps `ScoringOutcome.ShadowScore` / `ShadowScorerVersion`; the final score is `Math.Max(scored.Score, t1.Floor ?? 0)` with the scorer's own version stamp preserved.

**API-06 — `VerdictFinalizer`** writes one `ClickEvent { Kind = EventKind.Verdict, Score, Band, RuleHits, ScorerVersion, FeatureSetVersion, Features (JSON via `FraudFeatureVectorJson.Options`), ShadowScore, ShadowScorerVersion, RetentionDays, TimestampUtc }` per session through `IEventSink`, and a `LabelEvent(tenantId, sessionId, LabelValues.Fraud, LabelSources.T1Rule, …)` when rule hits fired on a non-whitelisted session.

**ANA-02 + RSK-08 ClickHouse schema** (`TelemetryGuard.Analytics.ClickHouse/schema/0001_events.sql`, `0002_training_and_shadow.sql`) — everything this task reads already exists; **no new ClickHouse script is needed**:

- `tg_events`: `tenant_id UUID`, `session_id String`, `kind LowCardinality(String)` (`'verdict'`), `rule_hits Array(String)`, `score Nullable(Int16)`, `band LowCardinality(Nullable(String))`, `scorer_version LowCardinality(Nullable(String))`, `feature_set_version Nullable(UInt16)`, `features String DEFAULT ''`, `shadow_score Nullable(Int16)`, `shadow_scorer_version LowCardinality(String) DEFAULT ''`, `timestamp DateTime64(3,'UTC')`; `ORDER BY (tenant_id, timestamp)`.
- `tg_labels`: `tenant_id UUID, session_id String, label LowCardinality(String), label_source LowCardinality(String), created_at DateTime64(3,'UTC'), weight Float32 DEFAULT 1`; plain MergeTree, 400-day TTL. Duplicates are expected and resolved at read time by `LabelResolver`.
- ClickHouse parameters use the `{name:Type}` form with `cmd.AddParameter("name", value)` (`ClickHouse.Client.Utility`).

**DAT-03 — tenancy:** `ISystemConnectionFactory.OpenSystemAsync(ct)` / `OpenForTenantAsync(Guid, ct)` (`internal sealed class SystemConnectionFactory(IConfiguration cfg)`, registered by `AddTelemetryGuardData()` as `TryAddSingleton`); `WellKnownTenants.System = 00000000-0000-0000-0000-000000000001`; RLS policy `rls.TenantIsolationPolicy` + `rls.fn_tenantPredicate`; migration rules and the number registry in `TelemetryGuard.Data/migrations/README.md` (highest in the tree today: `0007_meta_sync.sql`; **`0008` is reserved by P2-01**, so this task takes `0009` — see *Parallel-execution seams*).

**ANA-07 — worker shape to copy:** `TelemetryGuard.Api/Workers/RollupService.cs` — `BackgroundService`, `PeriodicTimer`, a private `static readonly Meter` + counters, `public async Task RunOnceAsync(CancellationToken ct)` that never throws except on shutdown cancellation, and tenant enumeration via `ISystemConnectionFactory`.

**DAT-08 — test harness:** `tests/TelemetryGuard.Tests.Integration/Sql/SqlServerFixture.cs` (`[Collection("sqlserver")]`, applies real migrations via `Migrations.RunSqlServer`, seeds `TenantA`/`TenantB`), `ClickHouseSchemaFixture` in `SchemaMigratorTests.cs` (`IClassFixture`, runs `SchemaMigrator.ApplyAsync()`), and `TestEvents.Verdict(...)` / `TestEvents.SeedAsync(connectionString, events)`.

**SDK-06 — fixture data:** `npm run bot-traffic -- --site-key <key> [--target http://localhost:8080] [--runs N] [--profile linear|grid|jitter]` drives Playwright sessions carrying `X-TG-Synthetic: <runId>`; the API writes `synthetic_bot` fraud labels only when `Synthetic:Enabled=true`.

Build hygiene: `Directory.Build.props` sets `TreatWarningsAsErrors=true`, `Nullable=enable`, `ImplicitUsings=enable` for every project.

## Implementation steps

### 1. Migration — `TelemetryGuard.Data/migrations/0009_model_registry.sql`

**`0009`, not `0008`.** `0007_meta_sync.sql` is the highest number in the tree today, but **P2-01 owns `0008`** (`0008_publisher_site_summaries.sql`) and may land in either order relative to this task — see *Parallel-execution seams*. Taking `0008` here would collide. `migrations/*.sql` is a wildcard `EmbeddedResource` (`<EmbeddedResource Include="migrations\**\*.sql" />` in `TelemetryGuard.Data.csproj`), so **no csproj change is needed**.

DbUp applies pending scripts in **name order**, so `0009` is applied after `0008` on a fresh database regardless of which task merged first. The two scripts are completely independent (different tables, no shared object), so there is no ordering hazard either way — a database that already has `0009` and later receives `0008` is fine too, because DbUp journals by script name, not by ordinal.

```sql
------------------------------------------------------------------------------
-- 0009_model_registry.sql  (P2-02)
-- Model registry: ONE ROW PER TRAINING RUN of the global LightGBM model, with
-- the candidate -> shadow -> active lifecycle and rollback (D18).
--
-- PLATFORM-SCOPED, NOT TENANT-SCOPED — deliberately no TenantId column and
-- therefore deliberately NO rls.TenantIsolationPolicy predicate. The DAT-03 RLS
-- rule covers tenant-scoped tables; ONE GLOBAL MODEL is trained across all
-- tenants' labels (per-tenant models are explicitly out of scope, P2-02), and
-- this table stores model metadata only (metrics, windows, artifact paths) —
-- never tenant data. Access is background/CLI only, through
-- ISystemConnectionFactory; nothing on the request path reads it.
--
-- Status values mirror TelemetryGuard.Data.Repositories.ModelStatuses.
------------------------------------------------------------------------------
CREATE TABLE dbo.ModelRegistry
(
    ModelId           uniqueidentifier NOT NULL CONSTRAINT DF_MR_ModelId DEFAULT (NEWID()),
    ScorerVersion     varchar(64)      NULL,      -- 'lgbm-yyyyMMdd-hash8'; NULL only for runs that exported no artifact
    FeatureSetVersion int              NOT NULL,
    Status            varchar(16)      NOT NULL,  -- 'candidate'|'shadow'|'active'|'rejected'|'retired'
    TrainedUtc        datetime2(3)     NOT NULL,
    WindowFromUtc     datetime2(3)     NOT NULL,  -- training window [From, To)
    WindowToUtc       datetime2(3)     NOT NULL,
    TrainRows         int              NOT NULL,
    ValidationRows    int              NOT NULL,
    Positives         int              NOT NULL,
    Negatives         int              NOT NULL,
    DroppedConflicts  int              NOT NULL,
    Auc               float            NOT NULL,
    Auprc             float            NOT NULL,
    F1                float            NOT NULL,
    MinAucGate        float            NOT NULL,
    ScoreP99Ms        float            NULL,      -- measured single-row Predict latency (D3 gate)
    ArtifactPath      nvarchar(400)    NULL,      -- ABSOLUTE dir holding model.zip + metadata.json
    ArtifactSha256    binary(32)       NULL,      -- SHA-256 of model.zip, re-verified at load
    MetadataJson      nvarchar(max)    NULL,      -- verbatim metadata.json (reproducibility)
    GateJson          nvarchar(max)    NULL,      -- promotion-gate decision + reasons + divergence snapshot
    RejectReason      nvarchar(400)    NULL,
    Notes             nvarchar(400)    NULL,      -- who promoted / why / forced
    ShadowSinceUtc    datetime2(3)     NULL,
    ActiveSinceUtc    datetime2(3)     NULL,
    RetiredUtc        datetime2(3)     NULL,
    CreatedUtc        datetime2(3)     NOT NULL CONSTRAINT DF_MR_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    UpdatedUtc        datetime2(3)     NULL,
    CONSTRAINT PK_ModelRegistry PRIMARY KEY CLUSTERED (ModelId),
    CONSTRAINT CK_MR_Status CHECK (Status IN ('candidate', 'shadow', 'active', 'rejected', 'retired')),
    -- Anything that may ever serve MUST carry a version + verifiable artifact.
    CONSTRAINT CK_MR_Artifact CHECK
        (Status = 'rejected'
         OR (ScorerVersion IS NOT NULL AND ArtifactPath IS NOT NULL AND ArtifactSha256 IS NOT NULL))
);
GO
-- Natural key: one row per exported model version (NULL for gate-failed runs).
CREATE UNIQUE NONCLUSTERED INDEX UX_ModelRegistry_ScorerVersion
    ON dbo.ModelRegistry (ScorerVersion) WHERE ScorerVersion IS NOT NULL;
GO
-- At most ONE active and ONE shadow row — enforced by the DATABASE, not by
-- application discipline (same rationale as D11's RLS choice).
CREATE UNIQUE NONCLUSTERED INDEX UX_ModelRegistry_SingleActive
    ON dbo.ModelRegistry (Status) WHERE Status = 'active';
GO
CREATE UNIQUE NONCLUSTERED INDEX UX_ModelRegistry_SingleShadow
    ON dbo.ModelRegistry (Status) WHERE Status = 'shadow';
GO
CREATE NONCLUSTERED INDEX IX_ModelRegistry_TrainedUtc ON dbo.ModelRegistry (TrainedUtc DESC);
GO
```

Append to `TelemetryGuard.Data/migrations/README.md`:
`| 0009 | ModelRegistry (model lifecycle: candidate/shadow/active/rejected/retired) — platform table, deliberately outside RLS | P2-02 |`

### 2. Status constants + records (`TelemetryGuard.Data`)

`TelemetryGuard.Data/Repositories/ModelStatuses.cs` — mirror the shape of the shipped `ExclusionStatuses` (string constants, not an enum; the values ARE the storage contract):

```csharp
namespace TelemetryGuard.Data.Repositories;

/// <summary>Mirrors dbo.ModelRegistry.Status (varchar(16), CK_MR_Status). String
/// constants, NOT an enum — Dapper parameters compare as strings, and these values are
/// the CLI's wire contract (TelemetryGuard.Training's promote/rollback commands).</summary>
public static class ModelStatuses
{
    /// <summary>Trained, passed every gate, NOT serving anywhere. Only a human promotes it.</summary>
    public const string Candidate = "candidate";
    /// <summary>Listen-only (D18): loaded and scored, logged to tg_events.shadow_*, enforcing NOTHING.</summary>
    public const string Shadow = "shadow";
    /// <summary>Enforcing (D18 promotion). Reached ONLY via `promote --status active`.</summary>
    public const string Active = "active";
    /// <summary>Terminal: failed the AUC / incumbent-comparison / latency gate. NEVER servable.</summary>
    public const string Rejected = "rejected";
    /// <summary>Formerly shadow/active, superseded or rolled back. Re-promotable.</summary>
    public const string Retired = "retired";

    public static readonly IReadOnlyList<string> All = [Candidate, Shadow, Active, Rejected, Retired];
}
```

`TelemetryGuard.Data/Models/ModelRegistryModels.cs`:

```csharp
namespace TelemetryGuard.Data.Models;

/// <summary>One dbo.ModelRegistry row (list/inspection shape).</summary>
public sealed record ModelRegistryEntry(
    Guid ModelId, string? ScorerVersion, int FeatureSetVersion, string Status,
    DateTime TrainedUtc, DateTime WindowFromUtc, DateTime WindowToUtc,
    int TrainRows, int ValidationRows, int Positives, int Negatives, int DroppedConflicts,
    double Auc, double Auprc, double F1, double MinAucGate, double? ScoreP99Ms,
    string? ArtifactPath, byte[]? ArtifactSha256, string? RejectReason, string? Notes,
    DateTime? ShadowSinceUtc, DateTime? ActiveSinceUtc, DateTime? RetiredUtc, DateTime CreatedUtc);

/// <summary>The single row the serving host must load: 'active' wins over 'shadow'
/// (at most one model is loaded per process — see P2-02 step 4).</summary>
public sealed record ServingModel(
    Guid ModelId, string ScorerVersion, string Status,
    string ArtifactPath, byte[] ArtifactSha256, int FeatureSetVersion);
```

### 3. Registry repository (`TelemetryGuard.Data`, read side)

`TelemetryGuard.Data/Repositories/IModelRegistryRepository.cs` + `ModelRegistryRepository.cs`:

```csharp
public interface IModelRegistryRepository
{
    /// <summary>The row this process should serve: the 'active' row if one exists,
    /// otherwise the 'shadow' row, otherwise null (pure heuristic).</summary>
    Task<ServingModel?> GetServingModelAsync(CancellationToken ct);

    Task<IReadOnlyList<ModelRegistryEntry>> ListRecentAsync(int limit, CancellationToken ct);
}

/// <summary>
/// Dapper repository over dbo.ModelRegistry (P2-02). PLATFORM-scoped, so unlike every
/// tenant repository it takes <see cref="ISystemConnectionFactory"/> (DAT-03's
/// background-job factory) and is registered as a SINGLETON — there is no ambient
/// tenant and nothing on the request path may call it.
///
/// PUBLIC (the tenant repositories are internal) because TelemetryGuard.Api's
/// composition root constructs it directly, before the service provider exists, to
/// resolve the promoted model for AddScoringPipeline (P2-02 step 5).
///
/// WRITE side lives in TelemetryGuard.Training (ModelRegistryClient) — that project
/// deliberately does not reference TelemetryGuard.Data (RSK-08). The two share the
/// column contract of migration 0009; RegistryRoundTripTests proves they agree.
/// </summary>
public sealed class ModelRegistryRepository(ISystemConnectionFactory systemConnections)
    : IModelRegistryRepository
{
    public async Task<ServingModel?> GetServingModelAsync(CancellationToken ct)
    {
        await using var conn = await systemConnections.OpenSystemAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ServingModel>(new CommandDefinition(
            """
            SELECT TOP (1) ModelId, ScorerVersion, Status, ArtifactPath, ArtifactSha256, FeatureSetVersion
            FROM dbo.ModelRegistry
            WHERE Status IN (@Active, @Shadow)
            ORDER BY CASE Status WHEN @Active THEN 0 ELSE 1 END;
            """,
            new { Active = ModelStatuses.Active, Shadow = ModelStatuses.Shadow },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ModelRegistryEntry>> ListRecentAsync(int limit, CancellationToken ct) { /* SELECT TOP (@limit) … ORDER BY CreatedUtc DESC */ }
}
```

Register in `DataServiceCollectionExtensions.AddTelemetryGuardData()` next to the other repositories:

```csharp
// P2-02 model registry — SINGLETON (platform table, no ambient tenant; reads go
// through ISystemConnectionFactory, never a request-path connection).
services.TryAddSingleton<Repositories.IModelRegistryRepository, Repositories.ModelRegistryRepository>();
```

Also add the composition-root escape hatch used by step 5 (new file `TelemetryGuard.Data/SystemConnections.cs`), so that **no new raw `SqlConnection` is opened anywhere** — `SqlHealthCheck`'s "only place" comment stays true:

```csharp
namespace TelemetryGuard.Data;

/// <summary>
/// Composition-root escape hatch (P2-02): an <see cref="ISystemConnectionFactory"/>
/// built straight from configuration, for the ONE caller that needs a SYSTEM-stamped
/// connection BEFORE the service provider exists — TelemetryGuard.Api/Program.cs's
/// model-registry bootstrap. Every other caller injects ISystemConnectionFactory.
/// </summary>
public static class SystemConnections
{
    public static ISystemConnectionFactory FromConfiguration(IConfiguration cfg)
        => new SystemConnectionFactory(cfg);
}
```

### 4. Startup resolution — `TelemetryGuard.Api/Startup/ModelRegistryBootstrap.cs`

The registry decides which model the process loads; it does so by writing the **existing** RSK-08 `Scoring:*` keys into configuration, so `AddScoringPipeline` is reused verbatim.

```csharp
namespace TelemetryGuard.Api.Startup;

/// <summary>Outcome of resolving the promoted model at startup. <see cref="ModelDir"/>
/// is null when nothing may be loaded (no promoted row, or the artifact failed
/// verification) — the heuristic then enforces, which is always the SAFE direction.</summary>
public sealed record ServingModelState(
    string? ScorerVersion, string? Status, string? ModelDir, IReadOnlyList<string> Log);

public static class ModelRegistryBootstrap
{
    /// <summary>
    /// P2-02: reads dbo.ModelRegistry's promoted row and verifies its artifact before
    /// the host loads anything. Fail-SAFE by construction: ANY failure (SQL down,
    /// missing dir, hash mismatch, scorer_version mismatch, feature-set mismatch)
    /// logs and returns a state with ModelDir = null, i.e. the heuristic keeps
    /// enforcing. It can never fail OPEN toward model enforcement.
    /// Blocking on the async read is deliberate: this runs once, at composition time,
    /// before the host starts (no synchronization context to deadlock on).
    /// </summary>
    public static ServingModelState Resolve(
        IConfiguration configuration, int expectedFeatureSetVersion, TimeSpan timeout)
    {
        var log = new List<string>();
        try
        {
            var repo = new ModelRegistryRepository(SystemConnections.FromConfiguration(configuration));
            using var cts = new CancellationTokenSource(timeout);
            var serving = repo.GetServingModelAsync(cts.Token).GetAwaiter().GetResult();
            if (serving is null)
            {
                log.Add("Model registry holds no active/shadow row — heuristic enforcing, no model loaded.");
                return new ServingModelState(null, null, null, log);
            }

            var dir = ResolveArtifactDir(configuration, serving);
            var error = VerifyArtifact(dir, serving, expectedFeatureSetVersion);
            if (error is not null)
            {
                log.Add($"Model {serving.ScorerVersion} ({serving.Status}) REJECTED at load: {error}. " +
                        "Heuristic enforcing, no model loaded.");
                return new ServingModelState(serving.ScorerVersion, serving.Status, null, log);
            }

            log.Add($"Model registry: serving {serving.ScorerVersion} as '{serving.Status}' from {dir}.");
            return new ServingModelState(serving.ScorerVersion, serving.Status, dir, log);
        }
        catch (Exception ex)
        {
            log.Add($"Model registry read failed ({ex.GetType().Name}: {ex.Message}); " +
                    "heuristic enforcing, no model loaded.");
            return new ServingModelState(null, null, null, log);
        }
    }

    /// <summary>Scoring:Registry:ArtifactRoot (when set) + scorer_version wins over the
    /// stored absolute path — deployments mount the artifact volume elsewhere than the
    /// machine that trained the model.</summary>
    internal static string ResolveArtifactDir(IConfiguration configuration, ServingModel serving)
    {
        var root = configuration["Scoring:Registry:ArtifactRoot"];
        return string.IsNullOrWhiteSpace(root)
            ? serving.ArtifactPath
            : Path.Combine(root, serving.ScorerVersion);
    }

    /// <summary>Returns null when the artifact is loadable, else the reason. Checks, in
    /// order: model.zip exists → SHA-256 matches the registry → metadata.json's
    /// scorer_version equals the registry's → metadata.json's feature_set_version equals
    /// the running FraudFeatureVector.FeatureSetVersion (D18 lineage: never serve a model
    /// trained against a different feature set).</summary>
    internal static string? VerifyArtifact(string dir, ServingModel serving, int expectedFeatureSetVersion);

    /// <summary>Translates the state into the RSK-08 Scoring keys. This is the ONLY
    /// promotion mechanism: 'active' -> MlNet enforces, 'shadow' -> heuristic enforces +
    /// listen-only shadow scoring, nothing -> pure heuristic (and Scoring:ModelPath from
    /// appsettings is explicitly cleared, so a stale config value can never resurrect a
    /// demoted model).</summary>
    public static Dictionary<string, string?> ToScoringOverrides(ServingModelState state) =>
        state.ModelDir is null
            ? new() { ["Scoring:ModelPath"] = "", ["Scoring:Scorer"] = "Heuristic", ["Scoring:Mode"] = "Enforce" }
            : state.Status == ModelStatuses.Active
                ? new() { ["Scoring:ModelPath"] = state.ModelDir, ["Scoring:Scorer"] = "MlNet",     ["Scoring:Mode"] = "Enforce" }
                : new() { ["Scoring:ModelPath"] = state.ModelDir, ["Scoring:Scorer"] = "Heuristic", ["Scoring:Mode"] = "ListenOnly" };
}
```

**One model per process.** `AddPredictionEnginePool<MlFeatureRow, MlPrediction>()` is a single non-keyed registration, so exactly one model file can be loaded. When both an `active` and a `shadow` row exist, `active` wins and the shadow row is logged as "not loaded (an active model is serving)". Live shadow-vs-heuristic comparison is therefore available only while the heuristic enforces — that is exactly the pilot situation this loop is for; the promotion gate for later generations falls back to the offline holdout comparison (step 9). Running two models in one process is out of scope.

### 5. Wiring in `TelemetryGuard.Api/Program.cs`

Insert **after** `builder.Services.AddTelemetryGuardData();` (currently line ~70) and **before** `builder.Services.AddScoringPipeline(builder.Configuration);` (currently line ~105):

```csharp
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
    ? ModelRegistryBootstrap.Resolve(
        builder.Configuration, FraudFeatureVector.FeatureSetVersion, TimeSpan.FromSeconds(5))
    : new ServingModelState(null, null, null,
        ["Scoring:ModelSource != Registry — Scoring:ModelPath from configuration is authoritative (RSK-08 pilot mode)."]);

if (useRegistry)
{
    builder.Configuration.AddInMemoryCollection(ModelRegistryBootstrap.ToScoringOverrides(modelState));
}
builder.Services.AddSingleton(modelState);   // what THIS process actually loaded — the watcher compares against it
builder.Services.Configure<ModelRegistryOptions>(
    builder.Configuration.GetSection(ModelRegistryOptions.SectionName));
builder.Services.AddHostedService<TelemetryGuard.Api.Workers.ModelRegistryWatcher>();
```

Immediately after `var app = builder.Build();`, flush the bootstrap log through the real logger (it is produced before logging exists):

```csharp
foreach (var line in modelState.Log) app.Logger.LogInformation("{ModelRegistryBootstrap}", line);
```

`TelemetryGuard.Api/Options/ModelRegistryOptions.cs`:

```csharp
public sealed class ModelRegistryOptions
{
    public const string SectionName = "Scoring:Registry";
    /// <summary>Overrides the registry's stored absolute ArtifactPath (deployments mount
    /// the model volume elsewhere). Empty = use the stored path as-is.</summary>
    public string ArtifactRoot { get; set; } = "";
    public int PollMinutes { get; set; } = 5;
}
```

`TelemetryGuard.Api/appsettings.json` — extend the EXISTING `"Scoring"` object (do not create a second one); `ScoringOptions` ignores the two extra keys, which are read by the bootstrap/watcher:

```json
  "Scoring": {
    "BudgetMs": 50,
    "Bands": { "AllowMax": 30, "ChallengeMax": 70 },
    "Heuristic": {},
    // P2-02: "Registry" (default) — dbo.ModelRegistry's promoted row decides which
    // model loads and whether it enforces; an empty registry = pure heuristic, so a
    // fresh deployment behaves exactly as before. "Config" restores RSK-08's manual
    // pilot mode where Scoring:ModelPath/Scorer/Mode below are authoritative.
    "ModelSource": "Registry",
    "Registry": { "ArtifactRoot": "", "PollMinutes": 5 }
  },
```

### 6. Watcher — `TelemetryGuard.Api/Workers/ModelRegistryWatcher.cs`

Copy `RollupService`'s shape (`BackgroundService` + `PeriodicTimer` + private static `Meter`, public `RunOnceAsync` for tests, never throws except on shutdown):

```csharp
/// <summary>
/// P2-02: observability for promotions — it NEVER applies one. Every
/// Scoring:Registry:PollMinutes it compares dbo.ModelRegistry's promoted row against
/// what THIS process loaded (ServingModelState) and logs a warning + increments
/// tg.model.promotion_pending when they differ. The enforcing scorer is fixed for the
/// lifetime of the process (D18 lineage: one process, one scorer_version stamp);
/// applying a promotion is a restart, performed by a human.
/// </summary>
public sealed class ModelRegistryWatcher(
    IModelRegistryRepository registry,
    ServingModelState loaded,
    IOptions<ModelRegistryOptions> options,
    ILogger<ModelRegistryWatcher> log) : BackgroundService
{
    private static readonly Meter Meter = new("TelemetryGuard.ModelRegistry");
    private static readonly Counter<long> PromotionPending = Meter.CreateCounter<long>("tg.model.promotion_pending");
    private static readonly Counter<long> ReadFailures     = Meter.CreateCounter<long>("tg.model.registry_read_failures");
    …
}
```

Log message (Warning): `"Model promotion pending: registry serves {RegistryVersion} as '{RegistryStatus}', this process serves {LoadedVersion} — restart the host to apply."` Emit it at most once per distinct registry version to avoid log spam (remember the last reported version).

The watcher also takes `IConfiguration` and **no-ops entirely** (one Debug line at startup, `ExecuteAsync` returns immediately) when `Scoring:ModelSource` is not `"Registry"` — in RSK-08 pilot mode the registry is not what this process loaded, so comparing them would produce false alarms.

### 7. Training config — `TelemetryGuard.Training/appsettings.json`

```json
{
  "ConnectionStrings": {
    "ClickHouse": "Host=localhost;Port=8123;Database=telemetry_guard;Username=tg;Password=tg-dev-password",
    "Main": "Server=localhost,1433;Database=TelemetryGuard;User Id=sa;Password=TgDev!Str0ngPassw0rd;TrustServerCertificate=true"
  },
  "Training": {
    "MinAuc": 0.85,
    "T1PositiveWeight": 0.6,
    "ArtifactRoot": "artifacts/models",
    "Retrain": {
      "WindowDays": 30,
      "MinPositives": 500,
      "MinNegatives": 500,
      "MinIntervalHours": 144
    },
    "Promotion": {
      "MaxAucRegression": 0.0,
      "MaxAuprcRegression": 0.01,
      "MaxScoreP99Ms": 5.0,
      "MinShadowHours": 336,
      "MinShadowSessions": 1000,
      "MinLabeledShadowSessions": 200,
      "MinLiveAucAdvantage": 0.0
    },
    "Bands": { "AllowMax": 30, "ChallengeMax": 70 }
  }
}
```

`Training:Bands` MUST mirror the API's `Scoring:Bands` values — the divergence report maps scores to bands offline and this console deliberately does not reference `TelemetryGuard.Api`. Every key is overridable per run through environment variables (the config builder already calls `AddEnvironmentVariables()` with no prefix), e.g. `Training__Retrain__MinPositives=20` for a fixture-sized run.

### 8. Registry write side — `TelemetryGuard.Training/ModelRegistryClient.cs`

Hand-rolled Dapper on a SYSTEM-sentinel-stamped `SqlConnection`, exactly like `LabelBuilder.ListActiveTenantIdsAsync` (same `EXEC sp_set_session_context @key = N'TenantId', @value = @tid, @read_only = 1` preamble, same reason: this console must not take a dependency on `TelemetryGuard.Data`). Carry a doc comment pointing at `ModelRegistryRepository` as the read-side twin and at migration `0009` as the column contract.

```csharp
public sealed record ModelRegistryRow(
    Guid ModelId, string? ScorerVersion, int FeatureSetVersion, string Status,
    DateTime TrainedUtc, DateTime WindowFromUtc, DateTime WindowToUtc,
    int Positives, int Negatives, double Auc, double Auprc, double F1, double? ScoreP99Ms,
    string? ArtifactPath, string? RejectReason, string? Notes,
    DateTime? ShadowSinceUtc, DateTime? ActiveSinceUtc);

public sealed record NewModelRun(
    string? ScorerVersion, int FeatureSetVersion, string Status,
    DateTime TrainedUtc, DateOnly WindowFrom, DateOnly WindowTo,
    int TrainRows, int ValidationRows, int Positives, int Negatives, int DroppedConflicts,
    double Auc, double Auprc, double F1, double MinAucGate, double? ScoreP99Ms,
    string? ArtifactPath, byte[]? ArtifactSha256, string? MetadataJson, string? GateJson,
    string? RejectReason);

public sealed class ModelRegistryClient(string sqlConnectionString)
{
    public Task<ModelRegistryRow?> GetIncumbentAsync(CancellationToken ct);          // active, else shadow, else null
    public Task<ModelRegistryRow?> GetByScorerVersionAsync(string scorerVersion, CancellationToken ct);
    public Task<ModelRegistryRow?> GetNewestRunAsync(CancellationToken ct);          // for MinIntervalHours
    public Task<IReadOnlyList<ModelRegistryRow>> ListRecentAsync(int limit, CancellationToken ct);
    public Task<Guid> InsertRunAsync(NewModelRun run, CancellationToken ct);
    public Task PromoteAsync(string scorerVersion, string toStatus, string? note, CancellationToken ct);
    public Task<string> RollbackAsync(string? toScorerVersion, string? note, CancellationToken ct);
}
```

`PromoteAsync` — ONE transaction (`await using var tx = await conn.BeginTransactionAsync(ct);`, every `CommandDefinition` carries `transaction: tx, cancellationToken: ct`, matching `EnforcementQueueRepository`):

```sql
-- 1. retire whatever currently holds the target status (keeps UX_ModelRegistry_Single* valid)
UPDATE dbo.ModelRegistry
SET Status = 'retired', RetiredUtc = SYSUTCDATETIME(), UpdatedUtc = SYSUTCDATETIME()
WHERE Status = @ToStatus;

-- 2. promote the target — 'rejected' rows can NEVER be promoted, and the OUTPUT tells
--    the caller whether anything actually moved (0 rows => refuse with exit code 4)
UPDATE dbo.ModelRegistry
SET Status         = @ToStatus,
    ShadowSinceUtc = CASE WHEN @ToStatus = 'shadow' THEN SYSUTCDATETIME() ELSE ShadowSinceUtc END,
    ActiveSinceUtc = CASE WHEN @ToStatus = 'active' THEN SYSUTCDATETIME() ELSE ActiveSinceUtc END,
    RetiredUtc     = NULL,
    Notes          = @Note,
    UpdatedUtc     = SYSUTCDATETIME()
OUTPUT inserted.ModelId
WHERE ScorerVersion = @ScorerVersion
  AND Status IN ('candidate', 'shadow', 'retired');
```

`RollbackAsync` — ONE transaction: retire the current `active` row, then set the target to `active`. Default target = the most recently retired row that once carried `ActiveSinceUtc IS NOT NULL` (`ORDER BY ActiveSinceUtc DESC`); if there is none, refuse (exit 4) with `"nothing to roll back to — use `promote --status active` explicitly"`. Rolling back to *no* model is `--to none`, which just retires the active row (the host then falls back to the heuristic at restart).

### 9. Promotion gate — `TelemetryGuard.Training/PromotionGate.cs` (pure, no I/O)

```csharp
public sealed record CandidateMetrics(double Auc, double Auprc, double F1, double? ScoreP99Ms, int Positives, int Negatives);
public sealed record IncumbentMetrics(string ScorerVersion, double Auc, double Auprc);
public sealed record TrainGateOptions(double MaxAucRegression, double MaxAuprcRegression, double MaxScoreP99Ms, int MinPositives, int MinNegatives);
public sealed record EnforceGateOptions(int MinShadowHours, int MinShadowSessions, int MinLabeledShadowSessions, double MinLiveAucAdvantage);
public sealed record GateDecision(bool Passed, IReadOnlyList<string> Reasons);

public static class PromotionGate
{
    /// <summary>Run at REGISTRATION time (`retrain`). A failure registers the run as
    /// 'rejected' — it can never be promoted afterwards. Checks: label counts per class;
    /// AUC/AUPRC vs the incumbent within the configured regression tolerance (no
    /// incumbent = only the absolute Training:MinAuc gate, already applied by
    /// Trainer.TrainAndExport); measured single-row Predict p99 under
    /// Training:Promotion:MaxScoreP99Ms (D3 — a model too slow to serve is not a model).</summary>
    public static GateDecision EvaluateCandidate(
        CandidateMetrics candidate, IncumbentMetrics? incumbent, TrainGateOptions options);

    /// <summary>Run at `promote --status active`. Passes when EITHER
    /// (a) the model has been 'shadow' for >= MinShadowHours with >= MinShadowSessions
    ///     shadow-scored sessions AND, on the labeled subset (>= MinLabeledShadowSessions),
    ///     its live rank-AUC beats the enforced heuristic's by >= MinLiveAucAdvantage
    ///     (the §11.4 listen-only pilot path); OR
    /// (b) an ACTIVE model incumbent already exists and this candidate beat it on the
    ///     offline holdout at registration time (generation 2+, where live shadow
    ///     evaluation is impossible — only one model loads per process).
    /// Reasons always list every unmet condition so the CLI can print them verbatim.</summary>
    public static GateDecision EvaluateEnforcePromotion(
        ModelRegistryRow candidate, DateTime nowUtc, DivergenceSummary? divergence,
        bool hasActiveIncumbent, EnforceGateOptions options);
}
```

`--force` on `promote` bypasses `EvaluateEnforcePromotion` **only**, prints every unmet reason as a warning, and records `Notes = "FORCED: <reasons>"`. It can never bypass `Status = 'rejected'`.

### 10. Divergence report — `TelemetryGuard.Training/DivergenceReport.cs`

Pure computation + a per-tenant ClickHouse read (hand-written SQL, as RSK-08 established for the offline side — this is **not** an `IAnalyticsQueries` intent; D7's abstraction covers the product's read path, not the trainer).

```csharp
public sealed record DivergenceRow(string SessionId, int EnforcedScore, int ShadowScore, bool? Fraud);

public sealed record DivergenceSummary(
    string ShadowScorerVersion,
    int Sessions,                 // comparable sessions (no rule floor applied — see below)
    int RuleFlooredSessions,      // excluded from the band comparison, reported for honesty
    int LabeledSessions,
    double BandAgreementRate,
    IReadOnlyList<int> BandMatrix, // 9 counts, row = enforced band, col = shadow band (allow/challenge/block)
    double MeanAbsDelta,
    int ShadowWouldBlockMore,
    int ShadowWouldBlockFewer,
    double EnforcedAuc,           // NaN when the labeled subset has only one class — never 0
    double ShadowAuc);

public static class DivergenceReport
{
    public static DivergenceSummary Compute(
        string shadowScorerVersion, IReadOnlyList<DivergenceRow> rows, int ruleFlooredSessions,
        int allowMax, int challengeMax);

    /// <summary>Rank (Mann-Whitney) AUC with averaged ranks for ties. Returns NaN when
    /// either class is absent — missing ≠ zero (§7).</summary>
    internal static double RankAuc(IReadOnlyList<(double Score, bool Positive)> rows);
}
```

Per-tenant read (`tenant_id` leads every filter — D11; the tenant list comes from `LabelBuilder.ListActiveTenantIdsAsync`):

```sql
SELECT session_id, score, shadow_score, length(rule_hits) AS floored
FROM tg_events
WHERE tenant_id = {t:UUID}
  AND kind = 'verdict'
  AND timestamp >= {f:DateTime64(3,'UTC')} AND timestamp < {to:DateTime64(3,'UTC')}
  AND shadow_scorer_version = {sv:String}
  AND shadow_score IS NOT NULL AND score IS NOT NULL
  AND NOT has(rule_hits, 'whitelisted')
```

**Why the floor matters:** `tg_events.score` is the ENFORCED score, already raised by the T1 floor (`Math.Max`), while `shadow_score` is the raw model output. Rows with `length(rule_hits) > 0` are therefore counted (`RuleFlooredSessions`) but **excluded from the band comparison and the AUC comparison** — on the remaining rows the enforced score is exactly the heuristic's score, which is the comparison the promotion decision needs. Never "reconstruct" a floor: the floor value is not stored on the event.

Labels for the labeled subset come from the same per-tenant `tg_labels` read the trainer uses, resolved through the shipped `LabelResolver.Resolve(rows, t1PositiveWeight)` — never re-implemented.

Output: a console table plus `{--out}/divergence-{from}_{to}.json` (default `--out artifacts/divergence`, already covered by `.gitignore`'s `artifacts/`). `promote --status active` re-runs the same computation for the shadow model's window and stores the summary in `GateJson`.

### 11. Trainer refactor (`TelemetryGuard.Training/Trainer.cs`)

The `retrain` command needs the loaded rows *and* the `TrainRunResult`, which `RunFromDatabaseAsync` swallows into an exit code. Extract the loading half; behavior of the existing command must not change.

```csharp
public sealed record TrainingSet(IReadOnlyList<MlFeatureRow> OrderedRows, int DroppedConflicts);

/// <summary>The ClickHouse half of RunFromDatabaseAsync, extracted for P2-02's retrain
/// command: per tenant (D11), read tg_labels, resolve via LabelResolver, join tg_events
/// on (tenant_id, session_id), deserialize `features` with FraudFeatureVectorJson.Options,
/// map through MlFeatureMapper, then order by event timestamp ascending across ALL tenants.</summary>
public async Task<TrainingSet> LoadTrainingSetAsync(
    string clickHouseConnectionString, IReadOnlyList<Guid> tenantIds,
    DateOnly from, DateOnly to, float t1PositiveWeight, CancellationToken ct = default);
```

`RunFromDatabaseAsync` becomes a thin wrapper over `LoadTrainingSetAsync` + `TrainAndExport` with **identical** return codes (1 when `OrderedRows.Count == 0`, else 0/2) and identical console output — RSK-08's tests must keep passing untouched.

Add the feature-set guard used by `retrain` (new method on `Trainer`, or a small `FeatureSetGuard` static — either is fine, keep it next to the ClickHouse reads):

```sql
SELECT feature_set_version, count()
FROM tg_events
WHERE tenant_id = {t:UUID} AND kind = 'verdict' AND features != ''
  AND timestamp >= {f:DateTime64(3,'UTC')} AND timestamp < {to:DateTime64(3,'UTC')}
GROUP BY feature_set_version
```

### 12. `retrain` command — `TelemetryGuard.Training/RetrainRunner.cs`

`dotnet run --project TelemetryGuard.Training -- retrain [--from yyyy-MM-dd] [--to yyyy-MM-dd] [--out <dir>] [--skip-label-build] [--force]`

**Visibility (load-bearing).** `RetrainRunner`, `ModelRegistryClient`, `PromotionGate`, `DivergenceReport` and every record they expose must be **`public`**, and `RetrainRunner`'s entry point must be a `public static Task<int> RunAsync(string[] args, IConfiguration config, CancellationToken ct)` that `Program.cs` calls and that `RetrainCycleTests` calls **in-process** (never by shelling out to `dotnet run`). Reason: `TelemetryGuard.Training.csproj` deliberately carries **no `InternalsVisibleTo`**, and its own comment explains why — granting it would expose the console's top-level-statement `Program` type to the test assemblies and collide with `TelemetryGuard.Api`'s `public partial class Program` under `WebApplicationFactory<Program>` (CS0433). **Do not "fix" the visibility problem by adding `InternalsVisibleTo`; make the new types public instead.** The existing `internal LabelBuilder.ListActiveTenantIdsAsync` stays internal and is reachable only from inside the Training assembly — call it from `RetrainRunner`, not from a test.

Default window when `--from`/`--to` are omitted: `[today - Training:Retrain:WindowDays, today)` (UTC). Steps, in order — each one prints a line and the whole run prints a final summary:

1. **Cadence guard**: `GetNewestRunAsync`; when `TrainedUtc` is newer than `now - Training:Retrain:MinIntervalHours` and `--force` was not passed → log `"last run {TrainedUtc} is younger than MinIntervalHours; nothing to do"` and **exit 0** (a cron calling this hourly must be harmless).
2. **Labels**: `new LabelBuilder(chCs, sqlCs, Console.WriteLine).RunAsync(from, to, ct)` unless `--skip-label-build`; print its per-source counters.
3. **Feature-set guard**: run the query from step 11 over every active tenant. If any row in the window carries a `feature_set_version` different from `FraudFeatureVector.FeatureSetVersion`, print the histogram and **exit 3** — *"the window straddles a feature-set boundary; narrow --from/--to past it"*. Never train across the boundary silently (RSK-08 guardrail).
4. **Load**: `LoadTrainingSetAsync(...)`; count positives/negatives. Below `Training:Retrain:MinPositives` / `MinNegatives` → print the counts and **exit 3**, registering NOTHING (no training run happened).
5. **Train**: `TrainAndExport(set.OrderedRows, minAuc, from, to, outDir, set.DroppedConflicts)`.
   - `GatePassed == false` → `InsertRunAsync` with `Status = ModelStatuses.Rejected`, `ScorerVersion = null`, `ArtifactPath = null`, `RejectReason = $"validation AUC {auc:F4} < Training:MinAuc {minAuc:F4}"` → **exit 2** (RSK-08's documented meaning, now also recorded).
6. **Latency measurement** (D3 gate): load the exported `model.zip` with `mlContext.Model.Load(path, out _)` → `mlContext.Model.CreatePredictionEngine<MlFeatureRow, MlPredictionRow>(model)`; score up to 2 000 validation rows (3 warm-up passes discarded), take the p99 of the per-call elapsed milliseconds. `MlPredictionRow` is a **local** 3-field DTO (`[ColumnName("PredictedLabel")] public bool PredictedLabel; public float Probability; public float Score;`) — deliberately duplicated from `TelemetryGuard.RiskEngine.Scoring.MlPrediction` because Training must not reference `TelemetryGuard.RiskEngine`, and `MlPrediction` cannot move into `TelemetryGuard.RiskEngine.Contracts` without breaking its zero-package-reference rule (`ColumnName` lives in `Microsoft.ML.Data`). Note the duplication in both files' doc comments.
7. **Incumbent comparison**: `GetIncumbentAsync()` → `PromotionGate.EvaluateCandidate(...)`.
8. **Register**: `InsertRunAsync` with `Status = candidate` (pass) or `rejected` (fail, `RejectReason` = the joined reasons), `ArtifactPath = Path.GetFullPath(result.ModelDir)`, `ArtifactSha256 = SHA-256 of model.zip`, `MetadataJson` = the verbatim `metadata.json` text, `GateJson` = the serialized `GateDecision`.
9. **Print the next step verbatim** — the loop never takes it itself:
   `Registered candidate lgbm-…. Nothing is serving it. Next: dotnet run --project TelemetryGuard.Training -- promote --model lgbm-… --status shadow`
   Exit 0 on `candidate`, 2 on `rejected`.

### 13. CLI surface — `TelemetryGuard.Training/Program.cs`

Extend the existing `switch` and `PrintUsage()`; keep the existing commands and exit-code meanings.

```
build-labels --from <yyyy-MM-dd> --to <yyyy-MM-dd>
train        --from <yyyy-MM-dd> --to <yyyy-MM-dd> [--out <dir>]
retrain      [--from <d>] [--to <d>] [--out <dir>] [--skip-label-build] [--force]
divergence   --from <d> --to <d> [--model <scorer-version>] [--out <dir>]
promote      --model <scorer-version> --status shadow|active [--note "<text>"] [--force]
rollback     [--to <scorer-version>|none] [--note "<text>"]
models       [--limit 20]

Exit codes: 0 = success (or --help/nothing to do), 1 = bad args/config,
            2 = training/candidate gate failed (run recorded as 'rejected'),
            3 = not enough labels / mixed feature_set_version (nothing recorded),
            4 = promotion or rollback refused (illegal transition or unmet gate).
```

`promote` deliberately uses `--status`, **not** `--to`: `--to` is already the window-end argument of `ParseWindowArgs` and reusing it would be a foot-gun. `promote --status active` prints, on success:
`Promoted lgbm-… to 'active'. RESTART the API host to apply — a running process never changes its enforcing scorer (D18 lineage).`

### 14. Runbook — `doc/runbooks/model-retraining.md`

Written for an operator with **no production traffic**, i.e. the SDK-06 fixture path end to end:

1. `./scripts/dev-up.sh && ./scripts/dev-seed.sh`; run the API with `Synthetic:Enabled=true` (`appsettings.Development.json`) and note the seeded site key.
2. Positives: `cd TelemetryGuard.Sdk && npm run bot-traffic -- --site-key <key> --runs 40 --profile linear` (repeat with `grid`, `jitter`) → `synthetic_bot` labels at ingest, plus `t1_rule` labels from API-06.
3. Negatives: for a handful of clean sessions, `POST /admin/whitelist` with `{"type":"ip","value":"…","source":"review_screen","sessionId":"<sid>","reason":"real customer"}` (API-07 requires `sessionId` matching `^[A-Za-z0-9_-]{8,64}$` when `source=review_screen`), which makes DAT-07 emit `legit`/`review_screen` labels (D19).
4. Retrain with fixture-sized thresholds:
   `Training__Retrain__MinPositives=20 Training__Retrain__MinNegatives=20 Training__MinAuc=0.6 dotnet run --project TelemetryGuard.Training -- retrain --from <d> --to <d>`
5. `promote --model <sv> --status shadow`, restart the API, generate more fixture traffic, then `divergence --from <d> --to <d>`.
6. `promote --model <sv> --status active` (add `Training__Promotion__MinShadowHours=0 Training__Promotion__MinShadowSessions=10 Training__Promotion__MinLabeledShadowSessions=5` for a dev walkthrough, or `--force`), restart, confirm new verdicts stamp `scorer_version = lgbm-…`.
7. `rollback`, restart, confirm verdicts stamp `heuristic-1` again.
8. Cron/ops example (a scheduled `dotnet run … retrain` on the training host, weekly) plus the explicit warning that **CI never trains** and never touches a real database.

## Files to create or modify

**Create**

- `TelemetryGuard.Data/migrations/0009_model_registry.sql`
- `TelemetryGuard.Data/Repositories/ModelStatuses.cs`
- `TelemetryGuard.Data/Repositories/IModelRegistryRepository.cs`
- `TelemetryGuard.Data/Repositories/ModelRegistryRepository.cs`
- `TelemetryGuard.Data/Models/ModelRegistryModels.cs`
- `TelemetryGuard.Data/SystemConnections.cs`
- `TelemetryGuard.Api/Startup/ModelRegistryBootstrap.cs`
- `TelemetryGuard.Api/Options/ModelRegistryOptions.cs`
- `TelemetryGuard.Api/Workers/ModelRegistryWatcher.cs`
- `TelemetryGuard.Training/ModelRegistryClient.cs`
- `TelemetryGuard.Training/PromotionGate.cs`
- `TelemetryGuard.Training/DivergenceReport.cs`
- `TelemetryGuard.Training/RetrainRunner.cs`
- `doc/runbooks/model-retraining.md`
- `tests/TelemetryGuard.Tests.Unit/Training/PromotionGateTests.cs`
- `tests/TelemetryGuard.Tests.Unit/Training/DivergenceReportTests.cs`
- `tests/TelemetryGuard.Tests.Unit/Scoring/ModelRegistryBootstrapTests.cs`
- `tests/TelemetryGuard.Tests.Integration/Sql/ModelRegistryRepositoryTests.cs`
- `tests/TelemetryGuard.Tests.Integration/Services/RetrainCycleTests.cs`

**Modify**

- `TelemetryGuard.Data/migrations/README.md` — one **appended** registry row for `0009` (P2-01 appends the `0008` row in parallel; never rewrite an existing row).
- `TelemetryGuard.Data/DataServiceCollectionExtensions.cs` — one `TryAddSingleton<IModelRegistryRepository, ModelRegistryRepository>()`.
- `TelemetryGuard.Api/Program.cs` — the bootstrap block + `ServingModelState` singleton + `ModelRegistryOptions` + `AddHostedService<ModelRegistryWatcher>()` (between `AddTelemetryGuardData()` and `AddScoringPipeline(...)`), and the log flush after `builder.Build()`. **No middleware-order change, no endpoint change.**
- `TelemetryGuard.Api/appsettings.json` — two keys inside the EXISTING `"Scoring"` section (`ModelSource`, `Registry`).
- `TelemetryGuard.Training/appsettings.json` — `Training:ArtifactRoot`, `Training:Retrain`, `Training:Promotion`, `Training:Bands`.
- `TelemetryGuard.Training/Program.cs` — new commands + usage text + exit codes 3/4.
- `TelemetryGuard.Training/Trainer.cs` — extract `LoadTrainingSetAsync` (+ `TrainingSet`) and the feature-set-version histogram query; `RunFromDatabaseAsync` keeps its exact behavior.
- `tests/TelemetryGuard.Tests.Integration/TelemetryGuard.Tests.Integration.csproj` — add `<ProjectReference Include="..\..\TelemetryGuard.Training\TelemetryGuard.Training.csproj" />` (the Unit project already has it).

**Explicitly NOT touched**

- `TelemetryGuard.sln` — no new projects. (P2-03 adds `TelemetryGuard.Portal` and P2-05 adds `TelemetryGuard.Analytics.Kusto` to the same file; leave both alone.)
- `TelemetryGuard.Training/TelemetryGuard.Training.csproj` — **no `InternalsVisibleTo`** (see step 12) and no new `ProjectReference`: the project must still reference only `RiskEngine.Contracts` + `Analytics.Abstractions`. New packages are also unnecessary — everything the loop needs (`Dapper`, `Microsoft.Data.SqlClient`, `Microsoft.ML`, `ClickHouse.Client`, `System.Text.Json`) is already referenced.
- Any sibling P2 task's new project — `TelemetryGuard.Portal/**` (P2-03), `TelemetryGuard.Analytics.Kusto/**` (P2-05) — and the P2-01 files `TelemetryGuard.Api/Workers/RollupService.cs`, `RollupOptions.cs`, `Endpoints/AdminEndpoints.cs`, `Endpoints/AdminModels.cs`.
- `.github/workflows/ci.yml` — no change. New unit tests are picked up by the existing `tests/TelemetryGuard.Tests.Unit` run and the new integration tests by the existing Testcontainers job. **CI must never run `retrain`** against a real database.
- `.gitignore` — no change; `artifacts/` is already ignored, which covers `artifacts/models/**` and `artifacts/divergence/**`. Models and training data are never committed.
- `TelemetryGuard.Analytics.ClickHouse/schema/*` — no new script; `0002_training_and_shadow.sql` already added `features`, `shadow_score`, `shadow_scorer_version`, `weight`.
- `TelemetryGuard.RiskEngine/**` — `ScoringOptions`, `MlNetScorer`, `ModelMetadataReader`, `IShadowScorer`, `ScoringPipeline` and `AddScoringPipeline` are consumed as-is.
- `doc/spec.md`, `doc/plan.md`, `CLAUDE.md`.

## Acceptance criteria

- `dotnet build TelemetryGuard.sln -c Release` succeeds with 0 warnings (`TreatWarningsAsErrors=true`); `TelemetryGuard.Training` still references only `RiskEngine.Contracts` + `Analytics.Abstractions`, and `TelemetryGuard.RiskEngine.Contracts` still has zero package references.
- Migration `0009` applies cleanly on a fresh database (whether or not P2-01's `0008` is present) and is journaled in `dbo.SchemaVersions`. `dbo.ModelRegistry` has **no `TenantId` column** and is **absent from `rls.TenantIsolationPolicy`** — asserted by a test that queries `sys.security_predicates` joined to `sys.tables`, so the deviation is intentional and provable rather than an oversight.
- Inserting two `active` rows (or two `shadow` rows) fails on `UX_ModelRegistry_SingleActive` / `UX_ModelRegistry_SingleShadow`; inserting a non-`rejected` row without `ScorerVersion`/`ArtifactPath`/`ArtifactSha256` fails on `CK_MR_Artifact`.
- **A rejected candidate never serves**: a run whose AUC is below `Training:MinAuc` writes exactly one `rejected` row with `ScorerVersion IS NULL`, exports no artifact, returns exit code 2; `promote --model … --status shadow|active` on any `rejected` row changes nothing and returns exit code 4.
- **Retrain registers, never promotes**: after a successful `retrain`, the registry holds exactly one new `candidate` row and the counts of `active`/`shadow` rows are unchanged. `grep -n "ModelStatuses.Active" TelemetryGuard.Training/RetrainRunner.cs` returns nothing.
- **Promotion is transactional**: `promote --status shadow` on a second candidate retires the previous shadow row in the same transaction (never two shadow rows, never zero during the switch); `promote --status active` sets `ActiveSinceUtc`; `rollback` retires the current active row and restores the previously active one, and `rollback --to none` leaves no active row.
- **The enforce gate binds**: `promote --status active` for a model that has been `shadow` for less than `MinShadowHours`, or with fewer than `MinShadowSessions` shadow-scored sessions, is refused with exit code 4 and prints every unmet reason; `--force` promotes and records `Notes` starting with `FORCED:`.
- **Startup resolution**: with `Scoring:ModelSource=Registry` and an `active` row whose artifact verifies, the host resolves `IScorer` to `MlNetScorer` and stamps its `scorer_version` on verdicts; with a `shadow` row it resolves `IScorer` to `HeuristicScorer` **and** registers an `IShadowScorer`; with an empty registry it loads no model at all even when `Scoring:ModelPath` is set in appsettings.
- **Fail-safe load**: a tampered `model.zip` (hash mismatch), a missing directory, a `metadata.json` whose `scorer_version` differs from the registry row, or a `feature_set_version` different from `FraudFeatureVector.FeatureSetVersion` each leave the heuristic enforcing and log the exact reason — the process must never start with an unverified model, and must never crash because of one.
- **Lineage**: a promotion never changes the `scorer_version` stamped by a *running* process; the watcher logs `promotion pending … restart the host to apply` and increments `tg.model.promotion_pending` instead. `scorer_version` values are never reused (guaranteed by `UX_ModelRegistry_ScorerVersion` plus the content hash in the version string).
- **Feature-set boundary**: `retrain` over a window containing any `tg_events` row whose `feature_set_version` differs from the running contract exits 3, registers nothing, and prints the version histogram.
- **Divergence report**: on seeded shadow/heuristic verdicts it reports the band-agreement matrix, mean absolute delta, would-block-more/fewer counts, and rank-AUC for both scorers; rule-floored sessions are excluded from the comparison and reported separately; a labeled subset with a single class yields `NaN` AUC, never 0.
- `dotnet run --project TelemetryGuard.Training -- --help` lists every command with the exit-code table; an unknown command still exits non-zero.
- The full fixture walkthrough in `doc/runbooks/model-retraining.md` works end to end against the compose stack with only SDK-06-generated traffic (no production data), including rollback.

## Testing

**Unit (no containers — `tests/TelemetryGuard.Tests.Unit`)**

- `PromotionGateTests`: candidate equal to the incumbent passes at `MaxAucRegression = 0`; 0.01 worse fails with a reason naming AUC; AUPRC regression inside tolerance passes; `ScoreP99Ms` above `MaxScoreP99Ms` fails (D3); too few positives/negatives fails; no incumbent → only the label-count and latency checks apply. Enforce gate: shadow age below the threshold fails; enough shadow hours but too few sessions fails; enough of both but live shadow AUC below the enforced AUC + advantage fails; the generation-2 branch (`hasActiveIncumbent = true`) passes on the offline comparison alone; every failure lists all unmet reasons, not just the first.
- `DivergenceReportTests`: a hand-built 10-row set gives the exact 3×3 band matrix, agreement rate, mean absolute delta and would-block-more/fewer counts; rule-floored rows are excluded from the comparison and counted; `RankAuc` returns 1.0 for perfect separation, 0.5 for a constant score, the tie-averaged value for a mixed case, and `NaN` when one class is missing.
- `ModelRegistryBootstrapTests`: `ToScoringOverrides` maps `active`/`shadow`/null exactly as in step 4 (including clearing `Scoring:ModelPath` when nothing is promoted); `VerifyArtifact` accepts a temp directory containing a real `model.zip` + `metadata.json` and rejects, with distinct messages, a missing file, a mutated file (hash mismatch), a mismatched `scorer_version`, and a mismatched `feature_set_version`. Build the fixture model by calling the shipped `Trainer.TrainAndExport` on ~200 synthetic `MlFeatureRow`s (seconds, in-memory) — do not commit a model file.
- A composition test (`ServiceCollection` + `ConfigurationBuilder`, mirroring the existing RSK-08 listen-only tests): the three override sets produce, respectively, `MlNetScorer` as `IScorer`; `HeuristicScorer` as `IScorer` plus a registered `IShadowScorer`; and no `PredictionEnginePool` registration at all.

**Integration (`tests/TelemetryGuard.Tests.Integration`, Testcontainers)**

- `ModelRegistryRepositoryTests` (`[Collection("sqlserver")]`, DAT-08 fixture, real migrations): insert candidate → promote shadow → promote active → rollback, asserting `GetServingModelAsync` at each step (`active` wins over `shadow`); the two unique indexes reject duplicates; a `rejected` row cannot be promoted; `CK_MR_Artifact` rejects a servable row without an artifact; the RLS-absence assertion above. **Round-trip check**: rows written by `TelemetryGuard.Training.ModelRegistryClient` are read back correctly by `TelemetryGuard.Data.ModelRegistryRepository` (this is what keeps the two hand-written column lists from drifting).
- `RetrainCycleTests` — the mini end-to-end, sized for CI: one `SqlServerFixture` (SQL) plus the `ClickHouseSchemaFixture` (ClickHouse). Seed ~300 `tg_events` verdict rows via `TestEvents.SeedAsync` with `Features` populated from separable synthetic `FraudFeatureVector`s (serialized with `FraudFeatureVectorJson.Options`) and matching `tg_labels` rows, then, with fixture-sized thresholds:
  1. `retrain` → one `candidate` row, artifact on disk under a temp `--out`, `MetadataJson`/`GateJson` populated, exit 0;
  2. re-run inside `MinIntervalHours` → exit 0, no new row; with `--force` → a new row;
  3. label-shuffled seed → exit 2 and a `rejected` row with `ScorerVersion IS NULL`;
  4. a window containing one row with `feature_set_version = 99` → exit 3, no new row;
  5. `promote --status shadow`, then seed verdict rows carrying `shadow_score`/`shadow_scorer_version` → `divergence` produces a summary with the expected session counts and writes its JSON;
  6. `promote --status active` (thresholds relaxed) → `GetServingModelAsync` returns the model as `active`; `rollback` → no active row.
  Keep every dataset in the hundreds of rows: the loop must be provable without a large dataset.
- `ModelRegistryWatcher` test: with a loaded `ServingModelState` of version A and a registry serving version B, one `RunOnceAsync` logs the pending-promotion warning and leaves the process's `IScorer` untouched; a registry read failure is swallowed (the host keeps running).

**Never**: no test may train against a real database, commit a model artifact, or leave rows in a shared `dbo.ModelRegistry` — the integration tests own their container.

## Out of scope / guardrails

- **Promotion is never automatic (D18).** No code path may set `Status = 'active'` except the `promote --status active` CLI command run by a human. `retrain` may not promote — not even to `shadow`. A running process never changes its enforcing scorer; applying a promotion is a restart.
- **Listen-only means listen-only.** A `shadow` model influences nothing: not the band, not challenges, not the exclusion queue, not the summaries. It is logged to `tg_events.shadow_score`/`shadow_scorer_version` only.
- **Rules only raise (§6.3).** Retraining never touches T1 rule semantics, thresholds, or the `Math.Max` floor fold. The divergence report never "removes" a floor — it excludes floored sessions from the comparison and says so.
- **NaN is signal (§7).** No imputation anywhere in the loop; `HandleMissingValue = true` stays; a metric that cannot be computed is `NaN`, never 0.
- **One global model.** Per-tenant models are out of scope (data volume, and every per-tenant split multiplies the promotion surface). `dbo.ModelRegistry` therefore has no `TenantId` and no RLS predicate — and that must stay a documented, test-asserted decision, not an accident. Training reads stay per-tenant-filtered (D11): tenants are enumerated under the SYSTEM sentinel and every ClickHouse query carries `tenant_id = {t:UUID}`.
- **Never train across a `feature_set_version` boundary** (step 12.3), and never serve a model whose `metadata.json` disagrees with the running `FraudFeatureVector.FeatureSetVersion` (step 4). The shipped contract is `public const int FeatureSetVersion = 1` (`TelemetryGuard.RiskEngine.Contracts/FraudFeatureVector.cs`) and **this task does not change it** — nor does P2-01, whose own guardrails forbid the bump. Whenever some future task does bump it to 2, every model trained at 1 becomes unservable by construction — that is the intended behavior, and it is why the guard in step 4 compares against the running constant rather than a stored default.
- **Training compute stays offline (D3).** No training, hyper-parameter search, or dataset materialization inside the API host; the host's only registry cost is one small SQL row read at startup plus a poll every `PollMinutes`. No model-server sidecar, no HTTP inference hop.
- **No server-side Python/Node (D1).** The loop is ML.NET/C# plus, at most, a shell cron entry. No notebooks, no sklearn, no ONNX conversion (ONNX Runtime remains the later escape hatch for sequence models).
- **Dapper only (D9), numbered DbUp scripts only (D10)**; no EF Core; never edit an applied migration. SQL Server connections come from `ISystemConnectionFactory` (via `SystemConnections.FromConfiguration` at the composition root) — no new raw `SqlConnection` anywhere; `SqlHealthCheck` stays the only raw-connection site.
- **No new `IAnalyticsQueries` method and no generic cross-engine query layer (D7).** The divergence and training reads are hand-written ClickHouse SQL inside the offline console, exactly as RSK-08 established. The API host never queries ClickHouse for registry purposes.
- **Backlog items stay out**, explicitly: the IP-reputation store, per-tenant Turnstile keys, SSO/user accounts, multi-instance verdict claiming (the API-06 SETNX single-instance note stands), the Kusto provider (P2-05), publisher-aggregate features (P2-01 — its aggregate tables are **not** read by this task, and `publisher_fraud_ratio` is a future task's work, not this one's), the customer portal (P2-03), and Azure/blob artifact storage (P2-04 — artifacts stay on the filesystem; `Scoring:Registry:ArtifactRoot` is the seam a future volume/blob mount uses).
- **Deployment interaction with P2-04, stated so nobody "fixes" it.** P2-04's container image ships no model artifact and sets no `Scoring__*` environment variable, so a deployed replica boots with `Scoring:ModelSource=Registry` and an **empty** registry, which this task's bootstrap resolves to *pure heuristic, no model loaded* — the same behavior the image has today. The registry read is guarded by a 5-second timeout and is fail-safe, so an unreachable Azure SQL at startup also lands on the heuristic. Do **not** add a `Scoring__ModelSource` env var, a model volume mount, or a registry-seeding deploy step to P2-04; if artifacts ever need to live outside the container, `Scoring:Registry:ArtifactRoot` is the one seam to use, in its own task.
- **No broker, no queue (D12)**: the registry table plus a poll is the design.
- **Never commit trained models, training data, or divergence dumps.** `artifacts/` stays ignored; the registry stores paths and hashes, not blobs.
