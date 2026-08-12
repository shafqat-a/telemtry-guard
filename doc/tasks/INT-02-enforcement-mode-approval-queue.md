---
id: INT-02
title: EnforcementMode approval queue flow
phase: 1.5
workstream: integrations
depends_on: [DAT-06, API-06]
size: M
spec_refs: [D21, D19, D11, "§6.3"]
detail_level: full
---

# INT-02: EnforcementMode approval queue flow

## Objective

Implement the tenant-facing half of spec D21: `/admin/enforcement` endpoints to list pending exclusion-queue entries and approve or reject them in audited batches, plus the repository methods and the `EnforcementAudit` table behind them. API-06 already writes `ExclusionQueue` rows with a status chosen by the tenant's `EnforcementMode` (`AutoEnforce` → approved immediately, `ApprovalQueue` → pending); this task completes the `pending → approved | rejected` transition path (the sync workers INT-03/INT-04 then take `approved → pushed | failed`), and removes the "MVP TODO" marker API-06 left for this flow.

## Spec context (self-contained)

- **D21 — Enforcement autonomy is a per-tenant setting.** `dbo.Tenants.EnforcementMode tinyint`: `0 = AutoEnforce` (default) — block-band actions and exclusion-list pushes execute automatically; `1 = ApprovalQueue` — those actions queue for tenant approval instead. **The 31–70 challenge band is ALWAYS automatic in both modes** (a Turnstile prompt is low-harm) — nothing in this task may queue, gate, or delay challenge actions.
- **D19 — automatic decisions, tenant override always possible.** The approval queue is one of the override surfaces; until the portal (P2-03) ships, these admin API endpoints ARE the override path for exclusions.
- **Status lifecycle** (full picture, this task owns the bolded edges):
  `pending` →(**approve**)→ `approved` →(INT-03/INT-04 push)→ `pushed` | `failed`; `pending` →(**reject**)→ `rejected` (terminal); `unsupported` (terminal, set by INT-04 for Meta ip rows). AutoEnforce tenants' rows are born `approved` (API-06) and never appear in the pending list.
- **D11 — tenancy**: every table row carries `TenantId`; RLS keyed on `SESSION_CONTEXT(N'TenantId')` is the primary isolation (policy `rls.TenantIsolationPolicy`, DAT-03); repositories use tenant-stamped connections from `ITenantConnectionFactory` only, and still write explicit `WHERE TenantId = @tid` for index seeks. Any migration creating a tenant-scoped table MUST add FILTER + BLOCK predicates for it to `rls.TenantIsolationPolicy` in the same migration (DAT-03 rule). `TenantId` is never optional.
- **Dapper only, no EF Core** (D9); migrations are numbered DbUp SQL scripts (D10).
- Admin/API traffic authenticates via `X-Api-Key` header; DAT-04's resolver returns the key's space-separated scopes (e.g. `admin ingest report`). Enforcement endpoints require the `admin` scope.

## Prerequisites

Read these task files in `doc/tasks/` and the code they produced — their names are authoritative where this file says "expected":

