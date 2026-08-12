---
id: DAT-08
title: SQL integration test harness and RLS proof
phase: 1
workstream: data
depends_on: [DAT-05, DAT-06, DAT-07]
size: M
spec_refs: [D9, D11, "§9"]
detail_level: full
---

# DAT-08: SQL integration test harness and RLS proof

## Objective
Build the Testcontainers-based xUnit harness in `tests/TelemetryGuard.Tests.Integration` that starts SQL Server (and Redis for the whitelist cache), applies all migrations programmatically via the DAT-01 runner, executes **every** Dapper repository method at least once, and proves the RLS tenant-isolation guarantees: cross-tenant reads return empty, mismatched writes are blocked, unstamped connections see nothing, and the SYSTEM sentinel sees everything. Per D9, this suite is the replacement for the compile-time checking EF would have provided — a repository method without a test here is untested SQL.

## Spec context (self-contained)
- **D9.4**: compile-time query checking was given up with EF; the replacement is "an integration-test pass: Testcontainers spins up SQL Server, DbUp applies migrations, every repository method executes once."
- **§9**: "Repository integration tests: ... include a deliberate cross-tenant read that must return empty" — empty, not an error: RLS FILTER silently filters; only write-side violations (BLOCK predicate) throw.
- **D11 RLS semantics under test**: predicate `rls.fn_tenantPredicate` passes when the row's `TenantId` equals `SESSION_CONTEXT(N'TenantId')` or when the session context equals the SYSTEM sentinel `00000000-0000-0000-0000-000000000001`. Unstamped session ⇒ NULL context ⇒ zero rows visible, writes blocked. Resolution tables `dbo.ApiKeys`/`dbo.Sites` are exempt and fully visible unstamped.
- Repositories under test get connections only from `ITenantConnectionFactory` (config key `ConnectionStrings:Main`) with ambient tenant from `ITenantContext`.

## Prerequisites
- **DAT-01**: `TelemetryGuard.MigrationRunner` with `public static int Migrations.RunSqlServer(string connectionString)`.
- **DAT-03** (via DAT-05/06/07): factories `TenantConnectionFactory` (public), `ISystemConnectionFactory`/`IResolutionConnectionFactory` (via `AddTelemetryGuardData()`), `WellKnownTenants.System`.
- **DAT-05**: `ITenantRepository` (GetCurrentAsync, UpdateRetentionDaysAsync, UpdateEnforcementModeAsync), `ISiteRepository` (CreateAsync, GetBySiteKeyAsync, ListAsync, UpdateAsync, DeleteAsync), `ICampaignRepository` (CreateAsync, GetAsync, GetRedirectAsync, ListAsync, UpdateAsync), records in `TelemetryGuard.Data.Models`.
- **DAT-06**: `IVerdictSummaryRepository` (UpsertDailySummaryAsync, UpsertFlaggedSourceAsync, GetDailySummariesAsync, GetTopFlaggedSourcesAsync), `IRollupWatermarkRepository` (GetAsync, SetAsync).
- **DAT-07**: `IWhitelistRepository` (AddAsync, RemoveAsync, ListAsync, AreWhitelistedAsync, RebuildCacheAsync), Redis key contract `t:{tenantId}:wl:{sourceType}`, optional `ILabelSink` injection.
- **FND-04**: `ITenantContext` / `TenantId` (adapt names from the actual code).

## Implementation steps

1. **Project setup** — `tests/TelemetryGuard.Tests.Integration/TelemetryGuard.Tests.Integration.csproj` (create if the FND-01 scaffold did not):
   packages `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`, `Testcontainers.MsSql` (≥ 3.10.0), `Testcontainers.Redis`, `Dapper`, `Microsoft.Data.SqlClient`, `StackExchange.Redis`, `Microsoft.Extensions.Configuration`; project references to `TelemetryGuard.Data`, `TelemetryGuard.MigrationRunner`, `TelemetryGuard.Core` (FND-04), and `TelemetryGuard.Analytics.Abstractions` (for the fake `ILabelSink` — ANA-01; if DAT-07 shipped without label emission because ANA-01 was absent, skip that reference and the two label tests, and record the gap in the PR).

