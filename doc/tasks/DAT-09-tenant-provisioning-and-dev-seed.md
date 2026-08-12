---
id: DAT-09
title: Tenant provisioning and dev seed
phase: 1
workstream: data
depends_on: [DAT-03, DAT-05]
size: M
spec_refs: [D8, D11, D16, D20, D21, D22, "§11"]
detail_level: full
---

# DAT-09: Tenant provisioning and dev seed

## Objective
Give the system a way to create tenants, API keys, sites, and campaigns — without it, nothing end-to-end can run: the DAT-03 RLS BLOCK predicates make `dbo.Tenants` unwritable from any unstamped or foreign-tenant connection, so tenant creation is only possible through the system-side connection factory. Deliverables: (a) a `provision` CLI verb on the existing `TelemetryGuard.MigrationRunner` console (DAT-01) with subcommands `create-tenant`, `issue-api-key`, `register-site`, `create-campaign`, all writing via `ISystemConnectionFactory` with explicit `TenantId` values; (b) `scripts/dev-seed.sh`, which applies migrations and then provisions one deterministic, well-known dev tenant + API key + site + campaign against the FND-02 compose stack so SDK fixtures and manual testing work immediately; (c) the pinned key-format decision (`tg_ak_` / `tg_sk_` prefixes, base62); (d) an integration test in the DAT-08 harness proving a provisioned tenant is visible under its own stamped session context and invisible under another tenant's.

