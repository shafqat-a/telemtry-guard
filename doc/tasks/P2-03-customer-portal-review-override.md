---
id: P2-03
title: Customer portal with review/override screens
phase: 2
workstream: portal
depends_on: [API-07, INT-02, DAT-05, ANA-07]
size: L
spec_refs: [D1, D17, D19, D21, D22, D23, "§11.5"]
detail_level: full
---

# P2-03: Customer portal with review/override screens

## Objective

Ship `TelemetryGuard.Portal` — a **server-rendered ASP.NET Core Razor Pages** app (no npm, no SPA framework, no JavaScript at all) that is a pure HTTP **client of the existing `/admin/*` API**. A tenant signs in by pasting an admin API key; the key is kept in an encrypted HttpOnly cookie and replayed as `X-Api-Key` on server-side calls to the API. Four screens: **dashboard** (per-campaign verdict summaries + exclusion-queue counts + D22 per-site integration level), **review/override** (top flagged sources → "this was a real customer" → whitelist add, optionally carrying a session id so API-07/DAT-07 emits the D19 negative label), **enforcement approval queue** (INT-02 list/approve/reject with `unsupported`/`failed` visibility), and **whitelist management** (list/add/delete).

Two small additive endpoints are added to the existing `/admin` group because the portal cannot function without them: `GET /admin/campaigns` (the summary report requires a `campaignId` and nothing exposes the campaign list) and `GET /admin/reports/flagged-sources` (`IVerdictSummaryRepository.GetTopFlaggedSourcesAsync` exists today with **no** HTTP caller). No new migration, no new table, no change to the API's `Program.cs`, middleware, auth, or `appsettings.json`.

## Spec context (self-contained)

- **D17 (the deferred decision, resolved here)** — "Customer-facing portal later: ASP.NET Core API + Blazor, or a static SPA… Deferred until productization demands it." **This task resolves it as: ASP.NET Core Razor Pages, server-rendered, zero client-side framework, zero JavaScript.** Rationale: D1 forbids server-side Node/Python; Blazor Server adds a stateful SignalR circuit per user for four CRUD screens; a SPA adds a second build chain (the D2 esbuild exception exists for the *tenant-page* SDK, not for our own admin UI) and would force CORS onto `/admin`. Razor Pages needs no toolchain, and Razor's `@` output encoding is XSS-safe by default for the attacker-controlled strings this UI renders (IPs, placement URLs, whitelist reasons). Record this as a numbered spec amendment (step 1).
- **D19 — human oversight**: every challenged/blocked event should be reviewable, and marking "real customer" both whitelists the source and writes a negative training label. **What the portal can actually honor, and what it cannot**: per-*event* review would require reading raw events, which **D23 forbids** (see below). The SQL side of the split carries per-source/per-day aggregates only (`dbo.FlaggedSourcesDaily`), so this portal reviews **flagged sources**, not individual events. The whitelist add is the override; the negative label is emitted **only** when the operator supplies a session id (see below). Per-event review is explicitly deferred (out of scope).
- **D19 label mechanics (already implemented — the portal writes no labels itself)**: `POST /admin/whitelist` with `source:"review_screen"` **requires** a `sessionId` matching `^[A-Za-z0-9_-]{8,64}$` (AdminEndpoints validation). DAT-07's `WhitelistRepository.AddAsync` then emits exactly one `LabelEvent(TenantId, SessionId, LabelValues.Legit, LabelSources.ReviewScreen, …)` through `ILabelSink` as its own side effect. `source:"manual"` emits none. The portal therefore exposes an **optional session-id field**: empty → `manual`; filled → `review_screen` (label emitted downstream). The portal must never reference `ILabelSink` — double-writing the label is the exact bug API-07's endpoint comments warn about.
- **D21 — enforcement autonomy**: `ApprovalQueue` tenants approve/reject exclusion pushes. The portal front-ends INT-02's endpoints only: `pending → approved | rejected`. The 31–70 challenge band is always automatic in both modes — **nothing in the portal may gate, delay or disable a challenge**. `approved → pushed | failed | unsupported` belongs to INT-03/INT-04's workers; the portal only displays those states.
- **D22 — integration level**: dashboards must display each site's integration level so tenants understand degraded detection on pixel-only sites. `GET /admin/sites/integration-status` already returns `siteKey, domain, configuredMode ('js'|'pixel' from dbo.Sites.IntegrationMode), lastBeaconAt, effectiveLevel ('js' when a beacon arrived in the last 24 h, else 'http-only')`.
- **D23 (hard)** — SQL Server holds summaries/breakdowns; ClickHouse holds raw events; "portals and APIs read small, fast, RLS-protected aggregate tables and never query the event store directly." Structurally guaranteed here: `TelemetryGuard.Portal.csproj` has **zero `<ProjectReference>` and zero `<PackageReference>`** — it cannot reach ClickHouse, SQL Server or Redis even by accident, and it has no connection string of any kind.
- **D7** — no generic cross-engine query layer. The portal's client is intent-named per screen; it never builds queries.
- **D11 — tenancy**: the portal introduces **no** tenancy path of its own. One API key = one tenant; the API's `TenantResolutionMiddleware` resolves it, RLS scopes every statement, and there is no tenant picker and no way to widen a request. Cross-tenant isolation is inherited unchanged and is proved at the portal layer by an integration test.
- **D1** — no server-side Node/Python. The portal is C#/Razor with **no npm, no package.json, no bundler, no CDN asset**.
- **§11.5 default #5** — "whether the review/override screen advances from Phase 2 into 1.5 if tenant demand appears early." Kept separable: `Pages/Review.cshtml` + its two API calls are the only review-specific surface; the page can ship alone.

## Parallel-execution seams (read before you touch a shared file)

P2-01..P2-05 are written to be implemented **in parallel by separate agents**. These are the only files this task shares with a sibling, and the rule for each:

| Shared file | Also touched by | Rule |
|---|---|---|
| `TelemetryGuard.Api/Endpoints/AdminEndpoints.cs` | P2-01 | Both add routes to the **same existing** `MapAdminEndpoints` group and both append private handlers. This task adds `/campaigns` + `/reports/flagged-sources`; P2-01 adds `/reports/publishers` + `/reports/sites` and one `IPublisherSummaryRepository` mention in the class doc comment. Purely additive — put your two `MapGet` lines at the end of the existing list and your handlers at the end of the file. |
| `TelemetryGuard.Api/Endpoints/AdminModels.cs` | P2-01 | Both **append** records at the end. Never reorder or edit existing records. |
| `TelemetryGuard.sln` | P2-05 | Both add one top-level project (`TelemetryGuard.Portal` here, `TelemetryGuard.Analytics.Kusto` there) with `dotnet sln add`. Never hand-edit GUIDs; re-run `dotnet sln add` if the file moved under you. |
| `tests/TelemetryGuard.Tests.Unit/…csproj` | P2-05 | Both add **one** `<ProjectReference>` to the same `<ItemGroup>`. Additive. |
| `tests/TelemetryGuard.Tests.Integration/…csproj` | P2-02 | Both add **one** `<ProjectReference>` to the same `<ItemGroup>` (this task: `TelemetryGuard.Portal`; P2-02: `TelemetryGuard.Training`). Additive. |
| `tests/TelemetryGuard.Tests.Unit/Api/AdminEndpointTests.cs` | P2-01 | Both extend the same `AdminApp` harness with a new fake repository. Register fakes the way that file already does — **`services.RemoveAll<T>(); services.AddSingleton<T>(fake);`**, never `TryAdd*` (the real repository is already registered by `AddTelemetryGuardData()`, so a `TryAdd` is a silent no-op). Add your fake class and your `[Fact]`s at the end of the file. |
| `doc/spec.md` | (none of P2-01/02/04/05) | Only this task appends a decision. `D24` is expected to be free; if a sibling has already taken it, take the next free number and keep the title. Never edit an existing decision. |

**No migration.** This task adds and edits **zero** files under `TelemetryGuard.Data/migrations/` and adds no row to `migrations/README.md`. (`0008` is P2-01's, `0009` is P2-02's — neither concerns this task.)

Nothing in this task depends on P2-01/P2-02/P2-04/P2-05 having landed, and nothing here blocks them.

## Prerequisites (exact contracts this task consumes — copy, do not re-derive)

**API-07 — `TelemetryGuard.Api/Endpoints/AdminEndpoints.cs` / `AdminModels.cs`** (group `app.MapGroup("/admin").AddEndpointFilter<AdminScopeFilter>()`):

