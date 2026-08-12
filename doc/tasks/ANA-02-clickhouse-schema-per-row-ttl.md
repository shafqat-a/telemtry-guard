---
id: ANA-02
title: ClickHouse schema with per-row TTL
phase: 1
workstream: analytics
depends_on: [FND-02]
size: M
spec_refs: [D6, D7, D10, D11, D18, D20, "section 8"]
detail_level: full
---

# ANA-02: ClickHouse schema with per-row TTL

## Objective

Create the ClickHouse DDL for the raw event store (`tg_events`), the training-label table (`tg_labels`), and a small `SchemaMigrator` class in `TelemetryGuard.Analytics.ClickHouse` that applies ordered, embedded SQL scripts exactly once (journaled in `tg_schema_migrations`). Each analytics provider owns its own schema scripts (D7) — this is the ClickHouse side.

## Spec context (self-contained)

- **D6/D7:** ClickHouse is the only implemented analytics engine for MVP. Each provider owns its own schema/migration scripts in its own project (`/schema/*.sql` here; the future Kusto provider will own `/schema/*.kql`). Never attempt engine-neutral DDL.
- **D10:** all schema management is versioned, numbered SQL scripts executed by a runner — no ORM migrations.
- **D11 (tenancy in ClickHouse):** `tenant_id` MUST be the FIRST column of every table's `ORDER BY` for aggressive partition pruning, e.g. `ORDER BY (tenant_id, timestamp)`. Every table has `tenant_id`; it is never optional.
- **D20 (retention):** per-tenant raw-event retention is 30–180 days (default 90). Implementation: `retention_days UInt16` is denormalized onto EVERY event row at ingest from tenant config, and the table-level TTL expression `timestamp + toIntervalDay(retention_days) DELETE` yields per-row expiry. One shared table serves all tenants — no per-tenant tables.
- **D18/D19:** training labels (`fraud`/`legit` from sources `t1_rule|synthetic_bot|conversion|review_screen`) are stored in ClickHouse in their own table with a FIXED 400-day TTL (labels must outlive the raw-event window to train future models; 400 days > max raw retention of 180 days plus a full year of seasonality).
- The column set mirrors the `ClickEvent` record from ANA-01 one-to-one (68 columns, including all four click-id columns `gclid`/`fbclid`/`msclkid`/`ttclid`). Null semantics: SDK numerics are `Float32` and hold `NaN` when absent (never 0); absent SDK boolean flags are `Nullable(UInt8)` NULL; velocity counters are non-nullable `Int32` (0 = legitimately cold).

## Prerequisites

- FND-02 provides the Docker Compose dev stack including a `clickhouse` service (image `clickhouse/clickhouse-server`, HTTP port 8123). Check `docker-compose.yml` (or `compose.yaml`) at the repo root for the actual service name, port mapping, user, and password, and use those values in the connection string below. Assumed default: `Host=localhost;Port=8123;Database=default;Username=default;Password=`.
- The project `TelemetryGuard.Analytics.ClickHouse/` exists at the repo root from the FND-01 scaffold (projects are NOT under `src/`), already referencing `TelemetryGuard.Core` and `TelemetryGuard.Analytics.Abstractions` (ANA-01), with a placeholder `schema/` folder.
- ANA-01 defined `ClickEvent` — its task file (`doc/tasks/ANA-01-analytics-abstractions.md`) lists the exact fields these columns mirror.
- DAT-01 (DbUp migration runner console) may or may not exist yet in the checkout — step 6 is conditional on it.

## Implementation steps

1. **NuGet:** add `ClickHouse.Client` (latest 7.x) to `TelemetryGuard.Analytics.ClickHouse/TelemetryGuard.Analytics.ClickHouse.csproj`. Also add to the csproj:
   ```xml
   <ItemGroup>
     <EmbeddedResource Include="schema/**/*.sql" />
   </ItemGroup>
   ```