2. **Fixture — `tests/TelemetryGuard.Tests.Integration/Sql/SqlServerFixture.cs`:**
   ```csharp
   using Dapper;
   using Microsoft.Data.SqlClient;
   using Testcontainers.MsSql;
   using Testcontainers.Redis;
   using TelemetryGuard.Data;
   using TelemetryGuard.MigrationRunner;

   namespace TelemetryGuard.Tests.Integration.Sql;

   [CollectionDefinition("sqlserver")]
   public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture> { }

   public sealed class SqlServerFixture : IAsyncLifetime
   {
       private readonly MsSqlContainer _sql = new MsSqlBuilder()
           .WithImage("mcr.microsoft.com/mssql/server:2022-latest").Build();
       private readonly RedisContainer _redis = new RedisBuilder()
           .WithImage("redis:7-alpine").Build();

       public static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
       public static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");
       public static readonly Guid CampaignA1 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
       public const string SiteKeyA = "site-a-key-0001";
       public const string ApiKeyA = "test-api-key-tenant-a";        // raw key; only its SHA-256 is stored

       public string ConnectionString { get; private set; } = "";
       public string RedisConnectionString { get; private set; } = "";

       public async Task InitializeAsync()
       {
           await Task.WhenAll(_sql.StartAsync(), _redis.StartAsync());
           ConnectionString = new SqlConnectionStringBuilder(_sql.GetConnectionString())
               { InitialCatalog = "TelemetryGuard" }.ToString();
           RedisConnectionString = _redis.GetConnectionString();

           if (Migrations.RunSqlServer(ConnectionString) != 0)
               throw new InvalidOperationException("Migrations failed — see console output.");
           await SeedAsync();
       }

       public async Task DisposeAsync()
       {
           await _sql.DisposeAsync();
           await _redis.DisposeAsync();
       }

       /// <summary>Open a connection stamped for the given tenant (or unstamped when null).</summary>
       public async Task<SqlConnection> OpenAsync(Guid? tenantId)
       {
           var conn = new SqlConnection(ConnectionString);
           await conn.OpenAsync();
           if (tenantId is { } tid)
               await conn.ExecuteAsync(
                   "EXEC sp_set_session_context @key = N'TenantId', @value = @tid, @read_only = 1",
                   new { tid });
           return conn;
       }

       private async Task SeedAsync()
       {
           await using var sys = await OpenAsync(WellKnownTenants.System);
           await sys.ExecuteAsync(
               "INSERT INTO dbo.Tenants (TenantId, Name) VALUES (@TenantA, N'Tenant A'), (@TenantB, N'Tenant B')",
               new { TenantA, TenantB });
           await sys.ExecuteAsync(
               "INSERT INTO dbo.Sites (TenantId, SiteKey, Domain) VALUES (@TenantA, @SiteKeyA, N'a.example.com')",
               new { TenantA, SiteKeyA });
           await sys.ExecuteAsync(
               "INSERT INTO dbo.ApiKeys (KeyHash, TenantId, Scopes) VALUES (@hash, @TenantA, N'admin')",
               new { hash = System.Security.Cryptography.SHA256.HashData(
                         System.Text.Encoding.UTF8.GetBytes(ApiKeyA)), TenantA });
           await sys.ExecuteAsync(
               """
               INSERT INTO dbo.Campaigns (TenantId, CampaignId, Platform, LandingUrl)
               VALUES (@TenantA, @CampaignA1, 'google', N'https://a.example.com/landing')
               """,
               new { TenantA, CampaignA1 });
       }
   }
   ```