## Spec context (self-contained)
- **D11 — RLS is the PRIMARY tenant isolation (DAT-03's migration `0002`)**: every tenant-scoped table has FILTER + BLOCK predicates keyed on `SESSION_CONTEXT(N'TenantId')`. An unstamped connection sees zero rows and cannot INSERT; a session stamped as tenant A cannot INSERT a row with `TenantId = B`. Therefore **tenant provisioning must go through DAT-03's `ISystemConnectionFactory`** — the only component allowed to stamp arbitrary tenant ids. Console code has no ambient request `ITenantContext`, so `TenantConnectionFactory` is unusable here by design.
- **Stamping does not require the tenant to exist.** `SESSION_CONTEXT` is a session value, not a foreign key: `OpenForTenantAsync(newTenantId)` followed by `INSERT INTO dbo.Tenants (TenantId, ...) VALUES (@TenantId, ...)` passes the BLOCK predicate because row value = session value. This is the create-tenant mechanism.
- **SYSTEM sentinel** (`00000000-0000-0000-0000-000000000001`, `WellKnownTenants.System`): never a real tenant (DAT-02's `CK_Tenants_NotSystemSentinel` CHECK rejects it), never written into a row, obtainable only via `OpenSystemAsync`. Provisioning verbs use **per-tenant stamping only** (`OpenForTenantAsync`) so every write also proves the BLOCK predicate passes.
- **D8/D23 — SQL Server owns config data**: tenants, API keys, sites, campaigns. No ClickHouse or Redis writes belong in provisioning.
- **D20 — retention**: per-tenant `RetentionDays`, 30–180, default 90 (DB CHECK `CK_Tenants_RetentionDays`).
- **D21 — enforcement mode**: `tinyint` — `0 = AutoEnforce` (default), `1 = ApprovalQueue`.
- **D22 — integration modes**: each site is `'js'` (SDK snippet with `data-site-key`) or `'pixel'` (`<img src=".../p.gif?k=…">`). Site keys are public identifiers (they appear in page source); possession must never grant read access — they only attribute ingested traffic (DAT-04).
- **API keys are never stored raw** (DAT-02): `dbo.ApiKeys.KeyHash binary(32)` = SHA-256 of the raw key. The raw key is printed exactly once at issuance and is unrecoverable afterwards. DAT-04's resolver hashes the presented `X-Api-Key` value verbatim, so the stored hash MUST be `SHA256(UTF8(full key string INCLUDING the tg_ak_ prefix))`.
- **Key-format decision (pinned by this task — spec §11 leaves naming defaults to be pinned during the build)**:
  - API key: `tg_ak_` + 43 base62 chars encoding 32 random bytes (49 chars total). Shape regex: `^tg_ak_[0-9A-Za-z]{43}$`.
  - Site key: `tg_sk_` + 22 base62 chars encoding 16 random bytes (28 chars total). Shape regex: `^tg_sk_[0-9A-Za-z]{22}$`.
  - Base62 alphabet `0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz`; fixed-length output left-padded with `0`.
  - Both shapes fit `dbo.Sites.SiteKey varchar(64)` and satisfy DAT-04's `SqlTenantResolver` site-key shape regex `^[A-Za-z0-9_-]{4,64}$` (underscores are in its class). Do not change the prefixes — docs, SDK-06's generator invocations, and the dev seed reference them.

## Prerequisites
- **DAT-03** (`TelemetryGuard.Data`): `WellKnownTenants.System`, `AddTelemetryGuardData()` (registers the factories; config key `ConnectionStrings:Main`), and:
  ```csharp
  public interface ISystemConnectionFactory
  {
      Task<SqlConnection> OpenSystemAsync(CancellationToken ct);                    // sentinel — do not use here
      Task<SqlConnection> OpenForTenantAsync(Guid tenantId, CancellationToken ct);  // rejects Guid.Empty and the sentinel
  }
  ```
- **DAT-05**: its guardrail explicitly defers tenant provisioning to "an admin/system concern outside this task" — this is that task. Mirror DAT-05's validation rules exactly (retention 30–180, enforcement mode 0/1, platform ∈ google|meta|tiktok|other, `LandingUrl` absolute `http`/`https` via `Uri.TryCreate`, integration mode js|pixel) so CLI-provisioned rows are indistinguishable from repository-written ones.
- **Via DAT-03's chain — DAT-01**: console `TelemetryGuard.MigrationRunner` exists with `Program.cs` dispatching on `--clickhouse` else running `Migrations.RunSqlServer` from env `MIGRATIONS_CONNECTIONSTRING`; exit codes 0 = ok, 1 = failure, 2 = usage/env error. **DAT-02**: tables `dbo.Tenants(TenantId, Name, Status, RetentionDays, EnforcementMode, CreatedUtc)`, `dbo.ApiKeys(KeyHash binary(32) PK, TenantId, Scopes nvarchar(400) space-separated, Status, CreatedUtc)` (RLS-exempt), `dbo.Sites(TenantId, SiteKey varchar(64) UNIQUE, Domain, IntegrationMode, CreatedUtc)` (RLS-exempt), `dbo.Campaigns(TenantId, CampaignId, Platform, ExternalCampaignId, LandingUrl, GeoTargets, Status, CreatedUtc)`.
- **FND-02** (not a depends_on; needed only to *run* the seed script): compose stack with SQL Server on `localhost,1433` (`sa` / `.env` `MSSQL_SA_PASSWORD`), ClickHouse on 8123, `.env` created by `scripts/dev-up.sh`.
- Consumers of the seeded values (do not implement, just stay compatible): DAT-04 resolver (`X-Api-Key` hash lookup, `?k=` site key), SDK-08 bundle route `/sdk/tg.js`, SDK-02 snippet (`data-site-key`), API-03 pixel `/p.gif?k=…`, API-02 tracker `/c?k=…&cid=…`, SDK-06 bot generator (`--target http://localhost:8080 --site-key <key>`).

## Implementation steps

1. **Packages** on `TelemetryGuard.MigrationRunner/TelemetryGuard.MigrationRunner.csproj` (Dapper and `Microsoft.Data.SqlClient` flow transitively from the `TelemetryGuard.Data` project reference — add them explicitly only if the build complains):
   ```bash
   dotnet add TelemetryGuard.MigrationRunner package Microsoft.Extensions.Configuration
   dotnet add TelemetryGuard.MigrationRunner package Microsoft.Extensions.DependencyInjection
   ```

2. **Create `TelemetryGuard.MigrationRunner/Provisioning/KeyGenerator.cs`:**
   ```csharp
   using System.Numerics;
   using System.Security.Cryptography;
   using System.Text;

   namespace TelemetryGuard.MigrationRunner.Provisioning;

   /// <summary>
   /// Key formats pinned by DAT-09:
   ///   API key : "tg_ak_" + 43 base62 chars over 32 random bytes (49 chars total)
   ///   Site key: "tg_sk_" + 22 base62 chars over 16 random bytes (28 chars total)
   /// Raw API keys are printed ONCE and never stored: dbo.ApiKeys.KeyHash is
   /// SHA-256 over the UTF-8 bytes of the FULL raw key string, prefix included
   /// (matches DAT-04's resolver and DAT-08's seed convention).
   /// </summary>
   public static class KeyGenerator
   {
       public const string ApiKeyPrefix = "tg_ak_";
       public const string SiteKeyPrefix = "tg_sk_";
       private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

       public static string NewApiKey() => ApiKeyPrefix + ToBase62(RandomNumberGenerator.GetBytes(32), 43);
       public static string NewSiteKey() => SiteKeyPrefix + ToBase62(RandomNumberGenerator.GetBytes(16), 22);

       public static byte[] Sha256(string rawKey) => SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));

       internal static string ToBase62(byte[] bytes, int outputLength)
       {
           var value = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
           var chars = new char[outputLength];
           for (var i = outputLength - 1; i >= 0; i--)
           {
               value = BigInteger.DivRem(value, 62, out var rem);
               chars[i] = Alphabet[(int)rem];
           }
           return new string(chars); // value is 0 here: 62^43 > 2^256 and 62^22 > 2^128
       }
   }
   ```

3. **Create `TelemetryGuard.MigrationRunner/Provisioning/ProvisionCommand.cs`.** Public static class; `RunAsync` is the CLI dispatcher, the four core methods are public so tests call them programmatically (same pattern as DAT-01's `Migrations.RunSqlServer`). Obtain the factory via DI so the real DAT-03 registrations are exercised:
   ```csharp
   using Dapper;
   using Microsoft.Extensions.Configuration;
   using Microsoft.Extensions.DependencyInjection;
   using TelemetryGuard.Data;

   namespace TelemetryGuard.MigrationRunner.Provisioning;

   public static class ProvisionCommand
   {
       private static ServiceProvider BuildProvider(string connectionString)
       {
           var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
           {
               ["ConnectionStrings:Main"] = connectionString
           }).Build();
           var services = new ServiceCollection();
           services.AddSingleton<IConfiguration>(cfg);
           services.AddTelemetryGuardData();   // DAT-03: registers ISystemConnectionFactory
           return services.BuildServiceProvider();
       }

       /// <summary>CLI entry: args exclude the leading "provision". Returns process exit code
       /// (0 ok/skip, 1 database failure, 2 usage/validation error).</summary>
       public static async Task<int> RunAsync(string[] args, string connectionString) { /* dispatch, see step 4 */ }

       public static async Task<Guid> CreateTenantAsync(string connectionString, Guid? tenantId,
           string name, int retentionDays, byte enforcementMode, CancellationToken ct = default)
       {
           if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
               throw new ArgumentException("name is required, max 200 chars.", nameof(name));
           if (retentionDays is < 30 or > 180)
               throw new ArgumentOutOfRangeException(nameof(retentionDays), "D20: 30..180.");
           if (enforcementMode > 1)
               throw new ArgumentOutOfRangeException(nameof(enforcementMode), "D21: 0=AutoEnforce, 1=ApprovalQueue.");
           var tid = tenantId ?? Guid.NewGuid();
           if (tid == Guid.Empty || tid == WellKnownTenants.System)
               throw new ArgumentOutOfRangeException(nameof(tenantId),
                   "TenantId must be a real id — never Guid.Empty or the SYSTEM sentinel.");

           await using var provider = BuildProvider(connectionString);
           var factory = provider.GetRequiredService<ISystemConnectionFactory>();
           await using var conn = await factory.OpenForTenantAsync(tid, ct);

           // Idempotent re-seed path: only reachable when an explicit --tenant-id was given.
           var exists = await conn.ExecuteScalarAsync<int>(
               "SELECT COUNT(*) FROM dbo.Tenants WHERE TenantId = @TenantId", new { TenantId = tid });
           if (tenantId is not null && exists > 0)
           {
               Console.WriteLine($"Tenant {tid} already exists; skipping.");
               return tid;
           }

           await conn.ExecuteAsync(
               """
               INSERT INTO dbo.Tenants (TenantId, Name, RetentionDays, EnforcementMode)
               VALUES (@TenantId, @Name, @RetentionDays, @EnforcementMode)
               """,
               new { TenantId = tid, Name = name, RetentionDays = retentionDays, EnforcementMode = enforcementMode });
           Console.WriteLine($"TenantId: {tid}");
           return tid;
       }

       public static async Task<string> IssueApiKeyAsync(string connectionString, Guid tenantId,
           string scopes, string? rawKey = null, CancellationToken ct = default) { ... }

       public static async Task<string> RegisterSiteAsync(string connectionString, Guid tenantId,
           string domain, string integrationMode, string? siteKey = null, CancellationToken ct = default) { ... }

       public static async Task<Guid> CreateCampaignAsync(string connectionString, Guid tenantId,
           string platform, string? externalId, string landingUrl, string? geoTargetsJson,
           Guid? campaignId = null, CancellationToken ct = default) { ... }
   }
   ```
   The three elided methods follow the `CreateTenantAsync` pattern exactly — validate, `OpenForTenantAsync(tenantId)`, existence check, INSERT with explicit `TenantId`:
   - **`IssueApiKeyAsync`**: validate scopes (default when caller passes empty: `"admin ingest report"`; max 400 chars); if `rawKey` supplied it must match `^tg_ak_[0-9A-Za-z]{43}$` else generate via `KeyGenerator.NewApiKey()`. Verify the tenant exists first — `SELECT COUNT(*) FROM dbo.Tenants WHERE TenantId = @TenantId` on the stamped connection; 0 → throw `InvalidOperationException("Tenant not found (or not visible under its own context).")`. Existence check for explicit keys: `SELECT COUNT(*) FROM dbo.ApiKeys WHERE KeyHash = @KeyHash` → skip when present. Insert:
     ```sql
     INSERT INTO dbo.ApiKeys (KeyHash, TenantId, Scopes) VALUES (@KeyHash, @TenantId, @Scopes);
     ```
     with `KeyHash = KeyGenerator.Sha256(rawKey)`. Then print — the only place the raw key ever appears:
     ```
     ApiKey: tg_ak_xxxxxxxx...   (printed ONCE — store it now; only its SHA-256 hash is kept)
     ```
   - **`RegisterSiteAsync`**: validate domain (non-empty, ≤253 chars) and `integrationMode is "js" or "pixel"`; `siteKey` supplied → must match `^tg_sk_[0-9A-Za-z]{22}$`, else `KeyGenerator.NewSiteKey()`. Tenant-existence check as above; skip when the `(TenantId, SiteKey)` row exists. Insert:
     ```sql
     INSERT INTO dbo.Sites (TenantId, SiteKey, Domain, IntegrationMode)
     VALUES (@TenantId, @SiteKey, @Domain, @IntegrationMode);
     ```
     Print `SiteKey: tg_sk_...` (site keys are public — no secrecy warning needed).
   - **`CreateCampaignAsync`**: validate platform ∈ `google|meta|tiktok|other`; `landingUrl` must be absolute `http`/`https` (`Uri.TryCreate(landingUrl, UriKind.Absolute, out var u)` and `u.Scheme is "http" or "https"`); `geoTargetsJson` null or parseable by `System.Text.Json.JsonDocument.Parse` as an array; `campaignId ?? Guid.NewGuid()`. Skip when the explicit `(TenantId, CampaignId)` row exists. Insert:
     ```sql
     INSERT INTO dbo.Campaigns (TenantId, CampaignId, Platform, ExternalCampaignId, LandingUrl, GeoTargets, Status)
     VALUES (@TenantId, @CampaignId, @Platform, @ExternalCampaignId, @LandingUrl, @GeoTargets, 0);
     ```
     Print `CampaignId: <guid>`.

4. **`RunAsync` dispatch and flag parsing** (hand-rolled — no CLI package; loop over `args`, a token starting `--` is a flag whose value is the next token). Subcommands and flags:
   ```
   provision create-tenant   --name <string> [--retention-days 90] [--enforcement-mode AutoEnforce|ApprovalQueue] [--tenant-id <guid>]
   provision issue-api-key   --tenant-id <guid> [--scopes "admin ingest report"] [--key <tg_ak_...>]
   provision register-site   --tenant-id <guid> --domain <host> [--integration-mode js|pixel] [--site-key <tg_sk_...>]
   provision create-campaign --tenant-id <guid> --platform google|meta|tiktok|other --landing-url <url>
                             [--external-id <string>] [--geo-targets '["US"]'] [--campaign-id <guid>]
   ```
   `--enforcement-mode` accepts the names case-insensitively or `0`/`1`. Unknown subcommand/flag, missing required flag, or any `ArgumentException`/`ArgumentOutOfRangeException`/`FormatException` from the core methods → print the error and the usage block to stderr, return **2** (no DB touched). `SqlException`/`InvalidOperationException` during execution → print error, return **1**. Success → **0**.

5. **Wire into `TelemetryGuard.MigrationRunner/Program.cs`** — insert at the very top, before the existing `--clickhouse` check (everything below stays as DAT-01 wrote it):
   ```csharp
   if (args.Length > 0 && args[0].Equals("provision", StringComparison.OrdinalIgnoreCase))
   {
       var provisionCs = Environment.GetEnvironmentVariable("MIGRATIONS_CONNECTIONSTRING");
       if (string.IsNullOrWhiteSpace(provisionCs))
       {
           Console.Error.WriteLine("MIGRATIONS_CONNECTIONSTRING environment variable is not set.");
           return 2;
       }
       return await TelemetryGuard.MigrationRunner.Provisioning.ProvisionCommand.RunAsync(args[1..], provisionCs);
   }
   ```

6. **Well-known dev seed values** — deterministic so SDK fixtures, docs, and manual tests can hardcode them. These are the canonical values; other tasks may reference them by this table:

   | Item | Value |
   |------|-------|
   | Dev TenantId | `33333333-3333-3333-3333-333333333333` |
   | Tenant name / retention / mode | `Dev Tenant` / 90 / AutoEnforce (0) |
   | Dev API key (raw) | `tg_ak_dev0000000000000000000000000000000000000000` ("dev" + 40 zeros = 43 chars after prefix) |
   | API key scopes | `admin ingest report` |
   | Dev site key | `tg_sk_dev0000000000000000000` ("dev" + 19 zeros = 22 chars after prefix) |
   | Site domain / mode | `localhost` / `js` |
   | Dev CampaignId | `44444444-4444-4444-4444-444444444444` |
   | Campaign platform / external id | `google` / `dev-campaign-001` |
   | Campaign landing URL | `http://localhost:4650/landing.html` (SDK-06's bot-generator fixture port, so `--click-url` tracker runs land on the generated page) |
   | Campaign geo targets | `["US"]` |

   They deliberately avoid DAT-08's fixture ids (`1111…`, `2222…`). The dev API key is a well-known credential — local dev only, never provisioned anywhere reachable from the internet.

7. **Create `scripts/dev-seed.sh`** (mark executable, `chmod +x`):
   ```bash
   #!/usr/bin/env bash
   set -euo pipefail
   cd "$(dirname "$0")/.."

   # Requires the FND-02 compose stack: run ./scripts/dev-up.sh first.
   if [ ! -f .env ]; then
     echo "ERROR: .env not found — run ./scripts/dev-up.sh first (FND-02)." >&2
     exit 1
   fi
   set -a; source .env; set +a

   export MIGRATIONS_CONNECTIONSTRING="Server=localhost,1433;Database=TelemetryGuard;User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=True"

   echo "== Applying SQL Server migrations (DAT-01) =="
   dotnet run --project TelemetryGuard.MigrationRunner

   echo "== Applying ClickHouse schema (no-op until ANA-02 lands) =="
   CLICKHOUSE_CONNECTIONSTRING="Host=localhost;Port=8123;Database=${CLICKHOUSE_DB:-telemetry_guard};Username=${CLICKHOUSE_USER:-tg};Password=${CLICKHOUSE_PASSWORD:-tg-dev-password}" \
     dotnet run --project TelemetryGuard.MigrationRunner -- --clickhouse

   echo "== Provisioning well-known dev tenant (idempotent) =="
   dotnet run --project TelemetryGuard.MigrationRunner -- provision create-tenant \
     --tenant-id 33333333-3333-3333-3333-333333333333 \
     --name "Dev Tenant" --retention-days 90 --enforcement-mode AutoEnforce

   dotnet run --project TelemetryGuard.MigrationRunner -- provision issue-api-key \
     --tenant-id 33333333-3333-3333-3333-333333333333 \
     --scopes "admin ingest report" \
     --key tg_ak_dev0000000000000000000000000000000000000000

   dotnet run --project TelemetryGuard.MigrationRunner -- provision register-site \
     --tenant-id 33333333-3333-3333-3333-333333333333 \
     --domain localhost --integration-mode js \
     --site-key tg_sk_dev0000000000000000000

   dotnet run --project TelemetryGuard.MigrationRunner -- provision create-campaign \
     --tenant-id 33333333-3333-3333-3333-333333333333 \
     --campaign-id 44444444-4444-4444-4444-444444444444 \
     --platform google --external-id dev-campaign-001 \
     --landing-url http://localhost:4650/landing.html \
     --geo-targets '["US"]'

   cat <<'EOF'

   == Dev seed complete — well-known values ==
   TenantId : 33333333-3333-3333-3333-333333333333
   API key  : tg_ak_dev0000000000000000000000000000000000000000  (scopes: admin ingest report)
   Site key : tg_sk_dev0000000000000000000  (localhost, js mode)
   Campaign : 44444444-4444-4444-4444-444444444444  (google, dev-campaign-001)

   Try it (API dev port assumed 8080, per SDK-06; substitute what `dotnet run --project TelemetryGuard.Api` reports):
     snippet : <script async src="http://localhost:8080/sdk/tg.js" data-site-key="tg_sk_dev0000000000000000000"></script>   (SDK-08 route)
     pixel   : <img src="http://localhost:8080/p.gif?k=tg_sk_dev0000000000000000000" alt="">                               (API-03)
     tracker : http://localhost:8080/c?k=tg_sk_dev0000000000000000000&cid=44444444-4444-4444-4444-444444444444&gclid=test1 (API-02)
     admin   : curl -H "X-Api-Key: tg_ak_dev0000000000000000000000000000000000000000" http://localhost:8080/admin/whitelist (API-07)
     bots    : cd TelemetryGuard.Sdk && npm run bot-traffic -- --target http://localhost:8080 --site-key tg_sk_dev0000000000000000000  (SDK-06)
   EOF
   ```
   The script is orchestration only — every data operation happens inside the .NET runner (D1: no scripting-language data logic). Re-running it is safe: migrations are journaled (DAT-01) and every provision call takes the explicit-id skip path.

8. **Integration test — `tests/TelemetryGuard.Tests.Integration/Sql/ProvisioningTests.cs`** in the DAT-08 harness: `[Collection("sqlserver")]`, `[Trait("Category", "Integration")]`, constructor-injected `SqlServerFixture fx`, calling the `ProvisionCommand` core methods with `fx.ConnectionString`. If DAT-08 has not landed yet (`Sql/SqlServerFixture.cs` absent), inline a minimal private fixture in this file (Testcontainers `MsSqlBuilder` + `Migrations.RunSqlServer`, as DAT-08 specifies) and mark it with a comment for DAT-08 to consolidate — same seed pattern DAT-03 used for its RLS smoke test. Tests (add a project reference to `TelemetryGuard.MigrationRunner` if DAT-08's csproj does not already have one):
   - `Provisioned_tenant_visible_under_own_context_invisible_under_others` — **the headline proof**: provision a fresh tenant C (`CreateTenantAsync` with a new Guid) + a campaign for it; then
     `await using var asC = await fx.OpenAsync(tenantC);` → `SELECT COUNT(*) FROM dbo.Tenants WHERE TenantId = @tid` = **1** and the campaign row is readable;
     `await using var asA = await fx.OpenAsync(SqlServerFixture.TenantA);` → the same two queries return **0 rows / empty** (empty, not an error — RLS FILTER semantics per DAT-08);
     `await using var sys = await fx.OpenAsync(WellKnownTenants.System);` → tenant C is visible.
   - `Issued_api_key_hash_matches_printed_raw_key` — `IssueApiKeyAsync` returns the raw key; on an **unstamped** connection (`fx.OpenAsync(null)` — `dbo.ApiKeys` is RLS-exempt) the stored `KeyHash` equals `KeyGenerator.Sha256(rawKey)`; raw key matches `^tg_ak_[0-9A-Za-z]{43}$`.
   - `Issued_api_key_resolves_via_resolver` — build services per DAT-08's `RepositoryFactory.BuildServices`, resolve `ITenantResolver`, `ResolveApiKeyAsync(rawKey)` → tenant C with the issued scopes.
   - `Registered_site_key_resolvable_unstamped` — row visible on an unstamped connection by `SiteKey`; key matches `^tg_sk_[0-9A-Za-z]{22}$`.
   - `Generated_keys_are_unique_and_well_formed` — two `NewApiKey()`/`NewSiteKey()` calls differ; lengths 49/28; regexes hold.
   - `Sentinel_empty_and_out_of_range_inputs_rejected_before_sql` — `CreateTenantAsync` with the sentinel or `Guid.Empty` → `ArgumentOutOfRangeException`; retention 29/181 → `ArgumentOutOfRangeException`; enforcement 2 → `ArgumentOutOfRangeException`; `CreateCampaignAsync` with `"/relative"` → `ArgumentException`; assert no row was written.
   - `Explicit_id_reprovision_is_idempotent` — `CreateTenantAsync` twice with the same explicit id → same id back, one row; `IssueApiKeyAsync` twice with the same explicit key → one `dbo.ApiKeys` row.

## Files to create or modify
- `TelemetryGuard.MigrationRunner/Provisioning/KeyGenerator.cs` (new)
- `TelemetryGuard.MigrationRunner/Provisioning/ProvisionCommand.cs` (new)
- `TelemetryGuard.MigrationRunner/Program.cs` (modify: `provision` dispatch at top)
- `TelemetryGuard.MigrationRunner/TelemetryGuard.MigrationRunner.csproj` (modify: packages)
- `scripts/dev-seed.sh` (new, executable)
- `tests/TelemetryGuard.Tests.Integration/Sql/ProvisioningTests.cs` (new)
- `tests/TelemetryGuard.Tests.Integration/TelemetryGuard.Tests.Integration.csproj` (modify only if the `TelemetryGuard.MigrationRunner` reference is missing)

## Acceptance criteria
- `dotnet build TelemetryGuard.sln` succeeds; existing DAT-01 behavior is unchanged (`dotnet run --project TelemetryGuard.MigrationRunner` with no args still runs SQL Server migrations; `--clickhouse` still delegates).
- With a migrated disposable SQL Server (DAT-02's docker one-liner) and `MIGRATIONS_CONNECTIONSTRING` set:
  - `... -- provision create-tenant --name "T1"` exits 0 and prints `TenantId: <guid>`; under a sentinel-stamped `sqlcmd` session the row shows `Status=0, RetentionDays=90, EnforcementMode=0`.
  - `--retention-days 29` (or `181`), `--enforcement-mode 2`, `--tenant-id 00000000-0000-0000-0000-000000000001`, and a relative `--landing-url` each exit **2** with an error on stderr and write nothing.
  - `... -- provision issue-api-key --tenant-id <t>` prints the raw key exactly once, matching `^tg_ak_[0-9A-Za-z]{43}$`; the `dbo.ApiKeys` row's `KeyHash` equals the SHA-256 of that printed string; `issue-api-key` against a nonexistent tenant exits **1**.
  - `... -- provision register-site --tenant-id <t> --domain example.com` prints a key matching `^tg_sk_[0-9A-Za-z]{22}$` and the row is readable on an unstamped connection (resolution table).
  - Running without `MIGRATIONS_CONNECTIONSTRING` exits **2**.
- `./scripts/dev-seed.sh` against a fresh FND-02 stack exits 0 and provisions exactly the well-known values of step 6; running it a **second** time exits 0 with `already exists; skipping` lines and no duplicate rows.
- After seeding, `X-Api-Key: tg_ak_dev…` resolves to `33333333-3333-3333-3333-333333333333` and `?k=tg_sk_dev…` resolves the same tenant (verifiable via DAT-08's resolver test path, or manually once API-01/DAT-04 run).
- The raw API key exists nowhere at rest: `grep -rn "tg_ak_" TelemetryGuard.MigrationRunner/` matches only the `ApiKeyPrefix` constant and validation regex — no full key literals in C#; the dev literal appears only in `scripts/dev-seed.sh` and this task file.
- `dotnet test tests/TelemetryGuard.Tests.Integration --filter Category=Integration` passes, including all seven `ProvisioningTests`; the headline test asserts **empty (not error)** under tenant A and **visible** under tenant C and the sentinel.

## Testing
Integration tests per step 8 are the deliverable proof (they extend DAT-08's harness; keep them in the `sqlserver` collection so the container is shared). Validation guards are asserted there too (no separate unit project needed — the methods are static and the guards throw before any DB work). Manual proof: the acceptance `sqlcmd`/CLI sequence, plus one full `./scripts/dev-seed.sh` run pasted into the PR description.

## Out of scope / guardrails
- **Dapper + `Microsoft.Data.SqlClient` only; no EF Core** (D9). No server-side Python/Node (D1) — `dev-seed.sh` is bash orchestration that only invokes the .NET runner; all data logic lives in C#.
- **Every write goes through `ISystemConnectionFactory.OpenForTenantAsync(tenantId)` with an explicit `TenantId` value in the SQL** — never `TenantConnectionFactory` (no ambient request context in a console), never `IResolutionConnectionFactory`, never a raw `new SqlConnection` in provisioning code, and never `OpenSystemAsync` in the verbs (per-tenant stamping keeps the BLOCK predicate meaningful). `TenantId` is never optional, defaulted, or inferred.
- **Never store or log a raw API key** — SHA-256 hash only (DAT-02); the single sanctioned output is the `ApiKey:` stdout line at issuance. Never echo it in errors.
- **Never write or stamp the SYSTEM sentinel as data** — reject it (and `Guid.Empty`) before any SQL; DAT-02's CHECK is the backstop, not the primary guard.
- Do NOT add provisioning HTTP endpoints or UI — tenant self-service is the Phase 2 portal (P2-03); API-07's admin surface is whitelists, not tenancy. CLI only at MVP.
- Do NOT put seed data in migration scripts (DAT-02 guardrail) and do not modify any existing migration; provisioning is runtime DML, not schema.
- Do NOT touch ClickHouse or Redis in the provision verbs (D23 — config is SQL Server's); the seed script's `--clickhouse` step is DAT-01 schema delegation, not data.
- Keep the key prefixes `tg_ak_`/`tg_sk_` and base62 lengths exactly as pinned — DAT-04's shape regex, `varchar(64)`, SDK-06's generator, and the dev docs all assume them.
- The `--key`/`--site-key`/`--tenant-id`/`--campaign-id` overrides exist for deterministic dev/test seeding only; production issuance always uses generated random values, and the well-known dev credentials must never be provisioned outside localhost.
