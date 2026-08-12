---
id: INT-03
title: Google Ads exclusion sync
phase: 1.5
workstream: integrations
depends_on: [INT-02, DAT-06]
size: L
spec_refs: [D15, D21, D20, D11, "§6.1 step 5"]
detail_level: full
---

# INT-03: Google Ads exclusion sync

## Objective

Implement `GoogleAdsExclusionSyncService` — a `BackgroundService` in `TelemetryGuard.Integrations` (hosted by the Api process) that every 15 minutes pushes `approved` Google exclusion-queue rows to Google Ads via the official `Google.Ads.GoogleAds` .NET client: `ip` rows become negative campaign criteria (IP blocks), `placement` rows become negative placement criteria, both sent as batched mutates with partial-failure handling. Includes `GoogleAdsOptions` (developer token + OAuth refresh-token credentials + login customer id), a small migration adding the per-tenant Google Ads customer id and a pushed-criteria state table, a documented LRU eviction strategy for Google's ~500-IP-exclusions-per-campaign cap, `pushed|failed` status transitions with error capture, and a dry-run mode that is ON by default until real credentials are configured.

## Spec context (self-contained)

- **D15 — Ad-platform enforcement, Google half**: use the official `Google.Ads.GoogleAds` .NET client to maintain IP exclusion lists / placement exclusions. (Meta is INT-04's thin HttpClient wrapper — not this task.)
- **§6.1 step 5**: verdicts scored 71–100 are excluded from attribution and *queued* for exclusion-list sync. API-06 writes the queue rows; INT-02 governs `pending → approved | rejected` per the tenant's `EnforcementMode` (D21: `AutoEnforce` rows are born approved; `ApprovalQueue` rows need tenant approval). **This worker consumes ONLY `approved` rows** — it must never push `pending` rows, and never touches the challenge band.
- **D20**: exclusion-list entries retain IPs for as long as the exclusion is active (operationally necessary) — the state table below is that record; it does not follow raw-event retention windows.
- **D11 — tenancy**: background jobs use `ISystemConnectionFactory` (DAT-03): the SYSTEM-sentinel connection ONLY to enumerate tenants, then a per-tenant stamped connection (`OpenForTenantAsync`) for that tenant's unit of work — RLS scopes every statement. Explicit `WHERE TenantId = @tid` stays in all SQL. `TenantId` is never optional.
- **Google Ads API facts this design rests on**:
  - IP exclusions are **negative campaign criteria** (`CampaignCriterion { Negative = true, IpBlock = { IpAddress } }`) — they exist per campaign, not per account.
  - Placement exclusions are negative campaign criteria with `Placement = { Url }` (campaign-level negative placements).
  - Google caps IP exclusions at **~500 per campaign** (product limit). Beyond the cap, adds fail — hence the LRU eviction strategy (below).
  - Mutations go through `CampaignCriterionService.MutateCampaignCriteria` with `PartialFailure = true` so one bad criterion does not sink the batch.
  - Auth: developer token + OAuth2 refresh-token flow (installed-app credentials), optional `login-customer-id` (MCC) header. The tenant's own account is addressed by its 10-digit customer id (no dashes).
- **Dry-run default ON**: until `DeveloperToken` and `OAuthRefreshToken` are configured (and `GoogleAds:DryRun` is explicitly set false), the worker must only LOG what it would push — no Google calls, no status transitions. A misconfigured fresh deployment must be inert, never accidentally mutating live ad accounts.
- Dapper + DbUp SQL scripts only (D9/D10); no EF Core; no server-side Python/Node (D1).

## Prerequisites

- **INT-02** (read its task file): `ExclusionStatuses` string constants (`"pending"`, `"approved"`, `"rejected"`, `"pushed"`, `"failed"`, `"unsupported"` — the queue's `Status` column is `varchar(16)`, NOT a numeric enum) in `TelemetryGuard.Data/Exclusions/`, the `UpdatedUtc` column its migration added, and the approval flow that produces `'approved'` rows. Failed rows are terminal for this worker; re-queueing is a manual `UPDATE ... SET Status='approved'` (documented, not built).
- **DAT-06** (migration `0003`, authoritative — read it): `dbo.ExclusionQueue`'s ACTUAL columns: `TenantId uniqueidentifier`, `Id bigint IDENTITY` (PK `(TenantId, Id)` — there is no GUID `ExclusionId`), `SourceType varchar(16)` (`'ip'|'placement'`), `Value varchar(256)`, `Reason nvarchar(400)`, `Status varchar(16)` (string CHECK, `'rejected'` added by INT-02), `CampaignScope uniqueidentifier NULL` (NULL = tenant-wide), `CreatedUtc`, `PushedUtc NULL`, plus `UpdatedUtc NULL` (added by INT-02's migration). It has NO `Platform` or `LastError` columns — **this task's migration adds them (step 2)**.
- **DAT-03**: `ISystemConnectionFactory` (`OpenSystemAsync` / `OpenForTenantAsync`) in `TelemetryGuard.Data`; RLS policy `rls.TenantIsolationPolicy`; migration registry `TelemetryGuard.Data/migrations/README.md`.
- **DAT-02**: `dbo.Tenants` (gains a column here) and `dbo.Campaigns` with `ExternalCampaignId varchar(64) NULL` — the Google campaign id used to build criterion resource names, and `Platform` (`'google'` rows are in scope).
- **API-01**: Api host `Program.cs` where the hosted service and options are registered.

## Implementation steps

1. **NuGet**: `dotnet add TelemetryGuard.Integrations package Google.Ads.GoogleAds` (latest stable; 21.x+). The snippets below use the newest `Google.Ads.GoogleAds.V*` namespace exposed by the installed package — pick the highest available (e.g. `V19`/`V20`) and use it consistently; the types named here exist in every recent version.

2. **Migration** — `TelemetryGuard.Data/migrations/NNNN_google_ads_sync.sql` (next free number after INT-02's; expected `0006`). Content:

   ```sql
   ------------------------------------------------------------------------------
   -- NNNN_google_ads_sync.sql  (INT-03)
   -- Per-tenant Google Ads account id + pushed-criteria state (exclusion-sync
   -- state is SQL Server's job per D8; rows live while the exclusion is active, D20).
   ------------------------------------------------------------------------------
   ALTER TABLE dbo.Tenants ADD
       GoogleAdsCustomerId varchar(10) NULL;  -- 10 digits, no dashes; NULL = sync disabled for tenant
   GO

   -- DAT-06's queue has no Platform or LastError column (UpdatedUtc came from INT-02's
   -- migration). Add them here: sync workers scope rows per platform and record push
   -- errors. Existing rows default to 'google' (the only platform API-06 targets at
   -- MVP); API-06 (or a backfill here) derives Platform from dbo.Campaigns.Platform
   -- for campaign-scoped rows once Meta campaigns exist (INT-04).
   ALTER TABLE dbo.ExclusionQueue ADD
       Platform  varchar(16)    NOT NULL CONSTRAINT DF_EQ_Platform DEFAULT ('google'),
       LastError nvarchar(2000) NULL;
   GO

   CREATE TABLE dbo.GoogleAdsPushedExclusions
   (
       TenantId              uniqueidentifier NOT NULL,
       CriterionResourceName nvarchar(256)    NOT NULL,  -- customers/{cid}/campaignCriteria/{campaignId}~{criterionId}
       GoogleCampaignId      varchar(32)      NOT NULL,
       SourceType            varchar(16)      NOT NULL,  -- 'ip' | 'placement'
       SourceValue           nvarchar(512)    NOT NULL,
       ExclusionQueueId      bigint           NOT NULL,  -- originating dbo.ExclusionQueue.Id
       PushedUtc             datetime2(3)     NOT NULL,
       CONSTRAINT PK_GoogleAdsPushedExclusions PRIMARY KEY CLUSTERED (TenantId, CriterionResourceName),
       CONSTRAINT CK_GAPE_SourceType CHECK (SourceType IN ('ip', 'placement'))
   );
   GO
   -- LRU scan path: oldest pushed ip criteria per campaign.
   CREATE NONCLUSTERED INDEX IX_GAPE_Lru
       ON dbo.GoogleAdsPushedExclusions (TenantId, GoogleCampaignId, SourceType, PushedUtc);
   GO
   ALTER SECURITY POLICY rls.TenantIsolationPolicy
       ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.GoogleAdsPushedExclusions,
       ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.GoogleAdsPushedExclusions;
   GO
   ```
   Append the registry row in `migrations/README.md`.

3. **Options** — `TelemetryGuard.Integrations/GoogleAds/GoogleAdsOptions.cs`, bound from section `GoogleAds`:

   ```csharp
   namespace TelemetryGuard.Integrations.GoogleAds;

   public sealed class GoogleAdsOptions
   {
       public const string SectionName = "GoogleAds";

       public string DeveloperToken { get; set; } = "";
       public string OAuthClientId { get; set; } = "";
       public string OAuthClientSecret { get; set; } = "";
       public string OAuthRefreshToken { get; set; } = "";
       /// <summary>MCC/manager account id (10 digits, no dashes); empty when tenants are accessed directly.</summary>
       public string LoginCustomerId { get; set; } = "";

       /// <summary>SAFETY: true by default. The worker only logs its plan until this is
       /// explicitly false AND credentials are present (see EffectiveDryRun).</summary>
       public bool DryRun { get; set; } = true;

       public int SyncIntervalMinutes { get; set; } = 15;
       public int MaxIpExclusionsPerCampaign { get; set; } = 500; // Google product cap (~500)
       public int MutateBatchSize { get; set; } = 500;            // operations per mutate request
       public int QueueBatchSize { get; set; } = 1000;            // queue rows fetched per tenant per cycle

       public bool CredentialsConfigured =>
           !string.IsNullOrEmpty(DeveloperToken) && !string.IsNullOrEmpty(OAuthRefreshToken)
           && !string.IsNullOrEmpty(OAuthClientId) && !string.IsNullOrEmpty(OAuthClientSecret);

       /// <summary>Dry-run unless explicitly disabled AND fully configured.</summary>
       public bool EffectiveDryRun => DryRun || !CredentialsConfigured;
   }
   ```
   Add to `TelemetryGuard.Api/appsettings.json`:
   ```json
   "GoogleAds": {
     "DeveloperToken": "", "OAuthClientId": "", "OAuthClientSecret": "",
     "OAuthRefreshToken": "", "LoginCustomerId": "",
     "DryRun": true, "SyncIntervalMinutes": 15,
     "MaxIpExclusionsPerCampaign": 500, "MutateBatchSize": 500, "QueueBatchSize": 1000
   }
   ```

4. **Google client seam** — so the worker is testable without the Google API, define a thin interface `TelemetryGuard.Integrations/GoogleAds/IGoogleAdsGateway.cs`:

   ```csharp
   namespace TelemetryGuard.Integrations.GoogleAds;

   public sealed record CriterionAdd(string GoogleCampaignId, string SourceType, string SourceValue);
   public sealed record MutateOutcome(
       IReadOnlyList<(CriterionAdd Add, string ResourceName)> Created,
       IReadOnlyList<(CriterionAdd Add, string Error)> Failures);

   /// <summary>The only type that talks to Google. One instance per tenant customer id.</summary>
   public interface IGoogleAdsGateway
   {
       /// <summary>Batched MutateCampaignCriteria with PartialFailure=true.
       /// removes = criterion resource names to delete (LRU eviction).</summary>
       Task<MutateOutcome> MutateAsync(
           string customerId, IReadOnlyList<CriterionAdd> adds,
           IReadOnlyList<string> removes, CancellationToken ct);
   }
   ```

   Implementation `GoogleAdsGateway.cs` (adjust `V19` to the installed version):

   ```csharp
   using Google.Ads.Gax.Config;
   using Google.Ads.GoogleAds;
   using Google.Ads.GoogleAds.Config;
   using Google.Ads.GoogleAds.V19.Common;
   using Google.Ads.GoogleAds.V19.Errors;
   using Google.Ads.GoogleAds.V19.Resources;
   using Google.Ads.GoogleAds.V19.Services;

   public sealed class GoogleAdsGateway(IOptions<GoogleAdsOptions> options) : IGoogleAdsGateway
   {
       private GoogleAdsClient CreateClient()
       {
           var o = options.Value;
           return new GoogleAdsClient(new GoogleAdsConfig
           {
               DeveloperToken = o.DeveloperToken,
               OAuth2Mode = Google.Ads.Gax.Config.OAuth2Flow.APPLICATION,
               OAuth2ClientId = o.OAuthClientId,
               OAuth2ClientSecret = o.OAuthClientSecret,
               OAuth2RefreshToken = o.OAuthRefreshToken,
               LoginCustomerId = string.IsNullOrEmpty(o.LoginCustomerId) ? null : o.LoginCustomerId
           });
       }

       public async Task<MutateOutcome> MutateAsync(
           string customerId, IReadOnlyList<CriterionAdd> adds,
           IReadOnlyList<string> removes, CancellationToken ct)
       {
           var service = CreateClient().GetService(Services.V19.CampaignCriterionService);
           var operations = new List<CampaignCriterionOperation>();
           foreach (var rn in removes)
               operations.Add(new CampaignCriterionOperation { Remove = rn });
           foreach (var a in adds)
           {
               var criterion = new CampaignCriterion
               {
                   Campaign = ResourceNames.Campaign(long.Parse(customerId), long.Parse(a.GoogleCampaignId)),
                   Negative = true
               };
               if (a.SourceType == "ip") criterion.IpBlock = new IpBlockInfo { IpAddress = a.SourceValue };
               else criterion.Placement = new PlacementInfo { Url = a.SourceValue };
               operations.Add(new CampaignCriterionOperation { Create = criterion });
           }

           var request = new MutateCampaignCriteriaRequest
           {
               CustomerId = customerId,
               PartialFailure = true,
               Operations = { operations }
           };
           var response = await service.MutateCampaignCriteriaAsync(request);

           // Partial-failure mapping. Results align 1:1 with operations INCLUDING the
           // removes — a successful remove returns the REMOVED criterion's resource name
           // (do NOT assume it is empty). Operation index layout: 0..removes.Count-1 are
           // removes; removes.Count + i is adds[i].
           // (1) Decode the failure payload (null when every operation succeeded):
           GoogleAdsFailure? failure = response.PartialFailureError is null
               ? null
               : GoogleAdsFailure.Parser.ParseFrom(response.PartialFailureError.Details[0].Value);
           // (equivalent helpers: ErrorUtilities.IsPartialFailure(response.PartialFailureError)
           //  then ErrorUtilities.GetGoogleAdsFailure(...) — use whichever the installed
           //  version exposes; the manual Parser.ParseFrom above works on all of them)
           // (2) Each error names its operation via Location.FieldPathElements: the element
           //     whose FieldName == "operations" carries the failed operation's Index.
           var failedByIdx = new Dictionary<int, string>();
           if (failure is not null)
               foreach (var e in failure.Errors)
               {
                   var opIdx = (int)e.Location.FieldPathElements
                       .First(p => p.FieldName == "operations").Index;
                   failedByIdx[opIdx] = failedByIdx.TryGetValue(opIdx, out var prior)
                       ? $"{prior}; {e.Message}" : e.Message;
               }
           // (3) Map add outcomes back by index: failed adds -> Failures with the error
           //     message; every other add -> Created with the result's resource name.
           var created = new List<(CriterionAdd, string)>();
           var failures = new List<(CriterionAdd, string)>();
           for (var i = 0; i < adds.Count; i++)
           {
               var opIdx = removes.Count + i;
               if (failedByIdx.TryGetValue(opIdx, out var err)) failures.Add((adds[i], err));
               else created.Add((adds[i], response.Results[opIdx].ResourceName));
           }
           return new MutateOutcome(created, failures);
       }
   }
   ```
   Wrap the whole call in `try/catch (GoogleAdsException ex)` — an exception at request level (auth, quota) fails ALL adds in the batch with `ex.Failure?.ToString() ?? ex.Message`.

5. **Sync planner (pure, unit-testable)** — `TelemetryGuard.Integrations/GoogleAds/SyncPlanner.cs`:

   ```csharp
   public sealed record CampaignPlan(
       string GoogleCampaignId,
       IReadOnlyList<CriterionAdd> Adds,
       IReadOnlyList<string> EvictResourceNames); // LRU evictions, ip only

   public static class SyncPlanner
   {
       /// <summary>
       /// LRU EVICTION STRATEGY (documented here, spec-mandated because Google caps IP
       /// exclusions at ~cap per campaign): per campaign, live = currently pushed ip
       /// criteria (state table, ordered by PushedUtc ascending = oldest first).
       /// If live + newAdds > cap, evict (live + newAdds - cap) OLDEST live ip criteria
       /// to make room — newest fraud evidence always wins; an evicted IP that reoffends
       /// will be re-queued by a future verdict and pushed again. Placements are NOT
       /// capped/evicted. If newAdds alone exceed cap, push only the newest cap adds and
       /// report the remainder as failures ("ip exclusion cap exceeded").
       /// </summary>
       public static IReadOnlyList<CampaignPlan> Plan(
           IReadOnlyList<CriterionAdd> approvedAdds,
           IReadOnlyList<(string GoogleCampaignId, string ResourceName, string SourceType, DateTime PushedUtc)> live,
           int maxIpPerCampaign);
   }
   ```

6. **Worker** — `TelemetryGuard.Integrations/GoogleAds/GoogleAdsExclusionSyncService.cs`, a `BackgroundService`:

   Per cycle (`await timer.WaitForNextTickAsync` on a `PeriodicTimer(TimeSpan.FromMinutes(opts.SyncIntervalMinutes))`; also run once at startup after a 1-minute delay):

   1. `OpenSystemAsync` → `SELECT TenantId, GoogleAdsCustomerId FROM dbo.Tenants WHERE Status = 0 AND GoogleAdsCustomerId IS NOT NULL`.
   2. For each tenant (independent try/catch per tenant — one tenant's failure never skips the rest): `await using var conn = await systemFactory.OpenForTenantAsync(tenantId, ct);` then:
      a. Load approved google rows (`@Approved` is the string `ExclusionStatuses.Approved` — `Status` is `varchar(16)`, never a numeric parameter):
         ```sql
         SELECT TOP (@batch) Id, SourceType, Value, CampaignScope
         FROM dbo.ExclusionQueue
         WHERE TenantId = @tid AND Platform = 'google' AND Status = @Approved
         ORDER BY CreatedUtc
         ```
      b. Resolve Google campaign ids: rows with `CampaignScope` set → `SELECT ExternalCampaignId FROM dbo.Campaigns WHERE TenantId=@tid AND CampaignId=@scope AND Platform='google'`; rows with `CampaignScope IS NULL` fan out to ALL of the tenant's active google campaigns having `ExternalCampaignId IS NOT NULL` (IP exclusions are per-campaign in Google Ads — there is no account-level IP exclusion mutate; document this in a comment). Rows whose campaign has no `ExternalCampaignId` → mark `'failed'` with `LastError='campaign has no ExternalCampaignId'`.
      c. Load live state: `SELECT GoogleCampaignId, CriterionResourceName, SourceType, PushedUtc FROM dbo.GoogleAdsPushedExclusions WHERE TenantId=@tid`. Skip adds already live for that campaign+value (idempotency): mark the queue row `'pushed'` without calling Google.
      d. `SyncPlanner.Plan(...)` → per-campaign adds + evictions.
      e. **Dry-run gate**: if `opts.EffectiveDryRun` → log the full plan at Information (`"[DRY-RUN] tenant {Tid} customer {Cid}: +{Adds} ips/placements, -{Evictions} evictions across {Campaigns} campaigns"`) and `continue` — NO Google call, NO status change.
      f. Chunk into `MutateBatchSize` operations → `gateway.MutateAsync(customerId, adds, removes, ct)`.
      g. Persist outcomes in one transaction on the tenant connection:
         - each Created add → `INSERT dbo.GoogleAdsPushedExclusions (...)` and `UPDATE dbo.ExclusionQueue SET Status=@Pushed, PushedUtc=@now, UpdatedUtc=@now, LastError=NULL WHERE TenantId=@tid AND Id=@id AND Status=@Approved` (all status parameters are `ExclusionStatuses` strings; a fan-out row is `'pushed'` when ALL its target campaigns succeeded; partially → `'failed'` with the per-campaign errors concatenated);
         - each failed add → `UPDATE ... SET Status=@Failed, UpdatedUtc=@now, LastError=LEFT(@err, 2000)`;
         - each executed eviction → `DELETE FROM dbo.GoogleAdsPushedExclusions WHERE TenantId=@tid AND CriterionResourceName=@rn`.
   3. Emit OTel counters (API-01 conventions): `tg.googleads.pushed`, `tg.googleads.failed`, `tg.googleads.evicted`, tagged `tenant_id`.

7. **DI extension** — `TelemetryGuard.Integrations/GoogleAds/GoogleAdsServiceCollectionExtensions.cs`:

   ```csharp
   public static IServiceCollection AddGoogleAdsExclusionSync(
       this IServiceCollection services, IConfiguration configuration)
   {
       services.Configure<GoogleAdsOptions>(configuration.GetSection(GoogleAdsOptions.SectionName));
       services.AddSingleton<IGoogleAdsGateway, GoogleAdsGateway>();
       services.AddHostedService<GoogleAdsExclusionSyncService>();
       return services;
   }
   ```
   Register `builder.Services.AddGoogleAdsExclusionSync(builder.Configuration);` in `TelemetryGuard.Api/Program.cs`. (One service hosts everything per D3; a separate worker process is a later, measured decision.)

## Files to create or modify

- `TelemetryGuard.Integrations/TelemetryGuard.Integrations.csproj` (add `Google.Ads.GoogleAds`)
- `TelemetryGuard.Data/migrations/NNNN_google_ads_sync.sql` (new)
- `TelemetryGuard.Data/migrations/README.md` (registry row)
- `TelemetryGuard.Integrations/GoogleAds/GoogleAdsOptions.cs`
- `TelemetryGuard.Integrations/GoogleAds/IGoogleAdsGateway.cs`
- `TelemetryGuard.Integrations/GoogleAds/GoogleAdsGateway.cs`
- `TelemetryGuard.Integrations/GoogleAds/SyncPlanner.cs`
- `TelemetryGuard.Integrations/GoogleAds/GoogleAdsExclusionSyncService.cs`
- `TelemetryGuard.Integrations/GoogleAds/GoogleAdsServiceCollectionExtensions.cs`
- `TelemetryGuard.Api/Program.cs` (registration line)
- `TelemetryGuard.Api/appsettings.json` (`GoogleAds` section)
- `tests/TelemetryGuard.Tests.Unit/Integrations/SyncPlannerTests.cs`
- `tests/TelemetryGuard.Tests.Unit/Integrations/GoogleAdsSyncServiceTests.cs`

## Acceptance criteria

- `dotnet build TelemetryGuard.sln` succeeds; migration applies cleanly and is journaled; RLS blocks cross-tenant/unstamped access to `dbo.GoogleAdsPushedExclusions`.
- **Dry-run safety**: with default appsettings (empty credentials, `DryRun: true`) the service starts, logs `[DRY-RUN]` plans for seeded approved rows, makes zero HTTP calls (fake gateway asserts zero invocations), and leaves every queue row `Status='approved'`. Setting `DryRun: false` with empty credentials STILL dry-runs (`EffectiveDryRun`).
- With a fake gateway returning success: approved `ip` rows transition to `'pushed'` (with `PushedUtc` set), state rows appear in `dbo.GoogleAdsPushedExclusions` with correct resource names; approved `placement` rows likewise; `'pending'`/`'rejected'`/`'failed'` rows are never selected (SQL filter proves it).
- With a fake gateway returning a partial failure: exactly the failed adds' queue rows become `'failed'` with non-empty `LastError` (truncated ≤ 2000 chars); successful ones still become `'pushed'` — one bad row never blocks the batch.
- **LRU cap tests** (`SyncPlannerTests`): cap 500, 490 live ips, 30 new adds → plan evicts the 20 oldest live and adds all 30; cap 5, 0 live, 8 adds → newest 5 added, 3 reported as cap-exceeded failures; placements never evicted; eviction order strictly by `PushedUtc` ascending.
- Re-running a cycle after success is a no-op (idempotency via the state table — no duplicate Google calls, no duplicate state rows).
- A tenant with `GoogleAdsCustomerId IS NULL` is skipped entirely; an exception for tenant A (gateway throws) is logged and tenant B still syncs in the same cycle.
- Challenge-band invariant: the worker reads only `dbo.ExclusionQueue` rows (which API-06 creates exclusively for block-band verdicts) — `grep -n "Challenge" TelemetryGuard.Integrations/GoogleAds/*.cs` returns nothing.

## Testing

- **Unit — `SyncPlannerTests`**: pure-function coverage of the LRU strategy (cases above, plus: adds already live are absent from plans; fan-out to multiple campaigns; empty inputs).
- **Unit — `GoogleAdsSyncServiceTests`**: run one cycle of the worker's per-tenant body (extract it as an internal method `SyncTenantAsync(...)` taking the connection + gateway so tests can drive it) against a Testcontainers SQL Server with migrations applied (reuse the DAT-08 harness fixture) and a scripted fake `IGoogleAdsGateway`: success path, partial failure, dry-run inertness, idempotent re-run, missing `ExternalCampaignId` → `'failed'`.
- **No live Google Ads calls in any test or CI job.** The real `GoogleAdsGateway` is exercised only manually against a Google Ads **test account** (developer-token test access) — document the manual smoke procedure in a `<remarks>` comment on the gateway.

## Out of scope / guardrails

- **Only `approved → pushed | failed`** transitions. Never push `pending` rows (that would nullify D21's ApprovalQueue mode); never touch `rejected`/`unsupported`; never transition anything back to `pending`. Re-queueing failed rows is a manual operation, not code.
- **Challenge band is untouchable** (D21): this worker enforces block-band exclusions only; nothing here may create, gate, or modify challenge behavior. Rules/enforcement only ever ADD restrictions — nothing here un-blocks or lowers any score.
- **Dry-run is the default posture**: any code path that could reach Google with `EffectiveDryRun == true` is a critical bug. Never log or persist OAuth secrets/developer token.
- **Meta is INT-04** — do not touch `Platform='meta'` rows or add Meta code here.
- **Dapper on stamped connections only** (D9/D11): system sentinel ONLY for the tenant enumeration; all queue/state work on `OpenForTenantAsync` connections with explicit `TenantId` predicates; no EF Core; no raw `SqlConnection`. New table is under `rls.TenantIsolationPolicy` (step 2).
- **Not in the request path**: this is a background worker; it must not add latency to any endpoint (<50 ms scoring budget is unaffected) and must not query ClickHouse (verdict data reaches it only via the SQL queue — D23 split; no generic cross-engine query layer, D7).
- **No broker** (D12): the SQL queue + 15-minute poll is the design; do not introduce Kafka/queues.
- No server-side Python/Node (D1). Never edit applied migrations.