3. **Repository construction helper — `tests/TelemetryGuard.Tests.Integration/Sql/RepositoryFactory.cs`.** Use FND-04's REAL `TenantContext` (its `Resolve` can be called once per instance) plus an `IConfiguration` pointing `ConnectionStrings:Main` at the container, then build the real factories/repositories via `AddTelemetryGuardData()` (DI route required — repository implementations are `internal`):
   ```csharp
   using TelemetryGuard.Core; // TenantContext, TenantId, ITenantContext

   public static ServiceProvider BuildServices(SqlServerFixture fx, Guid tenantId, ILabelSink? labelSink = null)
   {
       var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
       {
           ["ConnectionStrings:Main"] = fx.ConnectionString,
           ["ConnectionStrings:Redis"] = fx.RedisConnectionString,
       }).Build();

       var ctx = new TenantContext();
       ctx.Resolve(new TenantId(tenantId));

       var services = new ServiceCollection();
       services.AddSingleton<IConfiguration>(cfg);
       services.AddLogging();
       services.AddSingleton<ITenantContext>(ctx);
       if (labelSink is not null) services.AddSingleton(labelSink);
       services.AddTelemetryGuardData();
       return services.BuildServiceProvider();
   }
   ```
   Each test creates its own provider/scope; never share connections between tests.

4. **Repository coverage tests** — `[Collection("sqlserver")]` classes, one per repository. Every method below MUST have at least one test executing real SQL (names indicative):

   `TenantRepositoryTests`
   - `GetCurrentAsync_returns_seeded_tenant_defaults` (RetentionDays 90, EnforcementMode 0)
   - `UpdateRetentionDaysAsync_persists_within_range`
   - `UpdateRetentionDaysAsync_rejects_out_of_range` (29, 181 → `ArgumentOutOfRangeException`, no DB change)
   - `UpdateEnforcementModeAsync_persists_and_rejects_invalid`

   `SiteRepositoryTests`
   - `CreateAsync_then_GetBySiteKeyAsync_roundtrips`
   - `CreateAsync_rejects_ambient_tenant_mismatch`
   - `ListAsync_returns_only_current_tenant_sites`
   - `UpdateAsync_changes_domain_and_mode` / `UpdateAsync_rejects_bad_mode`
   - `DeleteAsync_removes_and_returns_true_then_false`

   `CampaignRepositoryTests`
   - `CreateAsync_then_GetAsync_roundtrips` (incl. GeoTargets JSON)
   - `CreateAsync_rejects_relative_or_empty_LandingUrl`
   - `GetRedirectAsync_returns_landing_url_and_status`
   - `GetRedirectAsync_unknown_campaign_returns_null`
   - `ListAsync_and_UpdateAsync_roundtrip`

   `VerdictSummaryRepositoryTests`
   - `UpsertDailySummaryAsync_inserts_then_updates_idempotently` (call twice with identical row → one row, absolute values, no doubling)
   - `UpsertDailySummaryAsync_rejects_foreign_tenant_row` (`InvalidOperationException`)
   - `UpsertFlaggedSourceAsync_upserts_idempotently`
   - `GetDailySummariesAsync_filters_campaign_and_range_ordered_by_date`
   - `GetTopFlaggedSourcesAsync_orders_by_blocked_then_flagged_and_limits`

   `RollupWatermarkRepositoryTests`
   - `GetAsync_returns_null_before_first_set`
   - `SetAsync_then_GetAsync_roundtrips_and_overwrites`

   `WhitelistRepositoryTests`
   - `AddAsync_inserts_and_populates_redis_set` (assert `SISMEMBER` = true and key TTL ∈ (0, 1 h])
   - `AddAsync_is_idempotent_on_natural_key` (same Id returned twice)
   - `RemoveAsync_deletes_and_removes_from_redis`
   - `ListAsync_filters_by_sourceType`
   - `AreWhitelistedAsync_batch_reports_membership_and_expired_as_false`
   - `RebuildCacheAsync_omits_expired_entries`
   - `AddAsync_review_screen_emits_negative_label` (fake `ILabelSink` implementing `ValueTask WriteAsync(LabelEvent, CancellationToken)` records exactly one `LabelEvent` with `Label == LabelValues.Legit`, `LabelSource == LabelSources.ReviewScreen`, and the `SessionId` passed in `NewWhitelistEntry`)
   - `AddAsync_manual_emits_no_label` (and `review_screen` WITHOUT a SessionId emits no label but still succeeds)
   - `AddAsync_survives_redis_outage` (point `ConnectionStrings:Redis` at a closed port with `abortConnect=false`, assert SQL row exists and no throw)

