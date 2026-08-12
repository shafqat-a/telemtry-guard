---
id: INT-04
title: Meta Marketing API wrapper
phase: 1.5
workstream: integrations
depends_on: [INT-02]
size: M
spec_refs: [D15, D21, D11]
detail_level: full
---

# INT-04: Meta Marketing API wrapper

## Objective

Implement the Meta half of D15 as a deliberately small, thin typed `HttpClient` wrapper (`IMetaMarketingClient`) plus a lightweight sync worker: `placement` entries from the exclusion queue are pushed into a per-tenant **publisher block list** via the Marketing API; `ip` entries are marked `Status=Unsupported` — **Meta has NO IP-exclusion API**, and this task states that honestly in code, docs, and reporting rather than pretending otherwise. System-user token configuration, a version-pinned Graph API base URL, minimal typed response records, and Graph error-envelope mapping complete the wrapper.

## Spec context (self-contained)

- **D15 — Meta**: no official .NET SDK exists → a thin typed `HttpClient` wrapper over the Marketing API, *kept deliberately small*. Do not grow this into a general Meta SDK: only the calls listed below.
- **HONESTY REQUIRED — capability truth table** (this is normative for this task and for tenant-facing reporting):

  | Exclusion source type | Google Ads (INT-03) | Meta (this task) |
  |---|---|---|
  | `ip` | Supported (negative campaign criteria) | **NOT SUPPORTED — no IP-exclusion API exists in the Marketing API.** Rows are marked `Unsupported`, never silently dropped. |
  | `placement` | Supported (negative placements) | Supported via **publisher block lists** (business-level lists of publisher URLs excluded from Audience Network / in-stream / Facebook placements). |

  `Unsupported` rows stay visible to tenants via `GET /admin/enforcement?status=unsupported` (INT-02) and later the portal (P2-03) — the product must never imply Meta IP blocking happened.
- **D21**: this worker consumes only `Approved` placement rows (`AutoEnforce` rows are born approved; `ApprovalQueue` rows were tenant-approved via INT-02). The challenge band is always automatic and entirely out of scope here.
- **Applying** a publisher block list to campaigns (ad set targeting `excluded_publisher_list_ids`) is NOT automated in this phase — the wrapper maintains the list; tenants attach it in Ads Manager. Document this limit in the runbook comment (deliberate smallness).
- **Graph API facts**: version-pinned base URL `https://graph.facebook.com/v21.0` (config, never hardcoded call-site strings); auth via a **system-user access token** passed as `access_token`; errors arrive as `{"error":{"message","type","code","error_subcode","fbtrace_id"}}`; publisher block lists live under the Business: `GET /{business_id}/publisher_block_lists`, create via `POST /{business_id}/block_list_drafts` with `name` + `publisher_urls` (the draft materializes as a block list), append via `POST /{block_list_id}` with `publisher_urls`. **Meta renames Graph edges between versions — before implementing, verify these three edge names against the changelog of the pinned version and adjust ONLY the path constants** (they are isolated in one file for exactly this reason).
- Tenancy (D11): per-tenant Meta identifiers, tenant-stamped connections, explicit `TenantId` predicates, RLS as primary enforcement. Dapper only (D9); no server-side Python/Node (D1).

## Prerequisites