- **DAT-06** (migration `0003_summaries_exclusions.sql`, authoritative — read it): `dbo.ExclusionQueue`'s ACTUAL columns are `TenantId uniqueidentifier NOT NULL`, `Id bigint IDENTITY(1,1) NOT NULL` (PK `(TenantId, Id)`), `SourceType varchar(16)` (`'ip'|'placement'`), `Value varchar(256)`, `Reason nvarchar(400)`, `Status varchar(16)` (default `'pending'`, `CONSTRAINT CK_EQ_Status CHECK (Status IN ('pending','approved','pushed','failed','unsupported'))`), `CampaignScope uniqueidentifier NULL` (NULL = tenant-wide), `CreatedUtc datetime2(3)`, `PushedUtc datetime2(3) NULL`; RLS-covered. Note what does NOT exist: no `Platform`, `UpdatedUtc`, or `LastError` columns (this task's migration adds `UpdatedUtc`; INT-03/INT-04's migrations add `Platform`/`LastError`), no GUID `ExclusionId` (the key is the bigint `Id`), no C# status type, and **`'rejected'` is missing from the CHECK — this task's migration extends it (as strings, step 1)**. DAT-06 deliberately shipped no ExclusionQueue repository ("no repository for `ExclusionQueue` in this task"), so this task creates the first one.
- **API-06**: verdict finalizer that inserts `ExclusionQueue` rows for block-band verdicts, choosing status by `EnforcementMode`, containing a `TODO` comment referencing INT-02. Find it: `grep -rn "INT-02" TelemetryGuard.Api/ TelemetryGuard.Data/`.
- **DAT-03**: `ITenantConnectionFactory` (tenant-stamped connections), `rls.TenantIsolationPolicy`, migration registry at `TelemetryGuard.Data/migrations/README.md`.
- **API-01/API-07**: the Api host with `/admin/*` endpoint group and API-07's admin-scope authorization helper (an endpoint filter / extension asserting the resolved API key carries the `admin` scope — check API-07's task file for its exact name and reuse it; only if none exists, implement a local endpoint filter reading the request's `X-Api-Key`-derived scopes the same way API-07 does).
- **FND-04**: `ITenantContext` (namespace `TelemetryGuard.Core.Tenancy`), `TenantId` record struct, `IClock`.

## Implementation steps

1. **Migration** — create `TelemetryGuard.Data/migrations/NNNN_enforcement_audit.sql` where `NNNN` is the next free number in `TelemetryGuard.Data/migrations/README.md` (expected `0005`; DAT-02=0001, DAT-03=0002, DAT-06/DAT-07 hold 0003/0004). Content:

   ```sql
   ------------------------------------------------------------------------------
   -- NNNN_enforcement_audit.sql  (INT-02)
   -- Audit trail for approval-queue decisions (spec D21/D19). Tenant-scoped: RLS
   -- predicates added below per the DAT-03 rule.
   -- EnforcementAudit.Action: 0=approve, 1=reject
   ------------------------------------------------------------------------------
   CREATE TABLE dbo.EnforcementAudit
   (
       TenantId         uniqueidentifier NOT NULL,
       AuditId          uniqueidentifier NOT NULL,
       ExclusionQueueId bigint           NOT NULL,   -- originating dbo.ExclusionQueue.Id
       Action           tinyint          NOT NULL,
       ActorKeyHash binary(32)       NULL,       -- SHA-256 of the X-Api-Key that acted; NULL = system
       Note         nvarchar(400)    NULL,
       CreatedUtc   datetime2(3)     NOT NULL CONSTRAINT DF_EnforcementAudit_CreatedUtc DEFAULT (SYSUTCDATETIME()),
       CONSTRAINT PK_EnforcementAudit PRIMARY KEY CLUSTERED (TenantId, AuditId),
       CONSTRAINT CK_EnforcementAudit_Action CHECK (Action IN (0, 1))
   );
   GO
   CREATE NONCLUSTERED INDEX IX_EnforcementAudit_Exclusion
       ON dbo.EnforcementAudit (TenantId, ExclusionQueueId);
   GO
   ALTER SECURITY POLICY rls.TenantIsolationPolicy
       ADD FILTER PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.EnforcementAudit,
       ADD BLOCK  PREDICATE rls.fn_tenantPredicate(TenantId) ON dbo.EnforcementAudit;
   GO

   -- DAT-06's queue lacks 'rejected' in its Status CHECK and has no UpdatedUtc column;
   -- both are required by the approval flow. Extend here (never edit the applied 0003
   -- script). Statuses are STRINGS — the column is varchar(16), not a tinyint.
   ALTER TABLE dbo.ExclusionQueue DROP CONSTRAINT CK_EQ_Status;
   ALTER TABLE dbo.ExclusionQueue ADD CONSTRAINT CK_EQ_Status
       CHECK (Status IN ('pending', 'approved', 'rejected', 'pushed', 'failed', 'unsupported'));
   GO
   ALTER TABLE dbo.ExclusionQueue ADD UpdatedUtc datetime2(3) NULL;
   GO
   ```
   Append the registry row to `migrations/README.md`: `| NNNN | EnforcementAudit + ExclusionQueue 'rejected' CHECK extension + UpdatedUtc | INT-02 |`.