2. **Create `TelemetryGuard.Analytics.ClickHouse/schema/0001_events.sql`** with exactly two statements. Column names/types/order are the storage contract consumed by ANA-03 (bulk copy) and ANA-04 (queries) — do not reorder or rename:
   ```sql
   -- tg_events: raw capture + verdict rows, one shared table for all tenants (D11/D20)
   CREATE TABLE IF NOT EXISTS tg_events
   (
       -- identity
       tenant_id                  UUID,
       site_key                   LowCardinality(String),
       session_id                 String,
       kind                       LowCardinality(String),   -- 'tracker'|'pixel'|'beacon'|'verdict'
       -- click ids (one column per supported ad platform — API-02 extracts all four)
       campaign_id                String DEFAULT '',
       gclid                      String DEFAULT '',
       fbclid                     String DEFAULT '',
       msclkid                    String DEFAULT '',
       ttclid                     String DEFAULT '',
       click_id_invalid           Nullable(UInt8),
       -- HTTP layer
       ip                         IPv6,
       header_names               Array(String),            -- ordered as received
       user_agent                 Nullable(String),
       sec_ch_ua                  Nullable(String),
       sec_ch_ua_mobile           Nullable(String),
       sec_ch_ua_platform         Nullable(String),
       accept_language            Nullable(String),
       referrer                   Nullable(String),
       tls_ja3                    Nullable(String),
       tls_ja4                    Nullable(String),
       cf_asn                     Nullable(UInt32),
       -- enrichment snapshot
       country                    LowCardinality(Nullable(String)),
       asn                        Nullable(UInt32),
       asn_org                    Nullable(String),
       asn_type                   LowCardinality(Nullable(String)),
       is_datacenter              Nullable(UInt8),
       is_proxy                   Nullable(UInt8),
       is_vpn                     Nullable(UInt8),
       is_tor                     Nullable(UInt8),
       is_private_relay           Nullable(UInt8),
       -- SDK summary (Float32 NaN = absent; Nullable(UInt8) NULL = absent)
       has_js_beacon              UInt8,
       beacon_integrity_ok        Nullable(UInt8),
       fingerprint_visitor_id     Nullable(String),
       storage_age_sec            Float32,
       webdriver_flag             Nullable(UInt8),
       headless_browser           Nullable(UInt8),
       screen_width               Float32,
       screen_height              Float32,
       timezone                   Nullable(String),
       language                   Nullable(String),
       mouse_event_count          Float32,
       key_event_count            Float32,
       touch_event_count          Float32,
       scroll_event_count         Float32,
       mean_inter_event_ms        Float32,
       std_inter_event_ms         Float32,
       mouse_path_linearity       Float32,
       first_interaction_delay_ms Float32,
       form_fill_time_sec         Float32,
       autofill_detected          Nullable(UInt8),
       paste_in_identity_fields   Nullable(UInt8),
       honeypot_touched           Nullable(UInt8),
       pointer_untrusted          Nullable(UInt8),
       input_modality_mismatch    Nullable(UInt8),
       time_on_page_sec           Float32,
       pages_viewed               Float32,
       -- velocity snapshot (0 = cold, never NULL/NaN)
       ip_clicks_last_min         Int32,
       ip_distinct_uas_last_hour  Int32,
       device_sessions_last_hour  Int32,
       device_ids_this_ip_hour    Int32,
       -- verdict block
       score                      Nullable(Int16),
       band                       LowCardinality(Nullable(String)),  -- 'allow'|'challenge'|'block'
       action                     LowCardinality(Nullable(String)),
       rule_hits                  Array(String),
       scorer_version             LowCardinality(Nullable(String)),
       feature_set_version        Nullable(UInt16),
       -- storage control
       retention_days             UInt16,                   -- denormalized at ingest (D20)
       timestamp                  DateTime64(3, 'UTC')
   )
   ENGINE = MergeTree
   PARTITION BY toYYYYMM(timestamp)
   ORDER BY (tenant_id, timestamp)
   TTL toDateTime(timestamp) + toIntervalDay(retention_days) DELETE;

   -- tg_labels: training labels (D18/D19), fixed 400-day TTL
   CREATE TABLE IF NOT EXISTS tg_labels
   (
       tenant_id    UUID,
       session_id   String,
       label        LowCardinality(String),   -- 'fraud'|'legit'
       label_source LowCardinality(String),   -- 't1_rule'|'synthetic_bot'|'conversion'|'review_screen'
       created_at   DateTime64(3, 'UTC')
   )
   ENGINE = MergeTree
   PARTITION BY toYYYYMM(created_at)
   ORDER BY (tenant_id, session_id, created_at)
   TTL toDateTime(created_at) + toIntervalDay(400) DELETE;
   ```
   Notes: the TTL expression wraps `DateTime64` in `toDateTime(...)` because TTL requires `Date`/`DateTime`; `IPv6` stores IPv4 as IPv4-mapped (`::ffff:a.b.c.d`) — writers convert (ANA-03).

