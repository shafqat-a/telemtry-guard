# TelemetryGuard.Analytics.Kusto

The second — and final (D6: "exactly two provider implementations ever
needed") — analytics provider. Covers both Azure Data Explorer and Microsoft
Fabric Eventhouse: same engine, same .NET SDK, differing only in the
connection string (D6). Never references `TelemetryGuard.Analytics.ClickHouse`,
and is never referenced by it (D7) — the provider switch lives in the
composition root, `TelemetryGuard.Api/Analytics/AnalyticsServiceCollectionExtensions.cs`.

**No real ADX/Fabric endpoint is ever contacted by this repo's tests, CI, or
default configuration.** The only Kusto endpoint anything here may reach is a
locally started emulator container (`mcr.microsoft.com/azuredataexplorer/kustainer-linux`).

## Step 0 — emulator reconnaissance answers

Verified against a running `kustainer-linux:latest` container (podman, host
port mapped, `Federated Security=False`, database `NetDefaultDB`) before
writing any provider code. Record kept here so nobody re-runs this blind.

| # | Question | Answer | Notes |
| --- | --- | --- | --- |
| Q1 | Streaming ingestion via `KustoIngestFactory.CreateStreamingIngestClient` against the engine endpoint? | **No** (fallback) | The SDK's streaming client consistently returned `BadRequest_StreamingIngestionPolicyNotEnabled` on this image even with the streaming-ingestion policy explicitly enabled at table, database AND cluster level (`.alter table … policy streamingingestion enable`, `.alter database … policy streamingingestion enable`, `.alter cluster policy streamingingestion '{"IsEnabled":true}'`, plus a settle delay). `StreamingKustoIngestTransport` instead issues `.ingest inline into table <table> with (format='multijson', ingestionMappingReference='<mapping>') <\| <payload>` through `IKustoQueryExecutor.ExecuteControlCommandAsync` — verified to ingest a JSON-ARRAY multijson payload (exactly what `KustoEventSink.WriteBatchJson` produces) with correct NaN round-tripping, and to become query-visible in well under a second. This transport is documented `<remarks>` emulator/dev only; production uses `QueuedKustoIngestTransport` (`IngestMode=Queued`), which was NOT re-implemented against the fallback path — it uses the real Data Management ingest client, which has no analogous policy quirk on a real cluster. |
| Q2 | Does a JSON `"NaN"` string ingest into a `real` column as `double.NaN`? | **Yes (primary)** | `q1test \| where isnan(f) \| count` returned the ingested row after writing `"f":"NaN"`. `KustoEventSink`/`KustoLabelSink` write JSON string `"NaN"` for any non-finite `float`; `KustoValueMapping.RealToFloat` also treats a JSON/Kusto `null` as `NaN` on read, so the fallback encoding (null for non-finite) would also satisfy §7 if a future cluster behaves differently — but the primary (string `"NaN"`) is what ships. |
| Q3 | Is `count_distinctif()` available? | **Yes** | Used directly in `GetIpVelocityAsync`'s KQL; no `dcountif` fallback needed. |
| Q4 | Does `\| take lim` accept a declared query parameter? | **Yes** | `declare query_parameters(lim: long); … \| take lim` and `… \| partition by day (top lim by scored_events desc)` both accept a bound `long` parameter on the emulator. `limit`/`limitPerDay` are validated in C# (`ArgumentOutOfRangeException.ThrowIfNegativeOrZero`) and then bound, never interpolated. |
| Q5 | Does `.delete table tg_events records <\| …` (soft delete) run on the emulator? | **Yes** | `KustoRetentionSweeper`'s sweep command runs and swept rows disappear from subsequent queries (see `KustoEngineTests.SweepAsync_D20_RemovesRowsPastTheirOwnRetentionDays_KeepsOthers`, run against the real emulator). |
| Q6 | Does `.alter-merge table … policy retention softdelete = …` run on the emulator? | **Yes** | Applied by `schema/0001_events.kql`; `.show table tg_events policy retention` reports the 180-day backstop after `KustoSchemaMigrator.ApplyAsync()`. |
| Q7 | Does `.create database <name> …` work, or must the fixture use the built-in `NetDefaultDB`? | **Must use `NetDefaultDB`** | `.create database …` failed on this image with `Database MD container path: '/tmp' must be empty` — an emulator-container-storage limitation, not something a real ADX/Fabric deployment hits. `KustoProviderFixture` and `KustoAnalyticsOptions.Database` both default/target `NetDefaultDB`; `Database` stays a real option so a production deployment names its own database. |

### An eighth finding, not in the original table: `kind` is a KQL reserved word

`kind` (one of the 71 `tg_events` columns, and one of the most-queried) collides
with the KQL keyword used by e.g. `join kind=inner`. Declaring or referencing it
as a bare identifier fails to parse (`SYN0002`/`General_BadRequest`) both in
`.create-merge table tg_events (kind: string, …)` and in `\| where kind == 'verdict'`.
Fixed by bracket-quoting **everywhere it is declared or referenced as a KQL
identifier**: `['kind']: string` in `schema/0001_events.kql`, and `['kind']`
in every `KustoAnalyticsQueries` predicate. The underlying column name is
still the plain string `"kind"` — `KustoEventSink.ColumnNames`, the ingestion
mapping's `"Column"` values, and JSON payload keys are all unaffected, because
those are never parsed as KQL identifiers. `KustoIngestionMappingTests`
verifies the three artifacts (table declaration, ingestion mapping, ColumnNames)
still agree name-for-name once the bracket-quoting is stripped back out.

### Also worth recording: namespace collision with `Kusto.*`

This project's own namespace is `TelemetryGuard.Analytics.Kusto` — it ends in
`.Kusto`. Writing a fully-qualified reference like `Kusto.Data.Common.X` inside
this namespace (or `TelemetryGuard.Tests.Contracts.Kusto`, the contract-suite
runner's namespace) resolves `Kusto` relative to the enclosing namespace FIRST
and fails with `CS0234`. Every file that touches the SDK imports `Kusto.Data`,
`Kusto.Data.Common`, `Kusto.Data.Net.Client`, `Kusto.Data.Ingestion`, and/or
`Kusto.Ingest` via `using` and references their types unqualified — never with
a leading `Kusto.` prefix.

### Package version drift

`dotnet add package` resolved `Microsoft.Azure.Kusto.Data`/`Microsoft.Azure.Kusto.Ingest`
to **14.2.1** (pinned, per the task's "pin whatever resolves" instruction).
That version's `Azure.Core` transitive dependency (1.57.0) requires
`Microsoft.Extensions.{Hosting,Logging,DependencyInjection}.Abstractions` and
`Microsoft.Extensions.Options` **>= 10.0.3**, one major series ahead of this
repo's usual `8.0.x` pin for those packages (the style the ClickHouse project
uses). Pinning at 8.0.x produces `NU1605` package-downgrade errors, which
`TreatWarningsAsErrors=true` turns into build failures. This project (and
`tests/TelemetryGuard.Tests.Contracts`, which references it) pin those four
packages at `10.0.3` instead; `Microsoft.Extensions.Options.ConfigurationExtensions`
stays at `8.0.0` (no such conflict there).

## Retention (D20): the Kusto mechanism, and why

Kusto has no per-row TTL, so D20 ("30-180 day, default 90, per-tenant
retention") is implemented in two layers here:

1. **Backstop (declarative, in `schema/0001_events.kql`):**
   `.alter-merge table tg_events policy retention softdelete = 180d` — 180
   days is D20's stated maximum, so nothing can outlive the policy even if the
   sweep stops running. `tg_labels` gets a fixed `400d`, mirroring
   ClickHouse's fixed 400-day label TTL.
2. **Per-row (imperative, `KustoRetentionSweeper` + `KustoRetentionService`):**
   a scheduled `.delete table tg_events records <\| …` soft delete, run daily
   by default (`RetentionSweepIntervalHours`), driven by the `retention_days`
   column every row already carries — the same denormalized value ClickHouse's
   TTL expression reads. This is the piece that makes a 30-day tenant actually
   expire at 30 days, not just "no later than 180".

**Rejected alternatives** (recorded so nobody re-litigates them):

- *Retention-bucket tables* (one table per retention value, with update-policy
  routing) multiplies the schema by the 30-180 day range and breaks the
  single-`tg_events` shape every query assumes.
- *`.purge`* is a DM-side GDPR tool measured in hours and is unavailable on
  the emulator; it also over-serves the need (D20 is routine expiry, not a
  compliance erasure request).
- *Materialized views* solve aggregation, not expiry — orthogonal to this
  problem.

## Queued vs. streaming ingestion — the production trade-off

`Analytics:Kusto:IngestMode` is `"Queued"` (production default) or
`"Streaming"` (emulator/dev):

- **Queued** (`QueuedKustoIngestTransport`) — the real Data Management
  queued-ingestion client. Visibility lag is measured in seconds even with
  `FlushImmediately = true`; this is D7's weak "eventual, batched" guarantee
  by design, and it is what production and Fabric Eventhouse both use — the
  Kusto emulator has no DM service, so this transport is never exercised
  against it.
- **Streaming** (`StreamingKustoIngestTransport`) — the `.ingest inline`
  control-command fallback described under Q1 above. Near-immediate
  visibility, but it embeds the whole batch as KQL text in a control command,
  which does not scale to production ingestion volumes or the DM service's
  queueing/backpressure. **Emulator/dev only, never production.**

## Emulator ≠ production parity

Every one of the Q1-Q7 answers above was captured against `kustainer-linux`,
not a real ADX or Fabric Eventhouse cluster. Known gaps between the two:

- Authentication: the emulator runs with `Federated Security=False`
  (anonymous); a real cluster requires AAD (managed identity / app
  credentials / user token) — `KustoConnectionStringBuilder` supports all of
  these, but none are exercised by this repo's tests.
- Queued ingestion: the emulator has no DM service at all, so `IngestMode=Queued`
  is entirely untested against the emulator — only unit-tested (mapping/
  serialization) and, per the manual procedure below, meant to be smoke-tested
  by a human against a real dev cluster before any production cutover.
- Policy behavior (retention, streaming ingestion, capacity) can differ
  between the emulator's single-node implementation and a real multi-node
  cluster or Fabric's OneLake-backed storage.
- Performance/scale characteristics are not comparable at all — the emulator
  is a single container with no autoscale, no multi-node query fan-out.

## Manual smoke procedure (human-run only — NEVER CI, NEVER production)

1. Provision a **dev** ADX cluster or Fabric Eventhouse (out of scope for
   this repo — done via the Azure/Fabric portal or CLI by a human with
   appropriate access).
2. Create a dev database on it.
3. Run the schema migrator against it:
   ```
   KUSTO_CONNECTIONSTRING="Data Source=https://<cluster>.<region>.kusto.windows.net;Fed=true" \
   KUSTO_DATABASE=<dev-db-name> \
   dotnet run --project TelemetryGuard.MigrationRunner -- --kusto
   ```
4. Set `Analytics:Provider=Kusto` in a **local, non-committed** config
   override, with `Analytics:Kusto:ConnectionString`/`IngestConnectionString`
   pointed at the dev cluster's engine/DM endpoints and `IngestMode=Queued`.
5. Run the API locally against that config; drive a handful of tracker/pixel/
   beacon/verdict events through it by hand (or via the SDK fixture pages).
6. Verify: rows appear in `tg_events`/`tg_labels` within the expected queued-
   ingestion lag (seconds, not the emulator's sub-second streaming path);
   `GetIpVelocityAsync`/`GetCampaignReportAsync`/`GetTopFlaggedSourcesAsync`/
   `GetTopPlacementsDailyAsync`/`GetSiteDailyCountsAsync` all return sane
   results for the driven traffic; `/ready` reports the `kusto` health check
   healthy.
7. Tear down the dev database/cluster when done.

No CI job, automated test, or script in this repository may ever point at a
real ADX/Fabric endpoint — the connection string keywords `kusto.windows.net`
and `kusto.fabric.microsoft.com` appear only in this documentation and in the
placeholder examples in `KustoAnalyticsOptions`' XML doc comments.
