---
id: API-07
title: Admin and whitelist API
phase: 1
workstream: api
depends_on: [API-01, DAT-07, DAT-05, DAT-06, ANA-01]
size: M
spec_refs: [D19, D22, D23, D11, "§10 Phase 1 'manual whitelist API'"]
detail_level: full
---

# API-07: Admin and whitelist API

## Objective

Implement the `/admin` route group (authenticated by API key with admin scope): whitelist CRUD backed by DAT-07's repository with Redis hot-path cache invalidation and the D19 review-loop side effect (whitelisting with `source=review_screen` also writes a *negative* training label), read-only summary reports from the DAT-06 aggregate tables, and a per-site integration-status report exposing effective JS-vs-pixel detection level per D22. All errors are RFC 7807 `application/problem+json`.

## Spec context (self-contained)

- **D19 (human oversight)**: decisions are automatic, but tenant override is always possible. Until the Phase-2 portal, a **manual whitelist API** covers the override path. Marking "this was a real customer" (source `review_screen`) both whitelists the source AND writes a negative label to the training store — the override loop doubles as the labeling loop.
- **D22 (integration level)**: dashboards must display each site's integration level so tenants understand their detection strength — full JS (beacons flowing) vs HTTP-only (pixel or nothing; SDK features NaN). Effective level is *observed*, not configured: a site whose last beacon is recent is "js"; otherwise "http-only".
- **D23**: admin reports read the small, RLS-protected SQL summary tables (populated by API-06/ANA-07) — NEVER the ClickHouse event store.
- **D11 (tenancy)**: tenant comes from `X-Api-Key` via DAT-04's middleware; SQL through tenant-stamped connections (RLS on `SESSION_CONTEXT('TenantId')`); Redis keys `t:{tid}:…`. Site keys resolve tenants for traffic endpoints but must NOT grant admin access.
- Whitelist consumers (the rule/scoring hot path) cache entries in Redis; every mutation here must invalidate that cache so overrides take effect immediately.

## Prerequisites