| Route | Query/body | 200 shape (camelCase JSON) |
|---|---|---|
| `GET /admin/whitelist` | `type?` (`ip\|device_id\|fingerprint`), `offset?` (≥0), `limit?` (clamped 1..200, default 50) | `WhitelistEntryResponse[]` = `{id, type, value, reason, source, createdBy, createdUtc, expiresUtc}` |
| `POST /admin/whitelist` | `AddWhitelistRequest {type, value, reason, source, sessionId}` | **201** `{id}` at `Location: /admin/whitelist/{id}` |
| `DELETE /admin/whitelist/{id:long}` | — | **204**; **404** problem+json when absent |
| `GET /admin/reports/summary` | `campaignId` (GUID, **required**), `from`,`to` (`yyyy-MM-dd`, **required**, `from<=to`, span ≤ 366 d) | `{from, to, campaignId, rows:[{date, events, allowed, challenged, blocked, avgScore}]}` — `avgScore` is `null` when `events == 0` |
| `GET /admin/sites/integration-status` | — | `{sites:[{siteKey, domain, configuredMode, lastBeaconAt, effectiveLevel}]}` |

Validation failures are `Results.ValidationProblem` (400, `application/problem+json`, `errors: {field: [msg]}`). `POST /admin/whitelist` rules: `type` ∈ {ip, device_id, fingerprint}; `value` non-empty and a parseable IP when `type=="ip"`; `source` ∈ {manual, review_screen}; `sessionId` required and `^[A-Za-z0-9_-]{8,64}$` when `source=="review_screen"`.

**INT-02 — `EnforcementAdminEndpoints.cs` / `EnforcementAdminModels.cs`** (same `/admin` prefix + filter, nested `/enforcement`):

| Route | Query/body | 200 shape |
|---|---|---|
| `GET /admin/enforcement` | `status?` (one of `ExclusionStatuses.All`, default `pending`), `limit?` (clamped 1..500, default 100) | `ExclusionQueueEntryResponse[]` = `{id, platform, sourceType, value, campaignScope, reason, status, createdUtc, updatedUtc}`, newest first, **no offset paging** |
| `POST /admin/enforcement/approve` | `{ids:[long], note?}` — 1..500 ids | `{requested, approved}` |
| `POST /admin/enforcement/reject` | `{ids:[long], note?}` — note ≤ 400 chars | `{requested, rejected}` |

`TelemetryGuard.Data/Repositories/ExclusionStatuses.cs`: `pending, approved, rejected, pushed, failed, unsupported` (string constants, CHECK-constraint order). Ids not currently `pending` are silently skipped → `approved`/`rejected` < `requested` is normal and must be shown honestly.

**Auth** — `TelemetryGuard.Api/Auth/AdminScopeFilter.cs` + `Tenancy/TenantResolutionMiddleware.cs`: any `/admin/*` request without a resolvable `X-Api-Key` gets **401 `application/problem+json`** from the middleware *before routing*; a site-key-resolved request would get **403** from the filter. There is **no cookie, session, CORS or browser-facing auth on the API** — which is why the browser never talks to the API directly in this design.

**DAT-05 — `TelemetryGuard.Data/Repositories/IConfigRepositories.cs`** (registered `TryAddScoped` in `DataServiceCollectionExtensions.AddTelemetryGuardData`): `ICampaignRepository.ListAsync(ct) → IReadOnlyList<CampaignRecord>` where `CampaignRecord(Guid TenantId, Guid CampaignId, string Platform, string? ExternalCampaignId, string LandingUrl, string? GeoTargets, byte Status, DateTime CreatedUtc)`. **`dbo.Campaigns` has no `Name` column** (see `migrations/0001_core_schema.sql`) — a campaign is labelled `{Platform} · {ExternalCampaignId ?? "(no external id)"} · {CampaignId:D}`. `Status`: 0=Active, 1=Paused, 2=Archived.

**DAT-06 — `ISummaryRepositories.cs` / `Models/SummaryRecords.cs`**: `IVerdictSummaryRepository.GetTopFlaggedSourcesAsync(DateOnly from, DateOnly to, int limit, ct)` → `FlaggedSourceDailyRow(Guid TenantId, DateOnly Date, string SourceType, string Value, int FlaggedCount, int BlockedCount, long ScoreSum)`; the repository **throws `ArgumentOutOfRangeException` unless `limit` is 1..1000** — the new endpoint clamps before calling. `dbo.FlaggedSourcesDaily.SourceType` CHECK allows `ip|placement|device_id|fingerprint`; ANA-07's rollup writes `'ip'` today.

**ANA-07 — `TelemetryGuard.Api/Workers/RollupService.cs`** (`"Rollup": {"IntervalMinutes": 15, "LookbackDays": 3, "TopFlaggedLimit": 100}`) is the only writer that materializes ClickHouse → `dbo.VerdictDailySummaries` / `dbo.FlaggedSourcesDaily`. Consequence the UI must state: **dashboard numbers lag by up to ~15 minutes**, and API-06's live `IncrementDailySummaryAsync` keeps the current day roughly current in between. Campaign-less (pixel/organic) sessions are summarized under `CampaignId = Guid.Empty` (`VerdictFinalizer.cs`) — the campaign picker must therefore offer a synthetic `(no campaign — organic / pixel)` entry for `00000000-0000-0000-0000-000000000000`.

**INT-04 — `TelemetryGuard.Api/Workers/MetaExclusionSyncService.cs`** writes this exact `LastError` on Meta `ip` rows it marks `unsupported`; the enforcement screen renders it verbatim from the API response (do not paraphrase):

> `Meta Marketing API provides no IP exclusion capability; entry cannot be enforced on Meta.`

**Build/CI facts**: `Directory.Build.props` sets `Nullable=enable`, `ImplicitUsings=enable`, **`TreatWarningsAsErrors=true`**, `LangVersion=latest`. `.github/workflows/ci.yml` builds `TelemetryGuard.sln` (Release) and runs the Unit / Integration / Contracts projects — **a new project inside the sln needs no ci.yml change**, and the portal adds no npm step. Dev ports: API `http://localhost:5120` (`TelemetryGuard.Api/Properties/launchSettings.json`).

## Implementation steps

### 1. Record the D17 resolution as a spec amendment

Append to `doc/spec.md` §5, **after D23**, using the next free decision number (expected **D24**; if a sibling P2 task already claimed it, take the next free one and keep the title). Never edit D17's text.