3. **Create `TelemetryGuard.Analytics.ClickHouse/SchemaMigrator.cs`** (namespace `TelemetryGuard.Analytics.ClickHouse`):
   ```csharp
   using System.Reflection;
   using ClickHouse.Client.ADO;

   namespace TelemetryGuard.Analytics.ClickHouse;

   /// <summary>
   /// Applies embedded schema/*.sql scripts in ordinal filename order, once each,
   /// journaled in tg_schema_migrations. ClickHouse analog of the DbUp runner (D10).
   /// </summary>
   public sealed class SchemaMigrator(string connectionString)
   {
       public async Task<IReadOnlyList<string>> ApplyAsync(CancellationToken ct = default)
       {
           await using var conn = new ClickHouseConnection(connectionString);
           await conn.OpenAsync(ct);

           await ExecAsync(conn,
               """
               CREATE TABLE IF NOT EXISTS tg_schema_migrations
               (
                   script_name String,
                   applied_at  DateTime('UTC') DEFAULT now()
               )
               ENGINE = MergeTree
               ORDER BY script_name
               """, ct);

           var applied = new HashSet<string>(StringComparer.Ordinal);
           await using (var cmd = conn.CreateCommand())
           {
               cmd.CommandText = "SELECT DISTINCT script_name FROM tg_schema_migrations";
               await using var reader = await cmd.ExecuteReaderAsync(ct);
               while (await reader.ReadAsync(ct))
                   applied.Add(reader.GetString(0));
           }

           var asm = Assembly.GetExecutingAssembly();
           const string marker = ".schema.";
           var resources = asm.GetManifestResourceNames()
               .Where(n => n.Contains(marker, StringComparison.Ordinal)
                        && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
               .OrderBy(n => n, StringComparer.Ordinal)
               .ToList();

           var newlyApplied = new List<string>();
           foreach (var resource in resources)
           {
               var scriptName = resource[(resource.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
               if (applied.Contains(scriptName)) continue;

               using var stream = asm.GetManifestResourceStream(resource)
                   ?? throw new InvalidOperationException($"Missing embedded resource {resource}");
               using var text = new StreamReader(stream);
               var sql = await text.ReadToEndAsync(ct);

               foreach (var statement in SplitStatements(sql))
                   await ExecAsync(conn, statement, ct);

               await using var journal = conn.CreateCommand();
               journal.CommandText = "INSERT INTO tg_schema_migrations (script_name) VALUES ({name:String})";
               journal.AddParameter("name", scriptName);
               await journal.ExecuteNonQueryAsync(ct);
               newlyApplied.Add(scriptName);
           }
           return newlyApplied;
       }

       private static async Task ExecAsync(ClickHouseConnection conn, string sql, CancellationToken ct)
       {
           await using var cmd = conn.CreateCommand();
           cmd.CommandText = sql;
           await cmd.ExecuteNonQueryAsync(ct);
       }

       /// <summary>Splits on ';' at end of line. One statement per fragment; comment-only fragments skipped.</summary>
       internal static IEnumerable<string> SplitStatements(string sql)
       {
           foreach (var raw in sql.Replace("\r\n", "\n").Split(";\n"))
           {
               var stmt = raw.Trim().TrimEnd(';').Trim();
               if (stmt.Length == 0) continue;
               var commentOnly = stmt.Split('\n')
                   .All(l => l.Trim().Length == 0 || l.TrimStart().StartsWith("--", StringComparison.Ordinal));
               if (commentOnly) continue;
               yield return stmt;
           }
       }
   }
   ```
   Convention this imposes on all future schema scripts: statements end with `;` at end-of-line; never put a `;` followed by a newline inside a string literal in DDL.

4. **Config key.** The connection string lives at `Analytics:ClickHouse:ConnectionString` (bound fully in ANA-05). Nothing in this task hard-codes it outside tests/tooling.

5. **Numbering convention.** Future ClickHouse schema changes are new files `0002_*.sql`, `0003_*.sql`, ... in the same folder — never edit an applied script (the journal is by filename).