- **API-01**: host + middleware (`X-Api-Key` resolution via DAT-04), RFC 7807 via `AddProblemDetails`, registration point `// app.MapAdminEndpoints();`.
- **DAT-04 (transitive)**: `TenantResolutionMiddleware` only attempts site-key resolution on `/c` and `/p.gif`; any request that reaches `/admin/*` handlers was necessarily resolved via `X-Api-Key` (unresolved admin routes get 401 problem+json from the middleware). The resolver's `ResolvedTenant` carries `Scopes` from `dbo.ApiKeys`, but the middleware does not currently copy them into ambient state — check the repo in case that changed; otherwise the scope check stays a documented TODO (step 1).
- **DAT-07 (authoritative — these are DAT-07's ACTUAL types in `TelemetryGuard.Data`; consume verbatim, do not adapt or invent)**:
  ```csharp
  public sealed record WhitelistEntry(
      long Id, Guid TenantId, string SourceType /* "ip" | "device_id" | "fingerprint" */,
      string Value, string? Reason, string Source /* "manual" | "review_screen" */,
      string? CreatedBy, DateTime CreatedUtc, DateTime? ExpiresUtc);

  public sealed record NewWhitelistEntry(
      string SourceType, string Value, string? Reason, string Source,
      string? CreatedBy, DateTime? ExpiresUtc, string? SessionId = null);

  public interface IWhitelistRepository
  {
      Task<long> AddAsync(NewWhitelistEntry entry, CancellationToken ct);          // upsert; REBUILDS the Redis cache set AND emits the D19 negative label itself when Source=='review_screen' && SessionId != null
      Task<bool> RemoveAsync(string sourceType, string value, CancellationToken ct);
      Task<WhitelistEntry?> GetByIdAsync(long id, CancellationToken ct);
      Task<bool> RemoveByIdAsync(long id, CancellationToken ct);                   // rebuilds the cache set for the removed entry's SourceType
      Task<IReadOnlyList<WhitelistEntry>> ListAsync(string? sourceType, int offset, int limit, CancellationToken ct);
      Task<IReadOnlyDictionary<string, bool>> AreWhitelistedAsync(string sourceType, IReadOnlyCollection<string> values, CancellationToken ct);
      Task RebuildCacheAsync(string sourceType, CancellationToken ct);
  }
  ```
  The Redis hot-path cache set is `t:{tid}:wl:{sourceType}` and is rebuilt by the repository on every write — **this API performs no direct Redis writes and no label emission of its own** (both are `AddAsync`/`Remove*` side effects; double-emitting the label was an earlier-draft bug).
- **DAT-06 (read side)**: `IVerdictSummaryRepository` (`TelemetryGuard.Data.Repositories`) already ships the read this task needs:
  ```csharp
  Task<IReadOnlyList<VerdictDailySummaryRow>> GetDailySummariesAsync(
      Guid campaignId, DateOnly from, DateOnly to, CancellationToken ct);   // inclusive range
  // VerdictDailySummaryRow(Guid TenantId, Guid CampaignId, DateOnly Date,
  //     int Allowed, int Challenged, int Blocked, long ScoreSum, int Events)
  ```
  Note the read is **per campaign** (campaign ids are Guids); `Guid.Empty` is the no-campaign bucket (API-06's sentinel).
- **DAT-05 (declared dependency — it is NOT transitively reachable via API-01/DAT-07, hence listed in depends_on)**: `ISiteRepository` (`dbo.Sites(TenantId, SiteKey, Domain, IntegrationMode, CreatedUtc)`); it has `GetBySiteKeyAsync` — if it lacks a list method, ADD `Task<IReadOnlyList<SiteRecord>> ListAsync(CancellationToken ct)` (Dapper `SELECT ... WHERE TenantId = @TenantId ORDER BY SiteKey`, same file/pattern).
- **API-04**: sets `t:{tid}:site:{siteKey}:lastbeacon` = unix seconds (EX 7 days) on every accepted beacon.
- **ANA-01 (label contract — already exists in `TelemetryGuard.Analytics.Abstractions`)**:
  ```csharp
  public interface ILabelSink { ValueTask WriteAsync(LabelEvent label, CancellationToken ct); }
  public sealed record LabelEvent(TenantId TenantId, string SessionId, string Label, string LabelSource, DateTime CreatedAtUtc);
  // constants: LabelValues.Fraud/Legit, LabelSources.T1Rule/SyntheticBot/Conversion/ReviewScreen
  ```
  Registered by the analytics provider switch (ANA-05, `ClickHouseLabelSink` → ClickHouse `tg_labels`). If no `ILabelSink` registration exists yet, register a temporary no-op `LoggingLabelSink` with `// TODO(ANA-05): replaced by provider registration`. Do NOT invent a second label interface.

## Implementation steps

1. **Admin scope filter** — `TelemetryGuard.Api/Auth/AdminScopeFilter.cs`, an `IEndpointFilter`:
   - Defense-in-depth 403 problem (`type: "https://telemetryguard.dev/errors/admin-scope-required"`) when the tenant context is unresolved — by DAT-04's construction site keys never resolve `/admin/*` (only `/c` and `/p.gif`), and unresolved admin requests were already 401'd by the middleware, so this is a backstop, not the primary gate.
   - If DAT-04's middleware exposes the API key's `Scopes` (from `ResolvedTenant`) into ambient state by the time you build this, require the `admin` scope; if it does not, accept any API-key-authenticated request and leave `// TODO(DAT-04): enforce 'admin' scope once scopes are exposed`.

2. **Create `TelemetryGuard.Api/Endpoints/AdminEndpoints.cs`**:
   ```csharp
   public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
   {
       var admin = app.MapGroup("/admin").AddEndpointFilter<AdminScopeFilter>();
       admin.MapGet   ("/whitelist",              ListWhitelistAsync);
       admin.MapPost  ("/whitelist",              AddWhitelistAsync);
       admin.MapDelete("/whitelist/{id:long}",    DeleteWhitelistAsync);
       admin.MapGet   ("/reports/summary",        GetSummaryAsync);
       admin.MapGet   ("/sites/integration-status", GetIntegrationStatusAsync);
       return app;
   }
   ```
   Uncomment `app.MapAdminEndpoints();` in `Program.cs`.

3. **Whitelist endpoints**:
   - `GET /admin/whitelist?type=ip&offset=0&limit=50` → `200` JSON array of entries (limit clamped to 200) via `ListAsync(type, offset, limit, ct)`.
   - `POST /admin/whitelist` body:
     ```json
     { "type": "ip", "value": "203.0.113.7", "reason": "confirmed real customer",
       "source": "manual", "sessionId": "9f8e7d6c5b4a39281706f5e4d3c2b1a0" }
     ```
     Validation (400 `ValidationProblem` on failure): `type` ∈ DAT-07's CHECK set — exactly `ip` | `device_id` | `fingerprint` (there is no `cidr` or `ua` type; the table's CHECK constraint would reject them); `value` non-empty (parse as a plain IP via `IPAddress.TryParse` when `type=="ip"`); `source` ∈ {`manual`, `review_screen`}; `sessionId` REQUIRED (matching `^[A-Za-z0-9_-]{8,64}$` — same sid pattern as API-04/API-05) when `source == "review_screen"`, optional otherwise.
     Effect — ONE call: `var id = await whitelist.AddAsync(new NewWhitelistEntry(type, value, reason, source, createdBy: /* api-key identifier if available, else null */ null, ExpiresUtc: null, SessionId: sessionId), ct);`. DAT-07's `AddAsync` already rebuilds the Redis cache set AND emits the D19 negative `LabelEvent` (Legit/ReviewScreen) when `Source=='review_screen'` with a `SessionId` — **do NOT call `ILabelSink` from this endpoint and do NOT issue any Redis `DEL`** (that would double-write the label and race the repository's rebuild). Response `201` with `{ "id": <id> }` and `Location: /admin/whitelist/{id}`.
   - `DELETE /admin/whitelist/{id:long}` → `GetByIdAsync(id)` first: null ⇒ `404` problem (RLS makes cross-tenant ids look nonexistent — exactly right); else `RemoveByIdAsync(id)` → `204` (the repository rebuilds the cache set for that entry's `SourceType` itself — no endpoint-side invalidation).

4. **Summary report** — `GET /admin/reports/summary?campaignId=<guid>&from=2026-08-01&to=2026-08-12`:
   - Validate: `campaignId` REQUIRED, `Guid.TryParse`-able (`00000000-...-0000` = the no-campaign bucket, allowed); `from <= to`, span ≤ 366 days (400 `ValidationProblem` otherwise). Dates are `DateOnly` ISO `yyyy-MM-dd`.
   - Read via `IVerdictSummaryRepository.GetDailySummariesAsync(campaignId, from, to, ct)` (tenant-stamped connection). Response `200`:
     ```json
     { "from": "2026-08-01", "to": "2026-08-12", "campaignId": "8b7c...-guid",
       "rows": [ { "date": "2026-08-01", "events": 120,
                   "allowed": 90, "challenged": 20, "blocked": 10, "avgScore": 27.4 } ] }
     ```
     `avgScore` = `ScoreSum / Events` rounded to 1 decimal (null when `Events == 0` — missing, not zero).

5. **Integration status** — `GET /admin/sites/integration-status`:
   - List the tenant's sites via `ISiteRepository` (`SiteRecord` includes the CONFIGURED `IntegrationMode` from `dbo.Sites`); for each, `GET t:{tid}:site:{siteKey}:lastbeacon`.
   - Observed effective level: `"js"` when a beacon was seen within the last 24 h; `"http-only"` otherwise (pixel-mode and not-yet-integrated sites are indistinguishable server-side by beacons alone — that is the honest signal D22 asks to surface). Reporting configured vs observed side by side is exactly the "integration level" display D22 requires. Response `200`:
     ```json
     { "sites": [ { "siteKey": "site_abc123", "domain": "landing-a.example",
                    "configuredMode": "js", "lastBeaconAt": "2026-08-12T09:15:22Z",
                    "effectiveLevel": "js" },
                  { "siteKey": "site_def456", "domain": "landing-b.example",
                    "configuredMode": "pixel", "lastBeaconAt": null,
                    "effectiveLevel": "http-only" } ] }
     ```
     (`configuredMode` = the `dbo.Sites.IntegrationMode` value mapped to a string; use DAT-02's encoding.)

6. **Errors**: every non-2xx from these handlers is `Results.Problem(...)`/`Results.ValidationProblem(...)` — RFC 7807 with `status`, `title`, `type`; never leak SQL or stack detail.

7. **DTOs** in `TelemetryGuard.Api/Endpoints/AdminModels.cs` (`AddWhitelistRequest`, response records). System.Text.Json camelCase (default web serializer).

## Files to create or modify

- `TelemetryGuard.Api/Auth/AdminScopeFilter.cs`
- `TelemetryGuard.Api/Endpoints/AdminEndpoints.cs`
- `TelemetryGuard.Api/Endpoints/AdminModels.cs`
- `TelemetryGuard.Api/Services/LoggingLabelSink.cs` (only if no `ILabelSink` registration exists yet — ANA-05 owns the real one)
- `TelemetryGuard.Data/Repositories/SiteRepository.cs` (+ its interface file — add `ListAsync` only if DAT-05 didn't ship one)
- `TelemetryGuard.Api/Program.cs` (`app.MapAdminEndpoints();` + label-sink registration if stubbing)
- `tests/TelemetryGuard.Tests.Unit/Api/AdminEndpointTests.cs`

## Acceptance criteria

- A request to `/admin/*` carrying only `?k=<siteKey>` (no `X-Api-Key`) → `401 application/problem+json` from DAT-04's middleware (site keys never resolve admin routes); with a valid `X-Api-Key` the filter passes (and, when scopes are exposed, only with the `admin` scope); the filter's own 403 backstop fires when handed an unresolved context in isolation-tests.
- `POST /admin/whitelist` with `source=manual` → 201 + `Location` header; `AddAsync` received a `NewWhitelistEntry` with `Source="manual"` and `SessionId=null`; the ENDPOINT issued no Redis command and no `ILabelSink` call (grep `AdminEndpoints.cs`: no `IConnectionMultiplexer`/`ILabelSink` usage in the whitelist handlers — cache rebuild and labeling are DAT-07 repository side effects).
- `POST` with `source=review_screen` + `sessionId` → 201 AND `AddAsync` received that `SessionId` (label emission is the repository's job — with the real DAT-07 repository and a fake `ILabelSink`, exactly ONE `LabelEvent(Label=LabelValues.Legit, LabelSource=LabelSources.ReviewScreen)` total, never two); with `source=review_screen` and NO `sessionId` → 400, nothing written.
- Invalid type (`cidr` and `ua` are NOT valid — only `ip`/`device_id`/`fingerprint`) / empty value / bad IP for `type=ip` → 400 `ValidationProblem`.
- `DELETE` existing id → 204 via `GetByIdAsync` + `RemoveByIdAsync`; unknown id → 404 problem and no `RemoveByIdAsync` call.
- `GET /admin/reports/summary` returns rows only from `IVerdictSummaryRepository.GetDailySummariesAsync` (no ClickHouse/`IAnalyticsQueries` reference anywhere in `AdminEndpoints.cs` — grep-checkable) with `avgScore = ScoreSum/Events` math; missing/invalid `campaignId` or `from > to` → 400.
- `GET /admin/sites/integration-status`: site with `lastbeacon` 1 h ago → `effectiveLevel:"js"`; 3 days ago or absent → `"http-only"`; `lastBeaconAt` null when key absent; `configuredMode` reflects `dbo.Sites.IntegrationMode`.
- All error responses are `application/problem+json`.

## Testing

- `WebApplicationFactory<Program>` with fakes: `IWhitelistRepository` (capture — assert the exact `NewWhitelistEntry` passed, incl. `SessionId`; assert the endpoint performs no Redis/label calls of its own), summary repo (scripted rows), site repo (scripted sites), Redis fake or Testcontainer (seeded `lastbeacon` values). One test with the REAL DAT-07 repository + fake `ILabelSink` proving single label emission. A test `ITenantContext` configured once as site-key-resolved (403 path) and once as API-key-resolved.
- Cover every acceptance criterion; include serialization-shape assertions on raw JSON (camelCase field names as shown in the contracts above).

## Out of scope / guardrails

- **Never query ClickHouse / `IAnalyticsQueries` from admin endpoints** — D23: portals/APIs read RLS-protected SQL aggregates only. No generic cross-engine query layer (D7).
- **Dapper only, tenant-stamped connections only** — all reads/writes via DAT-05/06/07 repositories through `TenantConnectionFactory`; RLS (`SESSION_CONTEXT('TenantId')`) is primary isolation; keep explicit `TenantId` predicates in any SQL you add. No EF Core. `TenantId` never optional — there is no "list all tenants" admin surface here (that would be a platform-operator concern, not a tenant API).
- **Whitelisting must not touch scores retroactively** — no re-scoring, no verdict rewriting, no exclusion-queue manipulation here (INT-02 owns approval-queue actions; P2-03 owns the review screen UX). Rules only raise scores; a whitelist prevents FUTURE flags via the hot path's whitelist check (RSK-05/RSK-07's concern).
- **The negative label is the only training side effect** (D19); do not build label review/training tooling (RSK-08/P2-02).
- No customer portal, no UI, no Grafana provisioning (OPS-01/P2-03). No pagination cursors, ETags, or admin user management — API-key scope only at MVP.
- No server-side Python/Node; Redis keys strictly `t:{tid}:…`.