5. **RLS proof tests — `tests/TelemetryGuard.Tests.Integration/Sql/RlsProofTests.cs`** (the §9 "deliberate cross-tenant read" and friends). Use raw connections from `fixture.OpenAsync(...)` so no repository code can mask the database behavior:
   ```csharp
   [Collection("sqlserver")]
   public sealed class RlsProofTests(SqlServerFixture fx)
   {
       [Fact] // (a) cross-tenant read is EMPTY, not an error
       public async Task Read_as_B_of_A_rows_returns_empty()
       {
           await using var asB = await fx.OpenAsync(SqlServerFixture.TenantB);
           var rows = await asB.QueryAsync(
               "SELECT * FROM dbo.Campaigns WHERE TenantId = @tid",
               new { tid = SqlServerFixture.TenantA });
           Assert.Empty(rows); // seeded campaign exists for A; B sees nothing, no exception
       }

       [Fact] // (b) BLOCK predicate: mismatched INSERT under a stamped context throws
       public async Task Insert_with_mismatched_TenantId_throws()
       {
           await using var asA = await fx.OpenAsync(SqlServerFixture.TenantA);
           await Assert.ThrowsAsync<SqlException>(() => asA.ExecuteAsync(
               """
               INSERT INTO dbo.Campaigns (TenantId, CampaignId, Platform, LandingUrl)
               VALUES (@tid, NEWID(), 'google', N'https://b.example.com/x')
               """,
               new { tid = SqlServerFixture.TenantB }));
       }

       [Fact] // (c) unstamped connection sees zero rows in every RLS table
       public async Task Unstamped_connection_sees_zero_rows()
       {
           await using var raw = await fx.OpenAsync(null);
           foreach (var table in new[] { "dbo.Tenants", "dbo.Campaigns",
               "dbo.VerdictDailySummaries", "dbo.FlaggedSourcesDaily",
               "dbo.ExclusionQueue", "dbo.RollupWatermarks", "dbo.WhitelistEntries" })
           {
               var n = await raw.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {table}");
               Assert.Equal(0, n);
           }
       }

       [Fact] // (d) SYSTEM sentinel sees all tenants
       public async Task System_sentinel_sees_all_rows()
       {
           await using var sys = await fx.OpenAsync(WellKnownTenants.System);
           var n = await sys.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Tenants");
           Assert.True(n >= 2); // both seeded tenants visible
       }

       [Fact] // (e) resolution tables are exempt: readable unstamped
       public async Task Resolution_tables_readable_unstamped()
       {
           await using var raw = await fx.OpenAsync(null);
           Assert.True(await raw.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Sites") >= 1);
           Assert.True(await raw.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.ApiKeys") >= 1);
       }
   }
   ```
   Add a companion repository-level test: build services for tenant B, call `ICampaignRepository.GetAsync(SqlServerFixture.CampaignA1)` → null, and `GetRedirectAsync` → null (isolation holds through the full factory + repository path, empty not error).