6. **MigrationRunner integration (conditional).** If the DAT-01 migration-runner console project exists in the checkout (look for a `MigrationRunner`/`TelemetryGuard.MigrationRunner` project — check `doc/tasks/DAT-01-*.md` for its exact name and CLI shape): add a `--clickhouse` switch that reads the `Analytics:ClickHouse:ConnectionString` config value (or `TG_CLICKHOUSE_CONNECTION` env var fallback) and runs `await new SchemaMigrator(cs).ApplyAsync()`, printing each newly applied script name, exit code 0 on success / non-zero on failure. Add a project reference from the runner to `TelemetryGuard.Analytics.ClickHouse`. If the runner does not exist yet, skip this step — `SchemaMigrator` is public and DAT-01 will wire it; leave a `// TODO(DAT-01)` note in the runner-facing XML doc of `SchemaMigrator`.

## Files to create or modify

- `TelemetryGuard.Analytics.ClickHouse/TelemetryGuard.Analytics.ClickHouse.csproj` (NuGet + EmbeddedResource; create project if scaffold lacks it)
- `TelemetryGuard.Analytics.ClickHouse/schema/0001_events.sql`
- `TelemetryGuard.Analytics.ClickHouse/SchemaMigrator.cs`
- (conditional) DAT-01's MigrationRunner `Program.cs` + csproj for the `--clickhouse` switch
- `TelemetryGuard.sln` (add project if missing)

## Acceptance criteria

- `dotnet build` succeeds; `0001_events.sql` is an embedded resource (verify: `dotnet build` then the migrator finds exactly one script).
- With the FND-02 stack up (`docker compose up -d clickhouse`), running `SchemaMigrator.ApplyAsync()` (via the runner's `--clickhouse` switch or a scratch test) creates `tg_events`, `tg_labels`, `tg_schema_migrations`; running it a second time applies nothing (returns empty list, journal unchanged at 1 row).
- `SHOW CREATE TABLE tg_events` shows: `ENGINE = MergeTree`, `PARTITION BY toYYYYMM(timestamp)`, `ORDER BY (tenant_id, timestamp)` (tenant first), and the TTL expression `toDateTime(timestamp) + toIntervalDay(retention_days)`.
- Per-row TTL proof: insert two rows — one with `retention_days = 1` and `timestamp = now() - INTERVAL 3 DAY`, one with `retention_days = 90` and the same timestamp; run `OPTIMIZE TABLE tg_events FINAL;` and confirm only the 90-day row remains.
- NaN storage proof: insert a row with `storage_age_sec = nan`; `SELECT isNaN(storage_age_sec) FROM tg_events WHERE ...` returns 1 (not 0.0).
- `tg_labels` accepts a `('fraud','t1_rule')` row and has the fixed 400-day TTL in `SHOW CREATE TABLE`.

## Testing

- Unit: `SchemaMigrator.SplitStatements` — asserts the 0001 script splits into exactly 2 statements, comment-only fragments are dropped, CRLF input works. Place in `tests/TelemetryGuard.Tests.Unit` (add a project reference to `TelemetryGuard.Analytics.ClickHouse`).
- Integration (this task, minimal — the full suite is ANA-06): a Testcontainers-based test `SchemaMigratorTests` in `tests/TelemetryGuard.Tests.Integration` (project exists from FND-01 with `Testcontainers.ClickHouse` 3.10.0 already pinned; image `clickhouse/clickhouse-server:24.8`): apply twice, assert idempotence and the TTL/NaN proofs above via raw `ClickHouseConnection` commands.

## Out of scope / guardrails

- **Provider-owned schema (D7):** this DDL lives in the ClickHouse project only. Do NOT create a shared/abstract schema model, do NOT add `.kql` files, do NOT touch SQL Server migrations (`TelemetryGuard.Data/migrations` belongs to the DAT workstream and DbUp).
- **`tenant_id` never optional:** every ClickHouse table created now or later must have `tenant_id` as the first `ORDER BY` column. No table without it.
- **Missing ≠ zero:** SDK numeric columns stay `Float32` (NaN-capable), absent flags stay `Nullable(UInt8)`. Do not change them to defaulted zeros or non-nullable booleans.
- **No per-tenant tables/databases** — per-row TTL on one shared table is the D20 design.
- Do not build ingestion (ANA-03), queries (ANA-04), or DI (ANA-05) here. No Grafana provisioning (OPS-01).
- No server-side Python/Node tooling for migrations — the runner is .NET (D1/D10).
- Do not use EF Core anywhere; relational access elsewhere in the repo is Dapper (D9) and analytics access is raw `ClickHouse.Client`.