2. **Status constants** — DAT-06 created no C# status type and the column is `varchar(16)`, so create `TelemetryGuard.Data/Exclusions/ExclusionStatuses.cs` (string constants, NOT an enum — Dapper parameters must compare as strings against the varchar column):

   ```csharp
   namespace TelemetryGuard.Data.Exclusions;

   /// <summary>Mirrors the ExclusionQueue.Status varchar(16) values (DAT-06's CHECK,
   /// extended with 'rejected' by this task's migration).</summary>
   public static class ExclusionStatuses
   {
       public const string Pending = "pending";           // ApprovalQueue tenants: awaiting tenant approval (this task)
       public const string Approved = "approved";         // eligible for platform push (INT-03/INT-04)
       public const string Rejected = "rejected";         // terminal: tenant declined (this task)
       public const string Pushed = "pushed";             // terminal: platform accepted (sync workers)
       public const string Failed = "failed";             // push errored; LastError (INT-03 adds it) holds detail
       public const string Unsupported = "unsupported";   // terminal: platform cannot enforce this source type (INT-04 Meta ip rows)
   }
   ```

3. **Repository** — create `TelemetryGuard.Data/Exclusions/IEnforcementQueueRepository.cs` + implementation (or extend DAT-06's exclusion repository with these members if one exists; keep the signatures):

   ```csharp
   using TelemetryGuard.Core.Tenancy;

   namespace TelemetryGuard.Data.Exclusions;

   public sealed record ExclusionQueueEntry(
       long Id, string SourceType, string Value,
       Guid? CampaignScope, string Reason, string Status,
       DateTime CreatedUtc, DateTime? UpdatedUtc);

   public interface IEnforcementQueueRepository
   {
       /// <summary>Entries for the current tenant filtered by status (an ExclusionStatuses
       /// value), newest first.</summary>
       Task<IReadOnlyList<ExclusionQueueEntry>> ListAsync(
           string status, int limit, CancellationToken ct);

       /// <summary>Transitions pending→approved for the given queue Ids and writes one audit
       /// row per transitioned entry, in ONE transaction. Ids not currently pending are
       /// skipped. Returns the number actually transitioned.</summary>
       Task<int> ApproveAsync(IReadOnlyList<long> ids, byte[]? actorKeyHash, CancellationToken ct);

       /// <summary>Transitions pending→rejected; same batching/audit/idempotency contract.</summary>
       Task<int> RejectAsync(IReadOnlyList<long> ids, string? note, byte[]? actorKeyHash, CancellationToken ct);
   }
   ```

   Implementation `EnforcementQueueRepository` (Dapper, tenant-stamped connection from `ITenantConnectionFactory`, ambient tenant from `ITenantContext`, time from `IClock`). Approve core (reject is identical with `ExclusionStatuses.Rejected` and the note):

   ```csharp
   await using var conn = await connectionFactory.OpenAsync(ct);
   await using var tx = await conn.BeginTransactionAsync(ct);

   var updated = (await conn.QueryAsync<long>(
       """
       UPDATE dbo.ExclusionQueue
       SET Status = @Approved, UpdatedUtc = @now
       OUTPUT inserted.Id
       WHERE TenantId = @tid AND Id IN @ids AND Status = @Pending
       """,
       new { Approved = ExclusionStatuses.Approved, Pending = ExclusionStatuses.Pending,
             now, tid, ids }, tx)).AsList();

   foreach (var id in updated)
       await conn.ExecuteAsync(
           """
           INSERT INTO dbo.EnforcementAudit (TenantId, AuditId, ExclusionQueueId, Action, ActorKeyHash, Note, CreatedUtc)
           VALUES (@tid, @auditId, @id, @action, @actor, @note, @now)
           """,
           new { tid, auditId = Guid.NewGuid(), id, action = (byte)0, actor = actorKeyHash, note = (string?)null, now }, tx);

   await tx.CommitAsync(ct);
   return updated.Count;
   ```
   Keep the explicit `TenantId = @tid` predicates (D11) even though RLS already scopes the connection. Adapt table/column names to DAT-06's actual migration if they differ.

4. **Register** the repository in `TelemetryGuard.Data/DataServiceCollectionExtensions.cs` (`services.TryAddScoped<IEnforcementQueueRepository, EnforcementQueueRepository>();`).

5. **Endpoints** — create `TelemetryGuard.Api/Endpoints/EnforcementAdminEndpoints.cs`:

   ```csharp
   public static class EnforcementAdminEndpoints
   {
       public static IEndpointRouteBuilder MapEnforcementAdminEndpoints(this IEndpointRouteBuilder app)
       {
           var group = app.MapGroup("/admin/enforcement");
           // Reuse API-07's admin-scope filter (check its actual name), e.g.:
           // group.AddEndpointFilter(new RequireScopeFilter("admin"));
           group.MapGet("/", ListAsync);          // ?status=pending&limit=100
           group.MapPost("/approve", ApproveAsync);
           group.MapPost("/reject", RejectAsync);
           return app;
       }
   }
   ```
   Register `app.MapEnforcementAdminEndpoints();` in `Program.cs` next to `app.MapAdminEndpoints();` (API-07).

   Behavior:
   - `GET /admin/enforcement?status=pending&limit=100` — `status` parses case-insensitively to one of the six `ExclusionStatuses` values (default `pending`; invalid → 400 problem+json); `limit` clamped to 1..500. Returns `200` JSON array of entries (camelCase: `id`, `sourceType`, `value`, `campaignScope`, `reason`, `status`, `createdUtc`, `updatedUtc`). `status=unsupported` and `status=failed` make INT-03/INT-04 outcomes visible here — this is the MVP reporting surface for them.
   - `POST /admin/enforcement/approve` body `{"ids": [123, ...]}` (the bigint queue `Id` values) — 1..500 ids, else 400. Returns `200 {"requested": n, "approved": m}` (m ≤ n; non-pending ids are skipped — idempotent replays are safe).
   - `POST /admin/enforcement/reject` body `{"ids": [...], "note": "optional, ≤400 chars"}` — same shape, returns `{"requested": n, "rejected": m}`.
   - Actor hash: `SHA256.HashData(Encoding.UTF8.GetBytes(apiKey))` from the `X-Api-Key` header value (same hashing DAT-04 uses for lookup); pass as `actorKeyHash`. Header absent (should not happen behind the admin filter) → pass null.
   - Errors are RFC 7807 `application/problem+json` (API-01 conventions).

6. **Remove the API-06 MVP TODO**: locate the `TODO(INT-02)` (or equivalent) comment in API-06's finalizer/enforcement code and replace it with a short pointer: `// Approval flow implemented by INT-02: /admin/enforcement endpoints transition pending→approved|rejected.` Verify while there that API-06 writes `'approved'` for `EnforcementMode=0` tenants and `'pending'` for `EnforcementMode=1` — that logic stays in API-06; do not duplicate or move it.

7. **Verify the challenge-band invariant**: `grep -rn "Challenge" TelemetryGuard.Api/Endpoints/EnforcementAdminEndpoints.cs TelemetryGuard.Data/Exclusions/` must show no code path that queues or gates challenge actions — only block-band exclusion entries flow through this queue (they are the only rows API-06 enqueues).

## Files to create or modify

- `TelemetryGuard.Data/migrations/NNNN_enforcement_audit.sql` (new; NNNN = next free number)
- `TelemetryGuard.Data/migrations/README.md` (registry row)
- `TelemetryGuard.Data/Exclusions/ExclusionStatuses.cs` (new — DAT-06 defined no C# status type)
- `TelemetryGuard.Data/Exclusions/IEnforcementQueueRepository.cs` (new)
- `TelemetryGuard.Data/Exclusions/EnforcementQueueRepository.cs` (new)
- `TelemetryGuard.Data/DataServiceCollectionExtensions.cs` (modify: registration)
- `TelemetryGuard.Api/Endpoints/EnforcementAdminEndpoints.cs` (new)
- `TelemetryGuard.Api/Program.cs` (modify: map endpoints)
- API-06's finalizer file (modify: remove the INT-02 TODO comment)
- `tests/TelemetryGuard.Tests.Unit/Api/EnforcementAdminEndpointTests.cs` (new)
- `tests/TelemetryGuard.Tests.Integration/...` (extend the DAT-08 harness with repository coverage — see Testing)

## Acceptance criteria

- `dotnet build TelemetryGuard.sln` succeeds; migration applies cleanly on a fresh database via `dotnet run --project TelemetryGuard.MigrationRunner` (journal shows the new script; second run is a no-op).
- RLS proof: on an unstamped session, `SELECT COUNT(*) FROM dbo.EnforcementAudit` returns 0 rows and inserts fail; stamped as tenant A, rows for tenant B are invisible.
- With a seeded `ApprovalQueue` tenant (`EnforcementMode=1`) and pending rows:
  - `GET /admin/enforcement?status=pending` with an admin-scoped `X-Api-Key` → 200 and the rows; with a non-admin key → 403 (or API-07's documented status); with no key → 401.
  - `POST /admin/enforcement/approve` with 2 pending ids + 1 already-approved id → `{"requested":3,"approved":2}`; the 2 rows are `Status='approved'` with fresh `UpdatedUtc`; exactly 2 `EnforcementAudit` rows exist with `Action=0` and a non-null `ActorKeyHash`.
  - Replaying the same approve request → `{"requested":3,"approved":0}` and no new audit rows.
  - `POST /admin/enforcement/reject` on a pending row → `Status='rejected'`, audit row `Action=1` with the note.
- With an `AutoEnforce` tenant (`EnforcementMode=0`): block-band verdicts produce rows born `Status='approved'` (API-06 behavior, asserted here as a regression guard) and `GET ...?status=pending` returns an empty array.
- 501 ids in a batch → 400 problem+json; invalid `status` value → 400.
- `grep -rn "TODO(INT-02)" --include=*.cs .` returns nothing.
- Approving/rejecting never touches any challenge-related code path or Redis key (review + the grep in step 7).

## Testing

- **Unit** (`EnforcementAdminEndpointTests.cs`, `WebApplicationFactory<Program>` with a fake `IEnforcementQueueRepository` and a test tenant context): auth matrix (no key / non-admin / admin), status parsing, limit clamping, batch-size validation, response shapes, idempotent-replay counts, actor hash passed through (capture on the fake).
- **Integration** (extend the DAT-08 Testcontainers harness pattern): run migrations, seed one tenant per mode plus pending/approved rows under the SYSTEM sentinel stamping pattern (DAT-03), then execute every repository method once: `ListAsync` per status, `ApproveAsync` mixed batch (assert count + audit rows + transaction atomicity by including a deliberately duplicated id), `RejectAsync` with note, and a cross-tenant check: tenant B's connection sees none of tenant A's entries.
- Both-modes coverage is mandatory (the task direction): one test seeds `AutoEnforce` and asserts the born-approved/empty-pending behavior; another seeds `ApprovalQueue` and exercises the full pending→approved and pending→rejected flows.

## Out of scope / guardrails

- **Challenge band is untouchable**: 31–70 challenge actions are always automatic in BOTH enforcement modes (D21). Nothing here may queue, delay, or approve challenges.
- **Do not push to ad platforms** — `approved → pushed|failed` belongs to INT-03 (Google) and INT-04 (Meta). This task ends at `approved`/`rejected`.
- **Do not re-decide verdicts**: approval/rejection here controls exclusion-list pushes only; it does not rescore or un-block past sessions (score changes only ever come from the scoring pipeline; rules only raise scores, never lower them).
- **Dapper on tenant-stamped connections only** (D9/D11): no EF Core; never open a raw `SqlConnection`; keep explicit `WHERE TenantId = @tid`; RLS (`SESSION_CONTEXT('TenantId')`) remains the primary enforcement and the new table MUST be in `rls.TenantIsolationPolicy` (step 1). `TenantId` is never optional — no cross-tenant admin listing.
- **No server-side Python/Node** (D1). No broker/queue infrastructure (D12) — the "queue" is a SQL table.
- Do not build UI — the review/override screen is P2-03; these JSON endpoints are its future backend.
- Do not modify API-06's mode-selection logic — only remove its TODO marker.
- Never edit an already-applied migration; the status-CHECK extension (if needed) happens in this task's NEW script.