6. **Resolver integration test** (DAT-04's SQL path): via `BuildServices`, resolve `ITenantResolver`; `ResolveApiKeyAsync(SqlServerFixture.ApiKeyA)` → TenantA with scope `admin`; `ResolveSiteKeyAsync(SqlServerFixture.SiteKeyA)` → TenantA; unknown key → null (twice, second hit served from cache — assert with a stopwatch-free approach: just assert null both times).

7. **CI**: mark all classes `[Trait("Category", "Integration")]`. Ensure FND-03's integration job runs `dotnet test tests/TelemetryGuard.Tests.Integration --filter Category=Integration` (adapt to the filter convention FND-03 established; Docker must be available on the runner).

## Files to create or modify
- `tests/TelemetryGuard.Tests.Integration/TelemetryGuard.Tests.Integration.csproj` (new or modify)
- `tests/TelemetryGuard.Tests.Integration/Sql/SqlServerFixture.cs` (new)
- `tests/TelemetryGuard.Tests.Integration/Sql/RepositoryFactory.cs` (new)
- `tests/TelemetryGuard.Tests.Integration/Sql/TenantRepositoryTests.cs` (new)
- `tests/TelemetryGuard.Tests.Integration/Sql/SiteRepositoryTests.cs` (new)
- `tests/TelemetryGuard.Tests.Integration/Sql/CampaignRepositoryTests.cs` (new)
- `tests/TelemetryGuard.Tests.Integration/Sql/VerdictSummaryRepositoryTests.cs` (new)
- `tests/TelemetryGuard.Tests.Integration/Sql/RollupWatermarkRepositoryTests.cs` (new)
- `tests/TelemetryGuard.Tests.Integration/Sql/WhitelistRepositoryTests.cs` (new)
- `tests/TelemetryGuard.Tests.Integration/Sql/RlsProofTests.cs` (new)
- `tests/TelemetryGuard.Tests.Integration/Sql/TenantResolverTests.cs` (new)
- `.github/workflows/ci.yml` (modify only if the integration job does not already run this project)

## Acceptance criteria
- `dotnet test tests/TelemetryGuard.Tests.Integration` passes locally with Docker running; total wall time under ~5 minutes (one shared SQL container per collection, not per test).
- Every public method of `ITenantRepository`, `ISiteRepository`, `ICampaignRepository`, `IVerdictSummaryRepository`, `IRollupWatermarkRepository`, `IWhitelistRepository`, and `ITenantResolver` is executed by at least one passing test (cross-check the list in step 4/6 against the interfaces; if a method exists that is not listed, add a test — the invariant is *every method*, the list is the enumeration at time of writing).
- RLS proofs (a)–(e) pass exactly as specified: (a) empty result, no exception; (b) `SqlException`; (c) zero rows across all seven RLS tables; (d) sentinel sees ≥ 2 tenants; (e) resolution tables readable unstamped.
- The idempotency tests prove absolute-value MERGE semantics (no count doubling after a repeated upsert).
- Suite is deterministic: unique-per-test natural keys (fresh GUIDs/values) so tests can run in any order and re-run without container restart.

## Testing
This task IS the test deliverable. Meta-verification: run the suite twice back-to-back (journal/no-op migration path and data-idempotency both get exercised), and run with `--filter Category=Integration` to confirm the trait filter works.

## Out of scope / guardrails
- Do NOT test ClickHouse or the analytics providers here — that is ANA-06's contract suite; this harness never opens a ClickHouse connection (D23 split, D7 no cross-engine layer).
- Do NOT weaken RLS assertions to "helpful" variants: (a) must assert **empty, not error** — asserting a throw would mask a broken FILTER predicate; (b) must assert a **throw** — a silent no-op would mask a broken BLOCK predicate.
- Do NOT bypass the migration runner with hand-created schema — the point is proving the real migrations produce a working, isolated schema (D9/D10).
- No EF Core, no test-only ORM helpers (D9). No mocking of `SqlConnection` — these are integration tests against the real engine.
- Do not seed or write any row with the SYSTEM sentinel as its `TenantId` — the sentinel is session-context-only (DAT-03).
- Keep this suite green as a merge gate: per D9 it replaces compile-time query checking; skipping it in CI reintroduces the risk EF's removal created.