- **INT-02** (read its task file): `ExclusionStatus` enum (`Pending=0, Approved=1, Rejected=2, Pushed=3, Failed=4, Unsupported=5`) and the queue table shape (expected `dbo.ExclusionQueue` — adapt names from DAT-06 via INT-02's file). INT-02's list endpoint is how `Unsupported` rows are surfaced.
- **DAT-03**: `ISystemConnectionFactory` for background-job connections; RLS policy; migration registry.
- **INT-03** (sibling, same phase): its migration pattern and worker structure — mirror them; if INT-03 has already claimed migration number `0006`, take the next free one.
- **API-01**: Api host for registration; appsettings aggregation point.

## Implementation steps

1. **Migration** — `TelemetryGuard.Data/migrations/NNNN_meta_sync.sql` (next free number; expected `0007`):

   ```sql
   ------------------------------------------------------------------------------
   -- NNNN_meta_sync.sql  (INT-04)
   -- Per-tenant Meta identifiers + the block-list id we manage for the tenant.
   ------------------------------------------------------------------------------
   ALTER TABLE dbo.Tenants ADD
       MetaBusinessId       varchar(32)  NULL,  -- Business Manager id; NULL = Meta sync disabled
       MetaBlockListId      varchar(32)  NULL;  -- our managed publisher block list; set on first sync
   GO
   ```
   (`dbo.Tenants` is already under RLS — no policy change needed.) Registry row in `migrations/README.md`.

2. **Options** — `TelemetryGuard.Integrations/Meta/MetaOptions.cs`, section `Meta`:

   ```csharp
   namespace TelemetryGuard.Integrations.Meta;

   public sealed class MetaOptions
   {
       public const string SectionName = "Meta";

       /// <summary>Version-pinned Graph API base. Bump deliberately; never call an unpinned URL.</summary>
       public string BaseUrl { get; set; } = "https://graph.facebook.com/v21.0";

       /// <summary>System-user access token (Business Settings → System users). Platform-level
       /// for MVP; per-tenant tokens are a later enhancement.</summary>
       public string SystemUserToken { get; set; } = "";

       public bool DryRun { get; set; } = true;      // same safety posture as INT-03
       public int SyncIntervalMinutes { get; set; } = 15;
       public int TimeoutSeconds { get; set; } = 10;
       public string BlockListNamePrefix { get; set; } = "TelemetryGuard exclusions";

       public bool EffectiveDryRun => DryRun || string.IsNullOrEmpty(SystemUserToken);
   }
   ```
   appsettings addition:
   ```json
   "Meta": { "BaseUrl": "https://graph.facebook.com/v21.0", "SystemUserToken": "",
             "DryRun": true, "SyncIntervalMinutes": 15, "TimeoutSeconds": 10,
             "BlockListNamePrefix": "TelemetryGuard exclusions" }
   ```

3. **Typed client** — `TelemetryGuard.Integrations/Meta/IMetaMarketingClient.cs` (the ENTIRE public surface; resist adding more):

   ```csharp
   namespace TelemetryGuard.Integrations.Meta;

   public sealed record MetaBlockList(string Id, string Name);

   /// <summary>Graph error envelope, mapped. Retryable: transient HTTP (5xx/timeout) and
   /// rate limiting (code 17, or 4 with error_subcode 2446079). Everything else is definitive.</summary>
   public sealed class MetaApiException(
       string message, string? type, int? code, int? errorSubcode, string? fbtraceId, bool retryable)
       : Exception(message)
   {
       public string? ErrorType { get; } = type;
       public int? Code { get; } = code;
       public int? ErrorSubcode { get; } = errorSubcode;
       public string? FbTraceId { get; } = fbtraceId;
       public bool Retryable { get; } = retryable;
   }

   /// <summary>Thin, deliberately small Marketing API surface (spec D15).
   /// HONESTY: Meta provides NO IP-exclusion API — this client cannot and will not
   /// offer one; ip exclusions are handled as Status=Unsupported by the sync worker.</summary>
   public interface IMetaMarketingClient
   {
       /// <summary>GET /{businessId}/publisher_block_lists — find our list by exact name; null when absent.</summary>
       Task<MetaBlockList?> FindBlockListAsync(string businessId, string name, CancellationToken ct);

       /// <summary>POST /{businessId}/block_list_drafts — create the list with initial URLs; returns its id.</summary>
       Task<string> CreateBlockListAsync(string businessId, string name,
           IReadOnlyList<string> publisherUrls, CancellationToken ct);

       /// <summary>POST /{blockListId} — append publisher URLs to an existing list. Idempotent
       /// (Meta ignores duplicates within a list).</summary>
       Task AddPublisherUrlsAsync(string blockListId,
           IReadOnlyList<string> publisherUrls, CancellationToken ct);
   }
   ```

4. **Implementation** — `MetaMarketingClient.cs`:
   - All paths built from `options.Value.BaseUrl` + a `private static class Edges { public const string PublisherBlockLists = "publisher_block_lists"; public const string BlockListDrafts = "block_list_drafts"; }` — the single place to fix if the pinned version renamed an edge (see Spec context).
   - Token goes in the POST body / query as `access_token` — NEVER log it; strip it from any logged URL (log path only).
   - Response parsing with `System.Text.Json` into minimal records: `{"data":[{"id","name"}],"paging":...}` for list; `{"id":"..."}` for create. Follow `paging.next` only for `FindBlockListAsync` (bounded to 10 pages).
   - Non-2xx: parse the error envelope into `MetaApiException` (fields above; unparseable body → message = raw body truncated 500 chars, retryable = status ≥ 500). One retry after 500 ms for `Retryable` failures, hand-rolled like INT-01 (no Polly).
   - `HttpClient.Timeout` from `TimeoutSeconds`.

5. **Sync worker** — `TelemetryGuard.Integrations/Meta/MetaExclusionSyncService.cs` (`BackgroundService`, `PeriodicTimer` on `SyncIntervalMinutes`, structure mirrors INT-03; keep it under ~150 lines — deliberately small):

   Per tenant (enumerated via `OpenSystemAsync` where `MetaBusinessId IS NOT NULL`, then `OpenForTenantAsync`):

   a. **Unsupported sweep (runs even in dry-run — it is a local truth, not a Meta call):**
      ```sql
      UPDATE dbo.ExclusionQueue
      SET Status = @Unsupported, UpdatedUtc = @now,
          LastError = N'Meta Marketing API provides no IP exclusion capability; entry cannot be enforced on Meta.'
      WHERE TenantId = @tid AND Platform = 'meta' AND SourceType = 'ip'
        AND Status IN (@Pending, @Approved)
      ```
   b. Load approved placements:
      ```sql
      SELECT ExclusionId, SourceValue FROM dbo.ExclusionQueue
      WHERE TenantId = @tid AND Platform = 'meta' AND SourceType = 'placement' AND Status = @Approved
      ORDER BY CreatedUtc
      ```
      `SourceValue` is the publisher URL/domain (e.g. `bad-publisher.example`).
   c. Dry-run gate: if `EffectiveDryRun` → log `[DRY-RUN]` plan, leave rows `Approved`, continue.
   d. Ensure the list: use `Tenants.MetaBlockListId` if set; else `FindBlockListAsync(businessId, $"{prefix} — {tenantId:D}")`; else `CreateBlockListAsync` with the batch; persist the id back to `dbo.Tenants.MetaBlockListId` on the tenant connection. If found/existing → `AddPublisherUrlsAsync`.
   e. Transition: success → all batch rows `Status=Pushed, UpdatedUtc=@now, LastError=NULL`; `MetaApiException` → all batch rows `Status=Failed, LastError=LEFT(message + ' fbtrace_id=' + FbTraceId, 2000)`. (List-level granularity is acceptable — the API appends URLs as one call; per-URL result granularity does not exist.)

6. **DI** — `MetaServiceCollectionExtensions.AddMetaExclusionSync(services, configuration)`: bind options, `AddHttpClient<IMetaMarketingClient, MetaMarketingClient>()`, `AddHostedService<MetaExclusionSyncService>()`. Register in `TelemetryGuard.Api/Program.cs`.

## Files to create or modify

- `TelemetryGuard.Data/migrations/NNNN_meta_sync.sql` (new) + `migrations/README.md` (registry row)
- `TelemetryGuard.Integrations/Meta/MetaOptions.cs`
- `TelemetryGuard.Integrations/Meta/IMetaMarketingClient.cs`
- `TelemetryGuard.Integrations/Meta/MetaMarketingClient.cs`
- `TelemetryGuard.Integrations/Meta/MetaExclusionSyncService.cs`
- `TelemetryGuard.Integrations/Meta/MetaServiceCollectionExtensions.cs`
- `TelemetryGuard.Api/Program.cs` (registration line)
- `TelemetryGuard.Api/appsettings.json` (`Meta` section)
- `tests/TelemetryGuard.Tests.Unit/Integrations/MetaMarketingClientTests.cs`
- `tests/TelemetryGuard.Tests.Unit/Integrations/MetaSyncServiceTests.cs`

## Acceptance criteria

- `dotnet build TelemetryGuard.sln` succeeds; migration applies and journals cleanly.
- **Honesty behaviors**:
  - Seeded `meta`/`ip` rows (both `Pending` and `Approved`) become `Unsupported` with the exact `LastError` text above after one cycle — even in dry-run; `google`/`ip` rows are untouched.
  - `GET /admin/enforcement?status=unsupported` (INT-02) returns them.
  - `grep -rn "ip" TelemetryGuard.Integrations/Meta/IMetaMarketingClient.cs` shows no IP-related API member; the no-IP-API statement appears in the interface XML docs.
- With a scripted `HttpMessageHandler` fake: `FindBlockListAsync` parses `data[]` and paging; `CreateBlockListAsync` posts `name` + `publisher_urls` and returns the id; error envelope `{"error":{"message":"...","type":"OAuthException","code":190,...}}` surfaces as `MetaApiException` with `Code=190`, `Retryable=false`; HTTP 500 retries once then throws with `Retryable=true`; the `access_token` never appears in logs (assert via a capturing logger).
- Worker tests (fake client + Testcontainers SQL via the DAT-08 fixture): approved placements → `Pushed` and block-list id persisted on first sync; `MetaApiException` → `Failed` with `fbtrace_id` in `LastError`; dry-run with token unset → placements stay `Approved`, zero client calls, but the ip sweep still ran; `Pending` placements are never pushed.
- All Graph URLs in the codebase start with the configured `BaseUrl` (single grep: `grep -rn "graph.facebook.com" TelemetryGuard.Integrations/` matches only `MetaOptions.cs`).

## Testing

Unit tests only, as listed in acceptance criteria — scripted `HttpMessageHandler` for the client, fake `IMetaMarketingClient` + SQL Testcontainers for the worker (extract the per-tenant body as internal `SyncTenantAsync` like INT-03 for direct-drive tests). No live Meta calls anywhere in tests/CI; manual smoke against a Meta test Business is documented in a `<remarks>` on the client.

## Out of scope / guardrails

- **Never fake IP enforcement**: no code, log line, or status may imply an IP was blocked on Meta. `Unsupported` is the only correct outcome for `meta`+`ip` rows.
- **Deliberately small** (D15): exactly the three client methods above. No campaign management, no targeting mutation (attaching `excluded_publisher_list_ids` to ad sets is explicitly out of scope), no insights/reporting endpoints, no general request builder.
- **Only `Approved → Pushed | Failed`** for placements; the ip sweep is the single exception allowed to act on `Pending` (it records impossibility, not enforcement). Never push `Pending` placements (D21 ApprovalQueue). Challenge band untouchable.
- **Version pinning**: one `BaseUrl` config value; verify edge names against the pinned version before coding; changing the version is a deliberate config+test change.
- **Dry-run default ON** until a system-user token is configured; secrets never logged or persisted outside configuration.
- Dapper on stamped connections only, explicit `TenantId` in every WHERE, RLS primary (D9/D11); no EF Core; no server-side Python/Node (D1); no broker (D12); no ClickHouse access (D23 — the queue is SQL); background-only, zero request-path latency (<50 ms budget unaffected).