```markdown
### D24 — Customer portal: **ASP.NET Core Razor Pages, server-rendered, zero JavaScript** *(resolves the D17 deferral)*

- The tenant-facing portal (`TelemetryGuard.Portal`, P2-03) is a Razor Pages app that talks to the existing `/admin/*` API over HTTP as a server-side client. Rejected: Blazor Server (a stateful circuit per user for four CRUD screens), Blazor WASM / static SPA (a second build chain; the D2 esbuild exception covers the tenant-page SDK, not our own admin UI, and it would force CORS onto `/admin`).
- **Auth is API-key only**: the operator pastes an admin API key, the portal validates it against `/admin`, and stores it in an encrypted HttpOnly cookie replayed as `X-Api-Key` server-side. Per-tenant user accounts, SSO and magic links stay in the backlog; there is no `dbo.Users` table.
- The portal holds **no** connection string (SQL, ClickHouse or Redis) and references no data project — D23's "portals never query the event store" is enforced by project structure, not by discipline.
```

Add the registry row to `doc/plan.md` only if that file tracks amendments; otherwise leave `plan.md` alone.

### 2. Two additive `/admin` endpoints (the only API change)

`TelemetryGuard.Api/Endpoints/AdminEndpoints.cs` — register inside the existing `MapAdminEndpoints`, next to the current routes (no `Program.cs` change; the group is already mapped):

```csharp
admin.MapGet("/campaigns", ListCampaignsAsync);
admin.MapGet("/reports/flagged-sources", GetFlaggedSourcesAsync);
```

Handlers (house style: scoped repositories injected, `CancellationToken ct` last, validation via the file's existing `ValidationProblem`/`TryParseDate`/`MaxSummaryRangeDays` helpers):

```csharp
    // ----------------------------------------------------------- campaigns --
    // P2-03: the summary report requires a campaignId and nothing else exposed the
    // tenant's campaign list. Read-only projection of DAT-05's ICampaignRepository;
    // LandingUrl/GeoTargets are deliberately NOT returned (the portal never needs
    // them, and LandingUrl is the /c open-redirect guardrail's own business).
    private static async Task<IResult> ListCampaignsAsync(
        ICampaignRepository campaigns, CancellationToken ct)
    {
        var rows = await campaigns.ListAsync(ct);
        return Results.Ok(new CampaignListResponse(
            rows.Select(c => new CampaignSummaryResponse(
                    c.CampaignId, c.Platform, c.ExternalCampaignId, c.Status))
                .ToList()));
    }

    // ------------------------------------------------------ flagged sources --
    // P2-03: D23 read of dbo.FlaggedSourcesDaily (SQL aggregate table) — the
    // review/override screen's data source. NEVER ClickHouse.
    private static async Task<IResult> GetFlaggedSourcesAsync(
        string? from, string? to, int? limit,
        IVerdictSummaryRepository summaries, CancellationToken ct)
    {
        if (from is null || !TryParseDate(from, out var fromDate))
            return ValidationProblem("from", "from is required and must be yyyy-MM-dd.");

        if (to is null || !TryParseDate(to, out var toDate))
            return ValidationProblem("to", "to is required and must be yyyy-MM-dd.");

        if (fromDate > toDate)
            return ValidationProblem("from", "from must be <= to.");

        if (toDate.DayNumber - fromDate.DayNumber > MaxSummaryRangeDays)
            return ValidationProblem("to", $"date range must not exceed {MaxSummaryRangeDays} days.");

        // DAT-06's repository throws outside 1..1000 — clamp, never forward blindly.
        var effectiveLimit = Math.Clamp(limit ?? 100, 1, 1000);

        var rows = await summaries.GetTopFlaggedSourcesAsync(fromDate, toDate, effectiveLimit, ct);
        return Results.Ok(new FlaggedSourcesReportResponse(
            fromDate, toDate,
            rows.Select(r => new FlaggedSourceResponse(
                    r.Date, r.SourceType, r.Value, r.FlaggedCount, r.BlockedCount, r.ScoreSum))
                .ToList()));
    }
```

`TelemetryGuard.Api/Endpoints/AdminModels.cs` — append:

```csharp
/// <summary>GET /admin/campaigns row. dbo.Campaigns has no Name column; callers
/// label a campaign from Platform + ExternalCampaignId + CampaignId.
/// Status: 0=Active, 1=Paused, 2=Archived (DAT-02).</summary>
public sealed record CampaignSummaryResponse(
    Guid CampaignId, string Platform, string? ExternalCampaignId, byte Status);

public sealed record CampaignListResponse(IReadOnlyList<CampaignSummaryResponse> Campaigns);

/// <summary>GET /admin/reports/flagged-sources row (dbo.FlaggedSourcesDaily).
/// ScoreSum is returned RAW and no average is computed: the denominator (scored
/// events for that source) is not stored on this table, and §7's missing-≠-zero
/// rule forbids inventing one. Contrast SummaryDayResponse.AvgScore, where
/// Events IS the denominator.</summary>
public sealed record FlaggedSourceResponse(
    DateOnly Date, string SourceType, string Value,
    int FlaggedCount, int BlockedCount, long ScoreSum);

public sealed record FlaggedSourcesReportResponse(
    DateOnly From, DateOnly To, IReadOnlyList<FlaggedSourceResponse> Sources);
```

Add `using TelemetryGuard.Data.Repositories;`-level types only — `ICampaignRepository` is already in the `TelemetryGuard.Data.Repositories` namespace the file imports. **Do not** touch `Program.cs`, `appsettings.json`, `AdminScopeFilter`, or the middleware.

### 3. Create the project and wire the solution

```bash
mkdir -p TelemetryGuard.Portal
# hand-write the csproj below (do NOT `dotnet new razor` — the template drags in
# bootstrap/jQuery under wwwroot/lib, which violates the no-CDN/no-JS rule)
dotnet sln TelemetryGuard.sln add TelemetryGuard.Portal/TelemetryGuard.Portal.csproj
```

`TelemetryGuard.Portal/TelemetryGuard.Portal.csproj` — **framework-only, by design**:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <!-- D23/D1 GUARDRAIL — INTENTIONALLY EMPTY: this project must have NO
       ProjectReference and NO PackageReference. Everything it uses (Razor Pages,
       cookie authentication, IHttpClientFactory, System.Text.Json, IMemoryCache)
       ships in the Microsoft.AspNetCore.App shared framework. Adding a reference to
       TelemetryGuard.Data / .Analytics.* would hand the portal a path to SQL Server
       and ClickHouse — the exact thing D23 forbids. Wire DTOs are re-declared
       locally in Api/AdminApiDtos.cs and pinned by a round-trip integration test. -->

  <!-- NO InternalsVisibleTo — see the BUILD PITFALL note in PortalEntryPoint.cs.
       Every type the test projects need is PUBLIC instead. Adding IVT here is a
       build-breaker, not a convenience. -->

</Project>
```

`TelemetryGuard.Portal/PortalEntryPoint.cs`:

```csharp
namespace TelemetryGuard.Portal;

/// <summary>Public marker type for WebApplicationFactory&lt;PortalEntryPoint&gt;.
///
/// BUILD PITFALL (two halves — both are real, and the second is easy to miss):
///  1. Do NOT add `public partial class Program` here the way TelemetryGuard.Api
///     does. The test projects reference both assemblies, and two public `Program`
///     types in the global namespace make every existing
///     WebApplicationFactory&lt;Program&gt; call ambiguous (CS0433).
///  2. Do NOT grant InternalsVisibleTo to TelemetryGuard.Tests.Unit /
///     .Tests.Integration either. This project's Program.cs uses top-level
///     statements, which emit an INTERNAL `Program` class in the global namespace;
///     IVT makes it accessible in the test assemblies and produces exactly the same
///     CS0433 against TelemetryGuard.Api's `public partial class Program`.
///     TelemetryGuard.Training.csproj carries this same warning in a comment for
///     the same reason — follow that precedent.
/// The consequence, which the rest of this task assumes: every portal type a test
/// touches is `public`, not `internal`.</summary>
public sealed class PortalEntryPoint;
```

**Accessibility rule for the whole project (do not deviate).** These types are `public` because the two test projects construct or deserialize them:

- `TelemetryGuard.Portal.Api`: `IAdminApiClient`, `PortalApiClient`, `AdminApiResult<T>`, `NoBody`, `PortalServiceCollectionExtensions`, and **every** record in `AdminApiDtos.cs`.
- `TelemetryGuard.Portal.Auth`: `PortalAuth` (its `Hint` is unit-tested directly).
- `TelemetryGuard.Portal.Options`: `PortalOptions` (already public).

Page models (`Pages/**`) and `PortalPageModel` stay whatever Razor Pages generates (public by convention) — tests assert on rendered HTML, never on a page-model type. Nothing else needs to be public; being public does not weaken D23, because the project still has zero data-tier references and no connection string.

Add the test-project references (test projects may reference the portal; the portal references nothing):

```xml
<!-- tests/TelemetryGuard.Tests.Unit/TelemetryGuard.Tests.Unit.csproj
     and tests/TelemetryGuard.Tests.Integration/TelemetryGuard.Tests.Integration.csproj -->
<ProjectReference Include="..\..\TelemetryGuard.Portal\TelemetryGuard.Portal.csproj" />
```

`.gitignore` needs **no** change (`bin/`, `obj/` are already covered; there is no build artifact directory). `ci.yml`, `docker-compose.yml` and `scripts/*` need **no** change — the portal runs on the host like the API: `dotnet run --project TelemetryGuard.Portal`.

### 4. Configuration + host composition

`TelemetryGuard.Portal/appsettings.json`:

```json
{
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
  "AllowedHosts": "*",
  // P2-03: the ONLY external dependency. No SQL/ClickHouse/Redis connection string
  // may ever appear in this file (D23 — the portal reads the admin API, nothing else).
  "Portal": {
    "ApiBaseUrl": "http://localhost:5120",
    "ApiTimeoutSeconds": 15,
    "SessionHours": 8,
    "SignInAttemptsPerIpPer5Min": 10
  }
}
```

`TelemetryGuard.Portal/Properties/launchSettings.json`: single `http` profile on `http://localhost:5140`, `ASPNETCORE_ENVIRONMENT=Development`.

`TelemetryGuard.Portal/Options/PortalOptions.cs`:

```csharp
namespace TelemetryGuard.Portal.Options;

public sealed class PortalOptions
{
    public const string SectionName = "Portal";

    public string ApiBaseUrl { get; set; } = "";
    public int ApiTimeoutSeconds { get; set; } = 15;
    public int SessionHours { get; set; } = 8;
    public int SignInAttemptsPerIpPer5Min { get; set; } = 10;
}
```

`TelemetryGuard.Portal/Program.cs`:

```csharp
using Microsoft.AspNetCore.Authentication.Cookies;
using TelemetryGuard.Portal.Api;
using TelemetryGuard.Portal.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.UseUtcTimestamp = true;
    o.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z' ";
});

builder.Services.AddOptions<PortalOptions>()
    .Bind(builder.Configuration.GetSection(PortalOptions.SectionName))
    .Validate(o => Uri.TryCreate(o.ApiBaseUrl, UriKind.Absolute, out _),
        "Portal:ApiBaseUrl must be an absolute URL (e.g. http://localhost:5120).")
    .ValidateOnStart();

builder.Services.AddMemoryCache();          // sign-in throttle (step 6)
builder.Services.AddHttpContextAccessor();  // PortalApiClient reads the session key
builder.Services.AddAdminApiClient(builder.Configuration);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "tg_portal";
        o.Cookie.HttpOnly = true;                 // the API key is NEVER reachable from script
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        o.LoginPath = "/signin";
        o.LogoutPath = "/signout";
        o.AccessDeniedPath = "/signin";
        o.ExpireTimeSpan = TimeSpan.FromHours(
            builder.Configuration.GetValue("Portal:SessionHours", 8));
        o.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();

builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/");                 // every page requires a session…
    o.Conventions.AllowAnonymousToPage("/SignIn");      // …except these two
    o.Conventions.AllowAnonymousToPage("/Error");
});

var app = builder.Build();

app.UseExceptionHandler("/Error");
app.UseStatusCodePagesWithReExecute("/Error", "?code={0}");

// Security headers. script-src 'none' is enforceable because this app ships ZERO
// JavaScript — keep it that way (see the guardrails section).
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["Content-Security-Policy"] =
        "default-src 'self'; script-src 'none'; style-src 'self'; img-src 'self'; " +
        "form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
    h.XContentTypeOptions = "nosniff";
    h["Referrer-Policy"] = "no-referrer";
    h.CacheControl = "no-store";   // tenant data must never be cached by a proxy
    await next();
});

app.UseStaticFiles();   // wwwroot/css/portal.css only
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();
app.Run();
```

### 5. Admin API client (`TelemetryGuard.Portal/Api/`)

`AdminApiResult.cs` — every call returns a result; **page handlers never throw on API failure**:

```csharp
namespace TelemetryGuard.Portal.Api;

/// <summary>Outcome of one admin-API call. Error is already human-readable
/// (rendered from the API's problem+json); SessionExpired means the key was
/// rejected (401/403) and the caller must sign the operator out.</summary>
public sealed record AdminApiResult<T>(T? Value, int StatusCode, string? Error, bool SessionExpired)
{
    public bool IsSuccess => Error is null && !SessionExpired;

    public static AdminApiResult<T> Ok(T value) => new(value, 200, null, false);
    public static AdminApiResult<T> Problem(int status, string error) => new(default, status, error, false);
    public static AdminApiResult<T> Rejected() =>
        new(default, 401, "The admin API rejected this API key.", true);
    public static AdminApiResult<T> Unreachable(string detail) =>
        new(default, 0, $"The admin API could not be reached: {detail}", false);
}

/// <summary>Marker for 204/empty-body responses (DELETE).</summary>
public sealed record NoBody;
```

`IAdminApiClient.cs` — intent-named, one method per screen action (D7: never a generic query passthrough):

```csharp
namespace TelemetryGuard.Portal.Api;

public interface IAdminApiClient
{
    /// <summary>Sign-in probe with an explicit key (no session yet): true iff
    /// GET /admin/campaigns returns 2xx.</summary>
    Task<bool> ValidateKeyAsync(string apiKey, CancellationToken ct);

    Task<AdminApiResult<CampaignListDto>> ListCampaignsAsync(CancellationToken ct);
    Task<AdminApiResult<SummaryReportDto>> GetSummaryAsync(Guid campaignId, DateOnly from, DateOnly to, CancellationToken ct);
    Task<AdminApiResult<IntegrationStatusReportDto>> GetIntegrationStatusAsync(CancellationToken ct);
    Task<AdminApiResult<FlaggedSourcesReportDto>> GetFlaggedSourcesAsync(DateOnly from, DateOnly to, int limit, CancellationToken ct);

    Task<AdminApiResult<IReadOnlyList<WhitelistEntryDto>>> ListWhitelistAsync(string? type, int offset, int limit, CancellationToken ct);
    Task<AdminApiResult<AddWhitelistResponseDto>> AddWhitelistAsync(AddWhitelistRequestDto request, CancellationToken ct);
    Task<AdminApiResult<NoBody>> DeleteWhitelistAsync(long id, CancellationToken ct);

    Task<AdminApiResult<IReadOnlyList<ExclusionQueueEntryDto>>> ListEnforcementAsync(string status, int limit, CancellationToken ct);
    Task<AdminApiResult<EnforcementApproveResponseDto>> ApproveEnforcementAsync(IReadOnlyList<long> ids, CancellationToken ct);
    Task<AdminApiResult<EnforcementRejectResponseDto>> RejectEnforcementAsync(IReadOnlyList<long> ids, string? note, CancellationToken ct);
}
```

`AdminApiDtos.cs` — local mirrors of the API's response records (field-for-field; the API serializes camelCase with the app default `System.Text.Json` options, and `DateOnly` round-trips as `yyyy-MM-dd` on .NET 8):

```csharp
namespace TelemetryGuard.Portal.Api;

// MIRRORS of TelemetryGuard.Api/Endpoints/AdminModels.cs + EnforcementAdminModels.cs.
// Re-declared (not project-referenced) so the portal keeps zero project references
// (D23). PortalAdminApiContractTests round-trips every one of these against the real
// API test host — if a field name drifts, that test fails, not production.
public sealed record CampaignSummaryDto(Guid CampaignId, string Platform, string? ExternalCampaignId, byte Status);
public sealed record CampaignListDto(IReadOnlyList<CampaignSummaryDto> Campaigns);

public sealed record SummaryDayDto(DateOnly Date, int Events, int Allowed, int Challenged, int Blocked, double? AvgScore);
public sealed record SummaryReportDto(DateOnly From, DateOnly To, Guid CampaignId, IReadOnlyList<SummaryDayDto> Rows);

public sealed record SiteIntegrationStatusDto(
    string SiteKey, string Domain, string ConfiguredMode, DateTime? LastBeaconAt, string EffectiveLevel);
public sealed record IntegrationStatusReportDto(IReadOnlyList<SiteIntegrationStatusDto> Sites);

public sealed record FlaggedSourceDto(
    DateOnly Date, string SourceType, string Value, int FlaggedCount, int BlockedCount, long ScoreSum);
public sealed record FlaggedSourcesReportDto(DateOnly From, DateOnly To, IReadOnlyList<FlaggedSourceDto> Sources);

public sealed record WhitelistEntryDto(
    long Id, string Type, string Value, string? Reason, string Source,
    string? CreatedBy, DateTime CreatedUtc, DateTime? ExpiresUtc);
public sealed record AddWhitelistRequestDto(string Type, string Value, string? Reason, string Source, string? SessionId);
public sealed record AddWhitelistResponseDto(long Id);

public sealed record ExclusionQueueEntryDto(
    long Id, string Platform, string SourceType, string Value, Guid? CampaignScope,
    string Reason, string Status, DateTime CreatedUtc, DateTime? UpdatedUtc);
public sealed record EnforcementBatchRequestDto(long[] Ids, string? Note);
public sealed record EnforcementApproveResponseDto(int Requested, int Approved);
public sealed record EnforcementRejectResponseDto(int Requested, int Rejected);
```

`PortalApiClient.cs` — the single place that speaks HTTP:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TelemetryGuard.Portal.Auth;

namespace TelemetryGuard.Portal.Api;

public sealed class PortalApiClient(HttpClient http, IHttpContextAccessor accessor) : IAdminApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<bool> ValidateKeyAsync(string apiKey, CancellationToken ct)
        => (await SendAsync<CampaignListDto>(HttpMethod.Get, "/admin/campaigns", null, apiKey, ct)).IsSuccess;

    public Task<AdminApiResult<CampaignListDto>> ListCampaignsAsync(CancellationToken ct)
        => SendAsync<CampaignListDto>(HttpMethod.Get, "/admin/campaigns", null, SessionKey(), ct);

    public Task<AdminApiResult<SummaryReportDto>> GetSummaryAsync(
        Guid campaignId, DateOnly from, DateOnly to, CancellationToken ct)
        => SendAsync<SummaryReportDto>(HttpMethod.Get,
            $"/admin/reports/summary?campaignId={campaignId:D}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}",
            null, SessionKey(), ct);

    public Task<AdminApiResult<FlaggedSourcesReportDto>> GetFlaggedSourcesAsync(
        DateOnly from, DateOnly to, int limit, CancellationToken ct)
        => SendAsync<FlaggedSourcesReportDto>(HttpMethod.Get,
            $"/admin/reports/flagged-sources?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&limit={limit}",
            null, SessionKey(), ct);

    public Task<AdminApiResult<IReadOnlyList<ExclusionQueueEntryDto>>> ListEnforcementAsync(
        string status, int limit, CancellationToken ct)
        => SendAsync<IReadOnlyList<ExclusionQueueEntryDto>>(HttpMethod.Get,
            $"/admin/enforcement?status={Uri.EscapeDataString(status)}&limit={limit}", null, SessionKey(), ct);

    public Task<AdminApiResult<EnforcementApproveResponseDto>> ApproveEnforcementAsync(
        IReadOnlyList<long> ids, CancellationToken ct)
        => SendAsync<EnforcementApproveResponseDto>(HttpMethod.Post, "/admin/enforcement/approve",
            new EnforcementBatchRequestDto([.. ids], null), SessionKey(), ct);

    // …RejectEnforcementAsync, ListWhitelistAsync, AddWhitelistAsync,
    //   DeleteWhitelistAsync, GetIntegrationStatusAsync follow the same shape.

    /// <summary>The signed-in operator's API key, from the auth cookie's claim.
    /// Every page that calls this is behind AuthorizeFolder("/"), so a missing key
    /// is a wiring bug, not a user state.</summary>
    private string SessionKey()
        => PortalAuth.ApiKeyFor(accessor.HttpContext?.User)
           ?? throw new InvalidOperationException("No portal session API key on this request.");

    private async Task<AdminApiResult<T>> SendAsync<T>(
        HttpMethod method, string path, object? body, string apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);   // never logged, never in a URL
        if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return AdminApiResult<T>.Unreachable(ex.Message);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return AdminApiResult<T>.Rejected();

            if (!response.IsSuccessStatusCode)
                return AdminApiResult<T>.Problem((int)response.StatusCode, await DescribeProblemAsync(response, ct));

            if (typeof(T) == typeof(NoBody) || response.StatusCode == HttpStatusCode.NoContent)
                return AdminApiResult<T>.Ok((T)(object)new NoBody());

            var value = await response.Content.ReadFromJsonAsync<T>(Json, ct);
            return value is null
                ? AdminApiResult<T>.Problem((int)response.StatusCode, "The admin API returned an empty body.")
                : AdminApiResult<T>.Ok(value);
        }
    }

    /// <summary>Renders RFC 7807 problem+json into one line. ValidationProblem bodies
    /// carry `errors: {field: [messages]}` — surface the API's own wording; the portal
    /// never invents validation text.</summary>
    private static async Task<string> DescribeProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var doc = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            var title = doc.TryGetProperty("title", out var t) ? t.GetString() : null;
            var detail = doc.TryGetProperty("detail", out var d) ? d.GetString() : null;
            var fields = new List<string>();
            if (doc.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
                foreach (var field in errors.EnumerateObject())
                    foreach (var message in field.Value.EnumerateArray())
                        fields.Add($"{field.Name}: {message.GetString()}");

            var parts = new[] { title, detail, fields.Count == 0 ? null : string.Join("; ", fields) }
                .Where(p => !string.IsNullOrEmpty(p));
            var text = string.Join(" — ", parts);
            return string.IsNullOrEmpty(text) ? $"Admin API returned {(int)response.StatusCode}." : text;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return $"Admin API returned {(int)response.StatusCode}.";
        }
    }
}
```

`PortalServiceCollectionExtensions.cs` (mirrors the `AddTurnstileVerification` / `AddGoogleAdsGateway` precedent):

```csharp
public static IServiceCollection AddAdminApiClient(
    this IServiceCollection services, IConfiguration configuration)
{
    var options = configuration.GetSection(PortalOptions.SectionName).Get<PortalOptions>() ?? new PortalOptions();
    services.AddHttpClient<IAdminApiClient, PortalApiClient>(c =>
    {
        c.BaseAddress = new Uri(options.ApiBaseUrl);
        c.Timeout = TimeSpan.FromSeconds(options.ApiTimeoutSeconds);
    });
    return services;
}
```

### 6. Session auth (`TelemetryGuard.Portal/Auth/PortalAuth.cs`) + sign-in throttle

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace TelemetryGuard.Portal.Auth;

/// <summary>
/// API-key-only session (spec amendment D24). The raw key rides in the auth cookie,
/// which ASP.NET Core Data Protection encrypts and signs, and which is HttpOnly +
/// SameSite=Strict — so it is never readable from script and never appears in a URL,
/// a log line or the rendered page (only Hint() does).
/// OPERATIONAL NOTE: the Data Protection key ring is per-process by default; a portal
/// restart invalidates existing cookies and everyone signs in again. Persisting the
/// key ring belongs to the (not-yet-written) portal-deployment task — NOT to P2-04,
/// whose scope is frozen at the API image and its Bicep.
/// </summary>
public static class PortalAuth
{
    public const string ApiKeyClaim = "tg:api_key";
    public const string KeyHintClaim = "tg:api_key_hint";

    public static ClaimsPrincipal PrincipalFor(string apiKey) => new(
        new ClaimsIdentity(
            [
                new Claim(ApiKeyClaim, apiKey),
                new Claim(KeyHintClaim, Hint(apiKey)),
                new Claim(ClaimTypes.Name, $"api-key {Hint(apiKey)}"),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme));

    public static string? ApiKeyFor(ClaimsPrincipal? user) => user?.FindFirst(ApiKeyClaim)?.Value;
    public static string? HintFor(ClaimsPrincipal? user) => user?.FindFirst(KeyHintClaim)?.Value;

    /// <summary>Last 4 characters only — the sole form of the key that may be rendered.</summary>
    public static string Hint(string apiKey) => apiKey.Length <= 4 ? "****" : $"…{apiKey[^4..]}";
}
```

`Pages/SignIn.cshtml(.cs)` — `@page "/signin"`, `[AllowAnonymous]`:

1. `OnGet(string? returnUrl)` — renders the form (a single `<input type="password" name="ApiKey" autocomplete="off">`, the antiforgery token, and the return url as a hidden field).
2. `OnPostAsync(string? returnUrl, CancellationToken ct)`:
   - throttle: `IMemoryCache` counter keyed `signin:{HttpContext.Connection.RemoteIpAddress}` with a 5-minute sliding window; over `Portal:SignInAttemptsPerIpPer5Min` → re-render with "Too many attempts. Wait a few minutes." and **do not call the API** (the API rejects unresolvable keys in middleware *before* its rate limiter, so this throttle is the only brake on key guessing);
   - empty key → "Enter your admin API key.";
   - `await api.ValidateKeyAsync(key.Trim(), ct)` false → increment the counter, "That API key was rejected by the admin API." (never echo the key back);
   - success → `HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, PortalAuth.PrincipalFor(key.Trim()), new AuthenticationProperties { IsPersistent = false })` then `LocalRedirect(SafeReturn(returnUrl))`.
   - `SafeReturn` = `Url.IsLocalUrl(returnUrl) ? returnUrl : "/"` — **open-redirect guard**, same discipline as API-02's `LandingUrl` rule.
3. `Pages/SignOut.cshtml(.cs)` — `@page "/signout"`, POST-only handler: `await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); return RedirectToPage("/SignIn");`

**Shared page behavior**: give every authenticated page model a base class `PortalPageModel` with a helper
`IActionResult? HandleSessionExpiry<T>(AdminApiResult<T> result)` returning `RedirectToPage("/SignIn")` after `SignOutAsync()` when `result.SessionExpired` is true, and otherwise `null` (the page continues and renders `result.Error` in its panel). One rejected call must never 500 the page.

### 7. Layout, styling, and the no-JS rule

- `Pages/_ViewImports.cshtml`: `@using TelemetryGuard.Portal`, `@using TelemetryGuard.Portal.Api`, `@namespace TelemetryGuard.Portal.Pages`, `@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers`.
- `Pages/_ViewStart.cshtml`: `Layout = "_Layout";`
- `Pages/Shared/_Layout.cshtml`: header with the product name, the signed-in key hint (`PortalAuth.HintFor(User)`), a sign-out form (POST), and nav links to `/` (Dashboard), `/review` (Review & override), `/enforcement` (Approval queue), `/whitelist` (Whitelist). `<link rel="stylesheet" href="~/css/portal.css">` — **no `<script>` tag anywhere in the app.**
- `Pages/Shared/_ApiError.cshtml` partial: renders one `AdminApiResult` error box (`role="alert"`).
- `wwwroot/css/portal.css`: hand-written, ~150 lines — system font stack, table/`form`/`.panel`/`.badge-{pending,approved,rejected,pushed,failed,unsupported}` styles. No web fonts, no CDN URL (the CSP forbids both).
- All values that come from the API (IPs, placement URLs, reasons, domains) are rendered through Razor's default `@value` encoding. **Never** use `@Html.Raw` anywhere in this project.

### 8. Dashboard — `Pages/Index.cshtml(.cs)` (`@page "/"`)

Query: `?campaignId=<guid>&from=yyyy-MM-dd&to=yyyy-MM-dd`. Defaults: `to = DateOnly.FromDateTime(DateTime.UtcNow)`, `from = to.AddDays(-13)` (14 days), `campaignId` = first campaign from `/admin/campaigns` or `Guid.Empty` when the tenant has none.

`OnGetAsync` makes these calls, each rendered into its own panel and each failing independently:

1. `ListCampaignsAsync` → the picker (`<select name="campaignId">` inside a GET form with the two date inputs and a submit button — no JS). Options are labelled `{Platform} · {ExternalCampaignId ?? "(no external id)"} · {CampaignId:D}` with a `(paused)`/`(archived)` suffix for `Status` 1/2, **plus a fixed first option** `(no campaign — organic / pixel)` with value `00000000-0000-0000-0000-000000000000` (API-06 summarizes campaign-less sessions under `Guid.Empty`).
2. `GetSummaryAsync(campaignId, from, to)` → table: Date · Events · Allowed · Challenged · Blocked · Avg score. `avgScore == null` renders `—` (**never `0`** — §7 missing ≠ zero). A totals row sums the four counts and prints `—` in the Avg column with the footnote: *"Per-day averages cannot be re-aggregated exactly (the API returns each day's mean rounded to 1 dp)."* Do **not** fabricate a weighted average.
3. Exclusion-queue counts: `ListEnforcementAsync(status, limit: 500)` for `pending`, `failed`, `unsupported` → three badges showing `rows.Count`, rendered as `500+` when the count equals 500 (the endpoint has no total-count or offset), each linking to `/enforcement?status=…`.
4. `GetIntegrationStatusAsync` → D22 panel: one row per site with `SiteKey`, `Domain`, `ConfiguredMode` (`js`/`pixel`), `EffectiveLevel` (`js`/`http-only`), `LastBeaconAt` (or `never`), plus this fixed explainer rendered whenever any row is `pixel` or `http-only`:

   > **Pixel / HTTP-only sites are scored on fewer signals.** Without the JS snippet we never receive behavioral timing, fingerprint or honeypot signals, storage age, or beacon integrity — those features are recorded as *missing* (not zero) and scoring falls back to network- and velocity-tier signals only. A site configured as `js` that shows `http-only` has had no beacon in the last 24 hours: check that the snippet is still installed.

   Also state under the table: *"Summary figures are materialized from the event store every ~15 minutes; the current day updates live per verdict."*

### 9. Review & override — `Pages/Review.cshtml(.cs)` (`@page "/review"`)

The D19 override screen, source-level (see Spec context).

- `OnGetAsync(from, to, limit)`: defaults `to = today (UTC)`, `from = to.AddDays(-6)`, `limit = 100`; calls `GetFlaggedSourcesAsync`. Table columns: Date · Source type · Value · Flagged · Blocked · Score sum · Override.
- The **Override** cell is a POST form per row (`asp-page-handler="Override"`) with hidden `sourceType`/`value`, an optional `sessionId` text input, an optional `reason` text input (pre-filled `Marked real customer in portal review`), and a submit button labelled `Mark as real customer`.
- `OnPostOverrideAsync(string sourceType, string value, string? sessionId, string? reason, CancellationToken ct)`:

  ```csharp
  // dbo.WhitelistEntries.SourceType allows ip|device_id|fingerprint only —
  // 'placement' rows (allowed by dbo.FlaggedSourcesDaily's CHECK) are NOT
  // whitelistable and the view renders an explanation instead of a button.
  if (sourceType is not ("ip" or "device_id" or "fingerprint"))
  {
      TempData["Error"] = "Placements cannot be whitelisted — reject the pending exclusion "
                        + "in the approval queue instead.";
      return RedirectToPage(new { from = From, to = To, limit = Limit });
  }

  // D19: a session id turns this into a review_screen add, and API-07 -> DAT-07's
  // WhitelistRepository.AddAsync emits exactly one negative training label
  // (LabelValues.Legit / LabelSources.ReviewScreen). Without one the API would
  // reject source='review_screen' (its sessionId rule), so we send 'manual' and NO
  // label is written — say so in the UI rather than faking a label.
  var trimmedSession = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
  if (trimmedSession is not null && !SessionIdShape().IsMatch(trimmedSession))
  {
      TempData["Error"] = "Session id must match ^[A-Za-z0-9_-]{8,64}$.";
      return RedirectToPage(new { from = From, to = To, limit = Limit });
  }

  var request = new AddWhitelistRequestDto(
      Type: sourceType,
      Value: value,
      Reason: Truncate(reason ?? "Marked real customer in portal review", 400), // nvarchar(400)
      Source: trimmedSession is null ? "manual" : "review_screen",
      SessionId: trimmedSession);
  ```

  On success: `TempData["Message"] = trimmedSession is null ? $"{value} whitelisted (no training label — no session id supplied)." : $"{value} whitelisted and a negative training label was recorded for session {trimmedSession}.";` then PRG back to the same filters. On `SessionExpired` → sign out + `/signin`. On any other error → `TempData["Error"] = result.Error`.
- Static copy under the table (the honest limits, all three are real):
  - *"Whitelisting prevents future challenges and blocks for this source. It does not rewrite past verdicts (verdicts are immutable records) and does not remove an exclusion already pushed to Google Ads or Meta — for a pending exclusion, reject it in the approval queue."*
  - *"Review is per source and per day. Individual events are not shown here: raw events live in the event store, which the portal deliberately never queries."*
  - *"Score sum is shown raw; no average is computed because the number of scored events for a source is not stored on this table."*
- `[GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")] private static partial Regex SessionIdShape();` on the page model (`public sealed partial class ReviewModel`) — same shape constant as `AdminEndpoints.SidShape()`.

### 10. Approval queue — `Pages/Enforcement.cshtml(.cs)` (`@page "/enforcement"`)

- `OnGetAsync(string? status, int? limit)`: `status` defaults to `pending` and must be one of the six literals (unknown → treat as `pending`; a status filter bar links all six). `limit` clamped 1..500 (default 100). Calls `ListEnforcementAsync`.
- Table: checkbox (`name="ids" value="{id}"`) · Id · Platform · Source type · Value · Campaign scope (`—` when null = tenant-wide) · Reason · Status badge · Created · Updated. When `status != "pending"` the checkbox column and the action buttons are **omitted** (the API only transitions `pending`).
- Two submit buttons in one form: `asp-page-handler="Approve"` and `asp-page-handler="Reject"`, plus a `note` text input (≤ 400 chars, meaningful only for reject). No JS ⇒ no "select all"; the page says *"Tick the rows you want to act on."*
- Handlers post `ids` to the matching endpoint and report the API's own numbers verbatim: `TempData["Message"] = $"{value.Approved} of {value.Requested} approved. Rows that were no longer pending were skipped.";` (same for reject). Never claim more than the API reported.
- Rows whose `status == "unsupported"` render the API's `reason`/status verbatim next to the INT-04 sentence quoted in the prerequisites; rows with `status == "failed"` show that the sync worker will not retry automatically (re-queueing is a manual operation — INT-03's documented posture).
- A fixed note on the page: *"Approving queues the exclusion for the platform sync worker (it runs every ~15 minutes). Nothing here pushes to an ad platform directly, and nothing here affects the challenge band — mid-band Turnstile prompts are always automatic."*

### 11. Whitelist management — `Pages/Whitelist.cshtml(.cs)` (`@page "/whitelist"`)

- `OnGetAsync(string? type, int? offset)`: `type` optional (`ip|device_id|fingerprint`; anything else → treated as unset), `offset ≥ 0`, page size 50 (API clamps 1..200). Prev/Next links move `offset` by 50; Next is rendered only when the returned page is full (the endpoint returns no total).
- Table: Id · Type · Value · Reason · Source (`manual`/`review_screen`) · Created · Expires (`never` when null) · Delete button (POST, `asp-page-handler="Delete"`, hidden `id`).
- Add form: `type` `<select>`, `value`, `reason`, optional `sessionId` (same D19 semantics and the same `^[A-Za-z0-9_-]{8,64}$` pre-check as step 9), submit → `AddWhitelistAsync`. The API's own 400s (bad IP, bad session id, …) are rendered verbatim from problem+json.
- Note under the add form: *"Adding an entry that already exists updates its reason and returns the same id — re-submitting is safe."* (DAT-07's `AddAsync` is a MERGE keyed on `(TenantId, SourceType, Value)`.)

### 12. Error page

`Pages/Error.cshtml(.cs)` — `@page "/error"`, `[AllowAnonymous]`, `[ResponseCache(NoStore = true)]`: shows the status code (from `?code=`) and a link back to `/`. Never render exception details or the API key.

## Files to create or modify

**New — portal (framework-only project):**
- `TelemetryGuard.Portal/TelemetryGuard.Portal.csproj`
- `TelemetryGuard.Portal/Program.cs`
- `TelemetryGuard.Portal/PortalEntryPoint.cs`
- `TelemetryGuard.Portal/appsettings.json`, `TelemetryGuard.Portal/Properties/launchSettings.json`
- `TelemetryGuard.Portal/Options/PortalOptions.cs`
- `TelemetryGuard.Portal/Api/IAdminApiClient.cs`, `PortalApiClient.cs`, `AdminApiDtos.cs`, `AdminApiResult.cs`, `PortalServiceCollectionExtensions.cs`
- `TelemetryGuard.Portal/Auth/PortalAuth.cs`
- `TelemetryGuard.Portal/Pages/PortalPageModel.cs`
- `TelemetryGuard.Portal/Pages/_ViewImports.cshtml`, `_ViewStart.cshtml`
- `TelemetryGuard.Portal/Pages/Shared/_Layout.cshtml`, `Shared/_ApiError.cshtml`
- `TelemetryGuard.Portal/Pages/SignIn.cshtml(.cs)`, `SignOut.cshtml(.cs)`, `Index.cshtml(.cs)`, `Review.cshtml(.cs)`, `Enforcement.cshtml(.cs)`, `Whitelist.cshtml(.cs)`, `Error.cshtml(.cs)`
- `TelemetryGuard.Portal/wwwroot/css/portal.css`

**Modified — API (two additive endpoints only):**
- `TelemetryGuard.Api/Endpoints/AdminEndpoints.cs` (two `MapGet` lines + two handlers)
- `TelemetryGuard.Api/Endpoints/AdminModels.cs` (four DTO records)

**Modified — shared:**
- `TelemetryGuard.sln` (add the portal project — use `dotnet sln add`, do not hand-edit GUIDs)
- `doc/spec.md` (append the D24 amendment from step 1 — never edit an existing decision)
- `tests/TelemetryGuard.Tests.Unit/TelemetryGuard.Tests.Unit.csproj`, `tests/TelemetryGuard.Tests.Integration/TelemetryGuard.Tests.Integration.csproj` (one `ProjectReference` each)

**New — tests:**
- `tests/TelemetryGuard.Tests.Unit/Portal/PortalSignInTests.cs`, `PortalPageRenderTests.cs`, `PortalActionPostTests.cs`
- `tests/TelemetryGuard.Tests.Unit/Api/AdminEndpointTests.cs` (extend: the two new endpoints)
- `tests/TelemetryGuard.Tests.Integration/Portal/PortalAdminApiContractTests.cs`

**Explicitly NOT touched:** `TelemetryGuard.Api/Program.cs`, `TelemetryGuard.Api/appsettings.json`, `TelemetryGuard.Api/Auth/AdminScopeFilter.cs`, `Tenancy/TenantResolutionMiddleware.cs`, `TelemetryGuard.Data/**` (no migration — the migration registry gains no row), `.github/workflows/ci.yml`, `docker-compose.yml`, `scripts/**`, `.gitignore`, `Dockerfile`/`.dockerignore`, `infra/**`, `CLAUDE.md`, `doc/plan.md`, `TelemetryGuard.Sdk/**`, `TelemetryGuard.Training/**`.

Also not touched: any sibling P2 task's files — `TelemetryGuard.Analytics.Kusto/**` (P2-05), `TelemetryGuard.Api/Workers/**` and `TelemetryGuard.Api/Startup/**` (P2-01/P2-02), and every file under `TelemetryGuard.Data/migrations/`.

## Acceptance criteria

**Build & structure**
- `dotnet build TelemetryGuard.sln` succeeds in Release with **zero warnings** (`TreatWarningsAsErrors=true` covers the portal, Razor compilation included).
- `TelemetryGuard.Portal.csproj` contains **no `<ProjectReference>` and no `<PackageReference>`** (D23 enforced structurally). `grep -rniE "clickhouse|SqlConnection|StackExchange|ConnectionStrings" TelemetryGuard.Portal/` returns nothing.
- `grep -rn "InternalsVisibleTo" TelemetryGuard.Portal/` returns **nothing**, and `grep -rn "class Program" TelemetryGuard.Portal/` returns nothing — the two halves of the `Program`-ambiguity pitfall. The existing `WebApplicationFactory<Program>` call sites in `tests/TelemetryGuard.Tests.Unit/Api/*` and `tests/TelemetryGuard.Tests.Integration/Api/*` still compile untouched; if any of them starts reporting CS0433/CS0104, an `InternalsVisibleTo` or a `Program` type crept into the portal — remove it rather than qualifying the call sites.
- `grep -rn "<script" TelemetryGuard.Portal/` returns nothing; `grep -rn "Html.Raw" TelemetryGuard.Portal/` returns nothing; there is no `package.json`, `node_modules/` or bundler config anywhere under `TelemetryGuard.Portal/`.
- No file under `TelemetryGuard.Data/migrations/` is added or edited; `migrations/README.md` is unchanged.

**API additions**
- `GET /admin/campaigns` returns the ambient tenant's campaigns and **401s without `X-Api-Key`** (middleware, before the handler) — same as every other `/admin` route.
- `GET /admin/reports/flagged-sources` validates `from`/`to` (`yyyy-MM-dd`, `from<=to`, ≤ 366 days) with `ValidationProblem` bodies, clamps `limit` into 1..1000 (so the DAT-06 repository never throws), and returns `scoreSum` **without** any computed average.

**Portal behavior**
- An unauthenticated GET of `/`, `/review`, `/enforcement`, `/whitelist` 302s to `/signin?ReturnUrl=…`; the return url is honored only when `Url.IsLocalUrl` (an absolute external url falls back to `/`).
- Signing in with a key the API rejects re-renders the form with an error, sets **no** cookie, and never echoes the key; after `Portal:SignInAttemptsPerIpPer5Min` failures from one IP the portal stops calling the API and says so.
- Signing in with a valid key sets the `tg_portal` cookie with `HttpOnly`, `SameSite=Strict` (and `Secure` outside Development); the rendered pages contain the key **hint** (`…abcd`) and never the key itself (`grep` the response body for the key must fail).
- Every rendered page carries `Content-Security-Policy: … script-src 'none' …`, `X-Content-Type-Options: nosniff`, `Cache-Control: no-store`.
- When the admin API returns 401/403 mid-session (key revoked), the portal signs the operator out and redirects to `/signin` instead of showing a broken page.
- When one panel's call fails (e.g. integration-status while Redis is down), that panel shows the API's problem+json text and **the rest of the page still renders** — no 500.
- Dashboard: a day with `events == 0` renders `—` for average score (never `0`); the totals row shows `—` for average; the campaign picker includes the `(no campaign — organic / pixel)` option for `Guid.Empty`; the D22 panel shows `configuredMode` and `effectiveLevel` per site with the pixel explainer.
- Review: "Mark as real customer" **without** a session id posts `source:"manual"`; **with** a valid session id posts `source:"review_screen"` + that `sessionId`, and the confirmation message states which of the two happened. A `placement` row offers no override button.
- Enforcement: approve/reject post only the ticked ids and report the API's `{requested, approved|rejected}` verbatim; the action controls are absent for non-`pending` filters; an `unsupported` Meta row displays the INT-04 sentence verbatim.
- Whitelist: add/delete/list round-trip; the API's validation messages (bad IP, bad session id, unknown type) appear verbatim.
- **Cross-tenant isolation at the portal layer**: signed in with tenant A's key, no tenant B row (whitelist, flagged source, exclusion, campaign, site) appears on any page — proved against a real migrated SQL Server with RLS.
- **XSS**: a whitelist reason / flagged-source value containing `<script>alert(1)</script>` renders escaped (`&lt;script&gt;`) in the HTML and never as a live tag.

## Testing

**Unit — `tests/TelemetryGuard.Tests.Unit/Portal/`** (no containers; the admin API is a stub):

- Harness: `WebApplicationFactory<TelemetryGuard.Portal.PortalEntryPoint>` with
  `b.UseSolutionRelativeContentRoot("TelemetryGuard.Portal")` (avoids MvcTestingAppManifest flakiness),
  `b.UseSetting("Portal:ApiBaseUrl", "http://admin.test")`, and
  `b.ConfigureTestServices(s => s.AddHttpClient<IAdminApiClient, PortalApiClient>(c => c.BaseAddress = new Uri("http://admin.test")).ConfigurePrimaryHttpMessageHandler(() => new StubAdminApiHandler(...)))`
  — re-registering the typed client is intentional: the later configure action wins, so the stub handler replaces the real transport.
  `StubAdminApiHandler : HttpMessageHandler` records every `(method, path, X-Api-Key, body)` and replays canned JSON per route (including a 400 `ValidationProblem`, a 401, and a network failure).
- Clients: `Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false })` (cookies are handled by default); a helper extracts `__RequestVerificationToken` from the rendered form with a regex so POST tests satisfy Razor Pages' automatic antiforgery validation.
- Cases: anonymous redirect to `/signin`; sign-in success sets the cookie and redirects to the local return url; sign-in failure sets no cookie; external return url is ignored; throttle trips after N failures; the outbound `X-Api-Key` equals the key that was signed in with; dashboard renders rows and `—` for null/zero-event averages; the campaign picker contains the `Guid.Empty` option; approve posts `{"ids":[…]}` and renders `"2 of 3 approved"`; review override maps empty-vs-valid session id to `manual` vs `review_screen` (**assert the exact captured request body**); an invalid session id is rejected before any API call is made; a 400 problem+json renders the API's message; a 401 mid-session clears the cookie; security headers present; hostile strings are HTML-escaped; every response body contains no `<script`.

**Unit — `tests/TelemetryGuard.Tests.Unit/Api/AdminEndpointTests.cs`** (extend the existing `AdminApp` harness): add a `FakeCampaignRepository`; assert `/admin/campaigns` shape and its 401-without-key; assert `/admin/reports/flagged-sources` date validation, `limit` clamping at both ends (0 → 1, 5000 → 1000, captured by the fake repository), and that the response carries `scoreSum` and **no** average field.

**Integration — `tests/TelemetryGuard.Tests.Integration/Portal/PortalAdminApiContractTests.cs`** (`[Collection("sqlserver")]`, `[Trait("Category","Integration")]`): the portal against the **real** API.

- Build `apiFactory = new WebApplicationFactory<Program>()` exactly as `Api/AdminEndpointTests.cs` does (fixture connection strings, unreachable ClickHouse, `ILabelSink` swapped for a capturing fake), then build the portal factory with
  `.ConfigureTestServices(s => s.AddHttpClient<IAdminApiClient, PortalApiClient>(c => c.BaseAddress = new Uri("http://api.local")).ConfigurePrimaryHttpMessageHandler(() => apiFactory.Server.CreateHandler()))`.
- Sign in with `SqlServerFixture.ApiKeyA`; assert:
  1. every DTO in `AdminApiDtos.cs` deserializes from the real endpoint (**the wire-contract drift guard** — dashboard, review, enforcement and whitelist pages all render 200);
  2. the dashboard lists `SqlServerFixture.CampaignA1` and the integration panel lists `SqlServerFixture.SiteKeyA` (`a.example.com`, configured `js`);
  3. a review override **with** a fresh session id creates a real `dbo.WhitelistEntries` row (verified through `fx.OpenAsync(SqlServerFixture.TenantA)`) **and** produces exactly one captured `LabelEvent` with `LabelValues.Legit` / `LabelSources.ReviewScreen` — and the same override **without** a session id creates the row with `Source='manual'` and produces **zero** additional labels;
  4. **cross-tenant**: seed a `dbo.WhitelistEntries` row and a `dbo.ExclusionQueue` row for `SqlServerFixture.TenantB` on a SYSTEM-stamped connection, then assert those values appear nowhere in the tenant-A portal's `/whitelist` and `/enforcement` HTML;
  5. approving a seeded `pending` exclusion through the portal flips the row to `'approved'` in SQL and writes one `dbo.EnforcementAudit` row; approving it again reports `0 of 1` (idempotent, honest).
- **No Playwright, no browser, no Node** in the portal's test path — assertions are on the returned HTML string.

## Out of scope / guardrails

- **Never read ClickHouse from the portal (D23)** and never add a generic cross-engine query path (D7). The portal has no data-tier reference, no connection string and no `IAnalyticsQueries` — keep it that way; if a screen needs new data, add an intent-named `/admin` endpoint over the SQL aggregate tables instead.
- **No user accounts, no SSO, no magic links, no `dbo.Users`/sessions table, no migration.** API-key-only auth is the decision (D24); per-tenant users are backlog. This task adds **zero** SQL scripts.
- **Overrides whitelist only.** They never lower a score, never rewrite verdict history (verdicts are immutable), never delete an already-pushed platform exclusion, and never mutate `dbo.FlaggedSourcesDaily`/`dbo.VerdictDailySummaries`. Rules only ever raise scores.
- **Challenge band stays automatic (D21).** No portal control may disable, delay or gate a mid-band Turnstile prompt; the portal touches only `pending → approved | rejected` through INT-02's endpoints and never pushes to Google/Meta itself.
- **No per-event review screen** — it needs a session-bearing SQL projection that does not exist and must not be improvised by querying the event store. If it is ever built, it is a new task with its own rollup.
- **The D19 label is DAT-07's side effect, not the portal's.** The portal must never reference `ILabelSink`, `LabelEvent` or ClickHouse; emitting a label directly would double-write it (see the warning in `AdminEndpoints.AddWhitelistAsync`).
- **No JavaScript, no npm, no bundler, no CDN asset, no web font** (D1; the D2 esbuild exception is for the tenant-page SDK only). The CSP `script-src 'none'` is the enforcement — if a feature seems to need script, drop the feature.
- **No CORS on the API, ever.** The browser talks only to the portal origin; the portal's server talks to the API. Adding `AddCors` to `TelemetryGuard.Api` would expose `/admin` to arbitrary pages and is out of bounds.
- **Never log, render or persist the raw API key** — only `PortalAuth.Hint()` (last 4). It must never appear in a URL, a query string, a log line or an OTel tag.
- **No tenant switching** and no tenant picker: one key ⇒ one tenant ⇒ RLS. The portal never sees or sends a `TenantId`.
- **Backlog items stay out**: IP-reputation store, per-tenant Turnstile keys, SSO/user accounts, multi-instance worker claiming, elastic-pool tenant isolation, Kusto provider, per-tenant branding/theming, email/alerting, CSV export, campaign/site/tenant *editing* (retention and enforcement-mode changes have repository methods but no admin endpoint — do not add one here), and portal-side scheduled jobs.
- **Deployment is a SEPARATE, NOT-YET-WRITTEN task — explicitly *not* P2-04.** P2-04's scope is frozen at the API image (`TelemetryGuard.Api` + `TelemetryGuard.MigrationRunner` in one container) plus its Bicep; its own guardrails list the portal as out of scope, and its Dockerfile publishes neither this project nor its `wwwroot/`. So this task ships **no** Dockerfile, no compose service, no Bicep, and no CI deploy step, and P2-04 ships nothing that *builds or deploys* the portal — it mentions it only to record the exclusion (a "not deployed by this task" row in its runbook mapping table). The follow-up deployment task owns: a second container image for `TelemetryGuard.Portal`, its own Container App + ingress, TLS termination, a **public domain kept separate from the tracking/pixel domains** (a shared origin would put the portal behind the Cloudflare-only `ipSecurityRestrictions` allow-list meant for the tracker), `Portal:ApiBaseUrl` pointing at the API's internal address, and a **persisted Data Protection key ring** (see below). Until then the portal runs on the host exactly like the API: `dotnet run --project TelemetryGuard.Portal`.
- **The Data Protection key ring is per-process by default**, so a portal restart invalidates every `tg_portal` cookie and all operators sign in again. That is acceptable for this task and must be stated in the runbook-less README/`Program.cs` comment rather than "solved" here with a database or blob key store — persisting it needs a storage decision that belongs to the deployment task.
- Never edit an applied migration; never edit an existing spec decision (append `D24+` only).
