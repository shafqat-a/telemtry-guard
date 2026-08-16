# TelemetryGuard — System Specification & Decision Record

**Version:** 1.1 · **Status:** Agreed baseline for MVP build (D18–D23 added from owner answers)
**Name:** TelemetryGuard (repo/solution: `telemetry-guard`)
**Companion document:** `fraud-signal-feature-spec.md` (detailed signal contract, ML.NET input vector)

---

## 1. Purpose

A multi-tenant SaaS that detects and blocks ad fraud, click fraud, and bot traffic for PPC campaigns (Google Ads, Meta, TikTok) and lead-generation forms. It scores every click/visit 0–100 in real time, enforces allow / challenge / block decisions, and feeds confirmed-fraud sources back into ad-platform exclusion lists so tenants stop paying for fake traffic.

Built almost entirely on open-source components, self-hostable for MVP, with a clean migration path to Azure managed services.

## 2. Goals

1. Detect the four major threat classes: headless/automated browsers, datacenter/proxy/VPN traffic, scripted input behavior, and coordinated click farms (velocity patterns).
2. Score in the request path in < 50 ms so verdicts can gate form submissions and attribution.
3. Catch non-JS bots (a large share of click fraud never executes JavaScript).
4. Multi-tenant from day one, with tenant isolation that does not depend on developer discipline.
5. Cloud-agnostic MVP (everything runs in Docker Compose locally); Azure-ready by design.
6. Swappable analytics backend (ClickHouse ↔ Kusto) via a provider model.

## 3. Non-goals (v1)

- **Browser-extension enumeration.** No browser API exposes an extension list; probing techniques are unreliable under Manifest V3. Excluded entirely rather than shipped as a weak signal.
- **Keystroke content capture.** Timing metadata only, never key values — compliance and liability line.
- **Deep-learning trajectory model (DELBOT/ONNX) in v1.** Three derived trajectory statistics stand in; revisit if insufficient.
- **Generic query abstraction over analytics engines.** We abstract by *intent*, never build a cross-engine query language (see D7).
- **Kafka/Snowplow at MVP.** Direct batched inserts first; streaming infra only when volume demands (see D12).
- **Cross-tenant fingerprint intelligence sharing.** Needs privacy/contractual review first.

---

## 4. System overview — application parts

Six components. The first two are *capture paths*, deliberately redundant: bots that never run JS are still seen by the server-side tracker.

| # | Component | Responsibility |
|---|-----------|----------------|
| 1 | **Client JS SDK** | Embedded snippet on tenant landing pages. Collects mouse/touch/scroll/keystroke *timing* events, FingerprintJS device ID, Botd automation checks, honeypot interactions, first-party storage age. Ships signed beacons via `sendBeacon` (fetch fallback) with a session ID. Tenants who cannot add script tags use a **web-pixel mode** instead (HTTP-only signals — see D22). |
| 2 | **Click/redirect tracker** | Server-side endpoint the ad's destination URL points to (`track.{domain}/c?cid=...`). Logs the click at HTTP level, 302-redirects to the real landing page. Guarantees visibility of non-JS bots and clicks where the page never loads. |
| 3 | **Ingestion API** | Receives beacons + tracker hits. Extracts HTTP-layer signals: IP, full header set and order, User-Agent + Client Hints, Accept-Language, referrer, cookies, gclid/fbclid, TLS fingerprint (when fronted by Cloudflare). Joins beacon data to click data via session ID. |
| 4 | **Enrichment + risk engine** | In-process GeoLite2/IP2Location lookups, Redis velocity counters, derived behavioral features, ML model inference → score 0–100. Stateless, < 50 ms budget. |
| 5 | **Decision/enforcement layer** | Maps score to action: **0–30 allow**, **31–70 challenge (Cloudflare Turnstile)**, **71–100 block + exclude from attribution**. Pushes confirmed-fraud IPs/placements to Google Ads and Meta exclusion lists via their APIs. |
| 6 | **Event store + offline side** | Every raw event and verdict persisted to the analytics store (ClickHouse). Feeds model retraining, false-positive review, publisher aggregates, and dashboards. Real-time path never blocks on it. |

### Two capture-path facts that shaped the design

- **Extensions cannot be enumerated** — inference via web-accessible-resource probing or content-script side effects is fragile (Manifest V3) and is a non-goal.
- **"Cookie age" means our own cookie's age.** The SDK sets a first-party cookie + localStorage entry with a signed timestamp on first visit and reports its age thereafter. A fingerprint seen 40 times that presents brand-new storage every visit is a bot farm wiping state (`storage_age_zero_repeat`, T2 signal).

---

## 5. Technology decisions (with rationale)

Format per decision: what we chose, what we compared it against, why, and the consequences we accepted.

### D1 — Backend language: **.NET (C#) end-to-end**

- **Constraint (from owner):** no scripting technologies server-side — Python and Node excluded. Candidates: .NET, Go, Rust.
- **Why .NET:**
  - Only candidate with a serious *in-language ML story*: **ML.NET trains LightGBM natively**, and **ONNX Runtime for .NET** covers future pretrained models. Go has no credible training libraries; Rust training is immature. Choosing Go/Rust would force training in another ecosystem — reintroducing the thing we excluded.
  - ASP.NET Core (Kestrel) is top-tier in HTTP throughput benchmarks; performance is not a reason to prefer Go here.
  - First-class Azure citizenship (SDKs, Container Apps, App Insights all .NET-native), matching the likely hosting target.
- **Rejected:** Go — fine for tiny network services but weak ML; Rust — best raw performance and solid `ort` inference, but materially slower development velocity and no training story. Rust remains an *option for a hot-path scoring microservice later* if profiling ever justifies it.
- **Consequence:** one language, one toolchain, training and serving share code.

### D2 — Client SDK: **TypeScript + esbuild** (the one unavoidable exception)

- Browsers only execute JavaScript; there is no .NET option client-side. TS compiles to a **static IIFE bundle** — no server-side scripting runtime is introduced.
- Plain TS, no framework: the snippet loads on third-party landing pages and must stay ~20–30 KB gzipped.
- Embeds **FingerprintJS OSS** + **Botd**; custom listeners for input timing, honeypots, storage age. Delivery via `navigator.sendBeacon()` so data survives page unloads.

### D3 — Ingestion framework: **ASP.NET Core 8/9 minimal APIs on Kestrel**

- Ingestion, click tracker, scoring, and decision all start in **one service** — the scoring model runs in-process (no network hop inside the 50 ms budget). Split later only under measured load.
- **MaxMind.GeoIP2** NuGet memory-maps GeoLite2 City + ASN → microsecond lookups in-process; weekly refresh job. IP2Location LITE PX for proxy/VPN flags.

### D4 — ML: **ML.NET LightGBM** for the main model; **ONNX Runtime** as the escape hatch

- Gradient-boosted trees are the right model class for tabular fraud features — they outperform deep nets on this data shape and train well on limited labels.
- Trajectory analysis v1 = hand-computed features (`mouse_path_linearity`, `mean/std_inter_event_ms`) feeding the same GBT — ~80 % of the DELBOT value at ~20 % of the complexity. A true sequence model, if ever needed, arrives as an ONNX artifact served by ONNX Runtime for .NET.

### D5 — Cache/velocity store: **Redis** (protocol as the seam — no provider abstraction)

- Chosen start: **self-hosted Redis in Docker**.
- Redis, Microsoft **Garnet**, **Azure Cache for Redis**, and **Azure Managed Redis** all speak the Redis protocol → one client (`StackExchange.Redis`), switching is a connection-string change. **Building a provider layer here would be dead code.** A thin interface exists only for testability.
- Workload: sliding-window counters, HyperLogLogs, dedupe, challenge tokens, per-tenant quotas.

### D6 — Analytics/event store: **ClickHouse first**, Kusto later, behind a provider model

- **Why ClickHouse first:** keeps MVP cloud-agnostic; entire dev loop runs locally in Docker Compose; best raw price/performance; single-node is sufficient early.
- **Azure alternatives evaluated** (for when hosting moves): 
  - **Azure Data Explorer (Kusto)** — the native-Azure analog: columnar, telemetry-optimized, Event Hubs-native, first-class Grafana/Power BI connectors. Default Azure pick.
  - **Microsoft Fabric Eventhouse** — *same Kusto engine* repackaged in Fabric; choose only if the org standardizes on Fabric. **One Kusto provider covers both** (same .NET SDK, different connection string).
  - **ClickHouse Cloud on Azure Marketplace** — best price/perf, bills through Azure, but a third-party relationship.
  - **Self-hosted ClickHouse on AKS/VM** — cheapest, most ops burden; acceptable MVP-to-mid path.
- **Consequence:** exactly **two provider implementations ever needed: ClickHouse and Kusto.**

### D7 — Provider architecture: **abstract by intent, not by query**

- The seam is a set of narrow interfaces named for what the app asks, answered in each engine's native dialect (KQL vs ClickHouse SQL). **No generic query layer, no LINQ-over-both** — that is a leaky abstraction that consumes months.

```csharp
public interface IEventSink
{
    ValueTask WriteBatchAsync(ReadOnlyMemory<ClickEvent> events, CancellationToken ct);
}

public interface IAnalyticsQueries
{
    Task<IpVelocityStats> GetIpVelocityAsync(string ip, TimeSpan window, CancellationToken ct);
    Task<CampaignFraudReport> GetCampaignReportAsync(string campaignId, DateRange range, CancellationToken ct);
    Task<IReadOnlyList<FlaggedSource>> GetTopFlaggedSourcesAsync(DateRange range, int limit, CancellationToken ct);
}
```

- Config-driven DI switch:

```csharp
// appsettings.json → "Analytics": { "Provider": "ClickHouse" }
cfg["Analytics:Provider"] switch
{
    "Kusto"      => s.AddSingleton<IEventSink, KustoEventSink>()
                     .AddSingleton<IAnalyticsQueries, KustoAnalyticsQueries>(),
    "ClickHouse" => s.AddSingleton<IEventSink, ClickHouseEventSink>()
                     .AddSingleton<IAnalyticsQueries, ClickHouseAnalyticsQueries>(),
    var p => throw new InvalidOperationException($"Unknown analytics provider '{p}'")
};
```

- Projects: `TelemetryGuard.Analytics.Abstractions` (interfaces + DTOs only), `TelemetryGuard.Analytics.ClickHouse`, `TelemetryGuard.Analytics.Kusto` (later). **Each provider owns its own schema/migration scripts** (ClickHouse DDL vs `.kql`) — schema management is where single-abstraction fantasies die.
- **Accepted costs (eyes open):**
  1. *Double surface* — every report touches two implementations once Kusto exists. Mitigation: a **shared contract-test suite** run against both (ClickHouse via Testcontainers, Kusto via Microsoft's free emulator container). Without contract tests the second provider silently rots.
  2. *Lowest-common-denominator pressure* — Kusto update policies vs ClickHouse materialized views are incompatible; engine-native features are reimplemented per provider, deliberately.
  3. *Ingestion semantics differ* — Kusto favors queued/batched (seconds), ClickHouse async inserts (near-real-time). `IEventSink` therefore promises only the **weaker guarantee: eventual, batched** — both can honor it.
- **Resolution: define the seam now, implement ClickHouse only.** Add the Kusto implementation when a real deployment demands it, with contract tests already waiting. The interface is cheap; the second implementation and its CI matrix are the expensive part.

### D8 — OLTP/summary database: **SQL Server** (Azure SQL in prod, mssql container locally)

- Owns the small relational data: tenants, users, API keys, campaign config, scoring rules, verdict summaries, exclusion-sync state.
- **Why:** same engine/T-SQL surface between the `mcr.microsoft.com/mssql/server` container and Azure SQL — code runs unchanged; most natural .NET pairing; owner preference. Azure SQL **serverless tier** (auto-pause, per-second billing) keeps early cost near zero.
- **Honest caveat kept on record:** Azure SQL is the priciest managed relational option per compute unit; PostgreSQL Flexible Server does everything needed (incl. RLS) for less. Decision stands on stack coherence — **confirmed and closed by D23** (SQL Server = summaries/breakdowns, ClickHouse = raw events; PostgreSQL fallback dropped).

### D9 — Data access: **Dapper** (over EF Core)

- **Why:** the ClickHouse provider was always going to be hand-written SQL behind intent-based interfaces; Dapper makes the *entire* data layer one pattern — explicit SQL behind narrow repositories — instead of ORM-here/raw-SQL-there. Dapper is extension methods over `Microsoft.Data.SqlClient`, identical against container and Azure SQL.
- **What was given up, and the replacement for each:**
  1. **EF global query filters** (automatic `TenantId` scoping) — the big one. Replacement: **promote SQL Server Row-Level Security from backstop to primary enforcement** (see D11). A forgotten `WHERE TenantId=…` now returns *empty*, not another tenant's rows — a bug, not a breach.
  2. **EF migrations** → versioned SQL scripts run by **DbUp or Grate** at deploy (see D10).
  3. **Change tracking / LINQ** → barely missed for this workload (config CRUD, verdict inserts). Cost is tedium: hand-written statements, rename-by-grep. Mitigation: repository-per-aggregate (`CampaignRepository`, `TenantRepository`, `VerdictRepository`) behind intent-named interfaces.
  4. **Compile-time query checking** → integration-test pass: Testcontainers spins up SQL Server, DbUp applies migrations, every repository method executes once. Same harness pattern as the ClickHouse contract tests.
- **Explicitly rejected: the EF-for-writes/Dapper-for-reads hybrid** — two mapping conventions and two tenant-enforcement mechanisms for a small team is worse than either pure choice.

### D10 — Migrations: **DbUp/Grate numbered SQL scripts**, per store

- ClickHouse already forces script-based schema management; SQL Server now migrates the same way. RLS policies, security predicates, and indexes are first-class citizens in scripts rather than `migrationBuilder.Sql()` blobs. One runner per store, executed in CI/CD.

### D11 — Multi-tenancy: **shared database, shared schema, `TenantId` on every row**

- **Why this model:** cheapest to operate; right for many small advertiser tenants; Azure SQL is built for it. Database-per-tenant on day one is operational overkill.
- **Enforcement layers (post-Dapper):**
  1. **RLS as primary** — security policy keyed on `SESSION_CONTEXT('TenantId')`; even raw SQL, Grafana connections, or future bugs cannot leak cross-tenant rows.
  2. **Tenant-bound connections only** — repositories can only obtain connections from a factory that stamps the session context; there is no path to an unscoped connection:

```csharp
public sealed class TenantConnectionFactory(ITenantContext tenant, IConfiguration cfg)
    : ITenantConnectionFactory
{
    public async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(cfg.GetConnectionString("Main"));
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(
            "EXEC sp_set_session_context @key = N'TenantId', @value = @tid, @read_only = 1",
            new { tid = tenant.TenantId });
        return conn;
    }
}
```

  3. **Explicit `TenantId` in WHERE clauses anyway** — for index seeks and readability; correctness no longer depends on it.
  4. **Composite keys/indexes leading with `TenantId`** — `(TenantId, CampaignId)`; uniqueness is per-tenant, lookups partition naturally.
- **Tenant resolution:** `TenantResolutionMiddleware` early in the pipeline — API key (dashboard/API) or the site key embedded in the JS snippet (beacon/click traffic) — populates a scoped `ITenantContext` consumed by SQL, Redis, and analytics layers alike.
- **Tenancy beyond SQL Server:**
  - **ClickHouse:** `tenant_id` is the *first* column of every table's `ORDER BY` (e.g., `ORDER BY (tenant_id, timestamp)`) for aggressive pruning. `IAnalyticsQueries` implementations take tenant from `ITenantContext` and inject it into every query — never an optional parameter.
  - **Redis:** every key prefixed `t:{tenantId}:…`; doubles as the seat of per-tenant rate limits and quotas.
- **Escape hatch:** a whale tenant with compliance demands → **Azure SQL elastic pools** make database-per-tenant economical *without redesign*, provided connection logic stays centralized behind `ITenantContext`/the factory (it does). Design for the hybrid, implement the shared model.

### D12 — Eventing: **no broker at MVP**

- FastPath: ingestion service writes to ClickHouse via **async batched inserts** (Redis Streams as an optional buffer). Kafka-class infra deferred.
- When volume demands it: **Event Hubs (Kafka-protocol endpoint)** on Azure or **Redpanda** self-hosted — both consumed by the same Confluent .NET client, so the upgrade is config + a consumer, not a rewrite. The protocol is the seam; no provider model needed.

### D13 — Edge & TLS fingerprinting: **Cloudflare in front (even in front of Azure)**

- **Why:** Azure Front Door does **not** pass JA3/JA4 TLS fingerprints to origin. Cloudflare (free tier upward) hands JA3/JA4, bot scores, and ASN in request headers — powering the T1 `tls_ua_mismatch` signal ("UA says Chrome, TLS handshake says Go binary").
- Alternatives if Cloudflare is unacceptable: terminate TLS on AKS with a fingerprinting-capable proxy (self-managed), or forgo the signal (it degrades to NaN — the model tolerates it).
- Azure Front Door Premium's bot-manager ruleset can still be layered as an extra cheap signal when hosting there.

### D14 — Challenge: **Cloudflare Turnstile**

- Free, invisible-first, far less user friction than reCAPTCHA; hosting-agnostic (JS widget + server-side verify from C#). Serves the 31–70 challenge band.

### D15 — Ad-platform enforcement

- **Google Ads:** official `Google.Ads.GoogleAds` .NET client → IP exclusion lists / placement exclusions.
- **Meta:** no official .NET SDK → thin typed `HttpClient` wrapper over the Marketing API (kept deliberately small).

### D16 — Hosting & ops

- **Local/MVP:** Docker Compose — ASP.NET service, Redis, ClickHouse, SQL Server, Grafana. Runs on one 8 GB VPS at early volume.
- **Azure path:** Container Apps first (scale-to-zero, simple) → AKS if outgrown; Front Door + WAF; **Application Insights via OpenTelemetry** (first-class in .NET); Key Vault for secrets; Azure Managed Grafana.

### D17 — Dashboards

- **Grafana on ClickHouse** for MVP — internal + early-tenant reporting in an afternoon.
- Customer-facing portal later: ASP.NET Core API + Blazor, or a static SPA (front-end is unavoidably JS/TS, same exception class as D2). Deferred until productization demands it.

### D18 — Model cold start: **rules-first launch; model trained on post-launch live data** *(resolves OQ1)*

- **Owner input:** a small real-traffic dataset becomes available only *after* the solution is built — no pre-existing labeled data.
- **Therefore MVP ships without a trained model:** deterministic T1 rules + a transparent weighted-heuristic scorer produce the 0–100 score at launch. The scorer sits behind an `IScorer` interface so heuristic → LightGBM is a config swap, not a rewrite.
- **Label pipeline once live traffic flows:** T1 rule hits = weak fraud positives; Playwright-generated synthetic bot runs = guaranteed positives; tenant-confirmed conversions and review-screen "real customer" marks (D19) = negatives. The pilot dataset runs in **listen-only mode** (scored and logged, model not enforcing) before the first trained model takes over.
- **Consequence:** every verdict is stamped with `scorer_version` so heuristic-era data is never confused with model-era data in training or reporting.

### D19 — Human oversight: **automatic decisions, tenant override always possible** *(resolves OQ2)*

- **Owner input:** both options — auto with human intervention possible.
- The system acts on scores autonomously; every challenged/blocked event is visible in a tenant-facing review screen where marking "this was a real customer" reverses the action (whitelists the source) *and* writes a negative label to the training store — the override loop doubles as the labeling loop.
- Until the portal ships (Phase 2), internal Grafana views + a manual whitelist API cover the override path.

### D20 — Data retention: **per-tenant configurable, 30–180 days** *(resolves OQ3)*

- Each tenant sets raw-event retention between 1 and 6 months; **proposed default 90 days**.
- **Implementation:** `retention_days` is denormalized onto every ClickHouse row at ingest from tenant config; the table TTL expression `timestamp + toIntervalDay(retention_days)` gives per-row expiry — one table serves all tenants, no per-tenant tables needed. (The future Kusto provider implements the same policy via its own retention mechanism — a provider-specific concern, per D7.)
- SQL Server summaries/breakdowns (D23) contain aggregates, not behavioral telemetry, and may outlive the raw window. Exclusion-list entries retain IPs for as long as the exclusion is active (operationally necessary).

### D21 — Enforcement autonomy: **a per-tenant setting** *(resolves OQ4)*

- **Owner input:** "we can punish — or let human decide — it should be a setting."
- Per-tenant `EnforcementMode`: **`AutoEnforce`** (default) — block band and exclusion-list pushes execute automatically; **`ApprovalQueue`** — those actions queue for tenant approval instead. The 31–70 challenge band is always automatic in both modes (a Turnstile prompt is low-harm).
- The setting lives in tenant/campaign config (SQL Server) and is read by the decision layer and the exclusion-sync job.

### D22 — Tenant integration: **JS section or web pixel, served from our CDN** *(resolves OQ5)*

- **Owner input:** tenants can include a web pixel *or* a JS section.
- Two modes: **(a) full JS snippet** `<script src="https://cdn.../tg.js" data-site-key="…">` — behavioral + fingerprint + HTTP signals; **(b) web pixel** `<img src="https://px.../p.gif?k=…">` — HTTP-layer + velocity signals only, for surfaces where script tags are impossible (locked-down site builders, some landing-page tools).
- Pixel mode degrades gracefully *by existing design*: all SDK features are `NaN` (Section 7 null semantics) and scoring proceeds on network + velocity tiers. Dashboards must display each site's integration level so tenants understand their detection strength.
- Delivery default: **latest from CDN** (detection fixes propagate instantly when bots adapt); a version-pinned URL is offered for tenants requiring change control.

### D23 — Data split confirmed: **SQL Server = summaries & breakdowns; ClickHouse = raw clicks** *(resolves OQ6 / closes the D8 checkpoint)*

- **Owner confirmation:** summary and breakdown data go to SQL Server; actual click/event rows stay in ClickHouse.
- Scheduled rollup jobs materialize per-tenant/per-campaign/per-day breakdowns from ClickHouse into SQL Server, so portals and APIs read small, fast, RLS-protected aggregate tables and never query the event store directly.
- PostgreSQL is dropped from consideration; SQL Server is final for the relational tier.

### D24 — IP intelligence: **behind a provider model, `iplegence` by default** *(amends D3's enrichment sketch)*

- **Owner input:** use `iplegence` (github.com/shafqat-a/iplegence) — an in-house Go pipeline that merges free/open IP intelligence (IPinfo Lite, sapics, iptoasn, GeoLite2/DB-IP city data, OpenProxyDB, Tor exit lists, iCloud Private Relay, and the official AWS/GCP/Azure/Cloudflare ranges) into **one MaxMind-compatible `Superior-IP.mmdb`**, published as a daily `vYYYY.MM.DD` release.
- **The seam:** `IIpIntelligenceProvider` — one narrow, intent-named method (`Lookup(IPAddress) → IpEnrichment`) implemented once per dataset, selected by `IpEnrichment:Provider`. Same shape as the analytics provider switch (D7): unknown value aborts startup. `IpEnrichmentService` keeps only what is dataset-independent (parsing, IPv4-in-IPv6 unmapping, private/loopback short-circuit) and nothing above the seam knows which dataset answered.
  - **`Iplegence`** (default) — one memory-mapped `Superior-IP.mmdb`.
  - **`MaxMind`** — RSK-02's original GeoLite2 City + GeoLite2 ASN + IP2Proxy LITE PX trio, kept as a one-line rollback.
- **Why in-process file, not iplegence's HTTP API** (it ships a server and a Docker image): the scoring path has a < 50 ms budget (D3) and enrichment runs on every click and beacon. A per-request network hop would force a fail-open/fail-closed decision on every timeout — fail-open lets fraud through, fail-closed blocks real users. A memory-mapped file has no such state: the data is there or it is null, and null already degrades correctly (D13). It also keeps a Go runtime out of the .NET-only backend (D1) — iplegence is a **build-time data producer**, never a runtime dependency.
- **Field mapping** (deliberate, and the reason the seam returns `IpEnrichment` rather than a raw record): `traits.usage_type` (see below) drives `AsnType`, falling back to `is_cdn` → `Cdn`, `is_hosting_provider` → `Datacenter`, then the embedded datacenter-ASN seed; `is_anonymous_vpn | is_public_proxy | is_tor_exit_node | is_anonymous` → `IsProxyOrVpn`; `is_tor_exit_node` → `IsTor`; `is_relay` → `IsPrivateRelay`. `IsDatacenter` is **derived from the resolved `AsnType`** (`Datacenter` or `Cdn`) rather than recomputed, so the classification and the flag can never contradict each other — in particular a prefix typed residential/mobile/education is not reported as a datacenter merely because its ASN appears in the hand-maintained seed. Hosting deliberately does **not** feed `IsProxyOrVpn` — it already has its own feature and its own paid-click T1 rule, and double-counting one fact would inflate scores. (The MaxMind provider's IP2Proxy mapping does count `DCH` as a proxy type; that difference is intentional.)
- **Null semantics are preserved end to end.** No database, or an address the build does not cover, yields null flags — *unknown*, not *clean*. A covered row yields real booleans, because iplegence omits false traits at write time: an absent trait means no source asserted it.
- **`asn_type` fidelity — resolved by `traits.usage_type`** (iplegence release 2026-08-15). The dataset now infers a coarse type — `residential` / `mobile` / `business` / `education` / `government` / `hosting` — from PeeringDB network types, ASN-name keywords and prefix flags, with `usage_type_source` recording which rule fired. It maps 1:1 onto `AsnType`, so `Mobile` (the classification that explains carrier-grade NAT when normalizing `device_ids_this_ip_hour`) is expressible again without IP2Proxy. Two limits, both accounted for: it is **inferred, not a commercial usage_type feed** — PeeringDB entries are self-declared and the ASN-name rules are keyword heuristics; and it covers roughly **45% of the routable IPv4 space** (measured over a 300k-address sample: residential 24%, hosting 10%, education 5%, business 3%, mobile 2%, government 1%). Untyped rows fall through to the flags and the seed and otherwise stay `Unknown` — never guessed. The composite-provider fallback (layering IP2Proxy `usage_type` over iplegence) stays available behind the seam but is no longer planned; revisit only if the inferred type proves unreliable against live data.
- **Distribution:** `scripts/update-iplegence.sh` fetches the release asset (or a URL, or a local `dist/`), verifies the published sha256, and atomically moves it into the geo data directory; the provider hot-swaps on mtime, so refreshes need no restart or redeploy. The generated `ATTRIBUTION.md` travels with the data — **source credits are never stripped**, and this is a merged free/open dataset, never described as commercial-grade.

---

## 6. Request flows

### 6.1 Ad click (paid traffic)

1. Ad destination URL → **click tracker** `GET /c?cid=…&gclid=…` — logs HTTP-layer signals, validates/dedupes `gclid` (Redis `SETNX`), 302 → landing page.
2. Landing page loads **SDK** → beacons stream to **ingestion** with session ID.
3. Risk engine joins tracker hit + beacons, enriches, computes features, scores.
4. No beacon within grace period (~10 s) → `has_js_beacon=0` → scored on HTTP-layer + velocity features alone (this is the non-JS-bot path, by design).
5. Verdict persisted (SQL summary + ClickHouse event); 71–100 → excluded from attribution and queued for exclusion-list sync.

### 6.2 Lead form

1. SDK monitors form (fill time, honeypots, paste/keystroke timing).
2. On submit, the page calls the **decision endpoint** with session ID → allow / challenge (Turnstile token round-trip) / block before the lead is accepted.

### 6.3 Scoring bands

| Score | Action |
|-------|--------|
| 0–30 | Allow; count conversion / valid click |
| 31–70 | Challenge (Turnstile); re-score with challenge outcome |
| 71–100 | Block; exclude from PPC attribution; feed exclusion sync |

Deterministic T1 rules may **raise** a score floor pre-model; rules never lower a score.

---

## 7. Signal & feature specification (condensed)

Full contract — tables with per-signal FP conditions, Redis key patterns, and the `FraudFeatureVector` C# record — lives in **`fraud-signal-feature-spec.md`** (`feature_set_version = 1`). The essentials:

**Tiers:** T1 = near-deterministic when it fires (rule-eligible; absence proves nothing) · T2 = strong model feature · T3 = weak/supporting, never a rule · CTX = context/conditioning only.

**Null semantics:** missing ≠ zero. No-beacon sessions carry `NaN` for SDK features (LightGBM branches on NaN natively); velocity counters are legitimately 0 when cold; `has_js_beacon` is itself a T2 feature.

**T1 (rule-eligible):** `honeypot_touched` (≥95), `click_before_render` / `pointer_untrusted` (≥90), `webdriver_flag` / `headless_browser` (≥85), `beacon_integrity_ok=false` (≥85), `ip_tor` paid-only (≥80), `tls_ua_mismatch` (≥80), `ip_datacenter_asn` on paid click (≥70 → challenge band), high `ip_clicks_last_min`, `ua_os_mismatch`, near-1.0 `mouse_path_linearity`, near-0 `std_inter_event_ms`, `click_id_invalid` (missing/replayed gclid/fbclid).

**T2 (strong):** `emulator_or_vm`, `screen_res_anomalous`, `storage_age_zero_repeat`, `ip_proxy_or_vpn` (Private Relay carved out as CTX), `ip_geo_target_mismatch`, `time_on_page_sec`, `form_fill_time_sec` (conditioned on `autofill_detected`), `first_interaction_delay_ms`, `input_modality_mismatch`, `device_sessions_last_hour`, `ip_distinct_uas_last_hour`, `device_ids_this_ip_hour` (**normalize by `asn_type` — carrier-grade NAT**), publisher aggregates (Phase 2).

**T3 (never rules — privacy-tool and edge-case FPs):** `cookies_disabled`, `canvas_fp_blocked` (Brave/Firefox by design), `timezone_ip_mismatch`, `language_geo_mismatch`, `ip_reputation_bad` (decays), `paste_in_identity_fields` (password managers), `referrer_missing` (superseded by `click_id_invalid`), raw event counts, `scroll_events`, `pages_viewed`.

**CTX:** `is_mobile`, `asn_type` (one-hot), `is_private_relay`, `form_submitted`, `autofill_detected`.

**Additions agreed beyond the original list:** `tls_ua_mismatch`, `storage_age_zero_repeat`, `asn_type`, `pointer_untrusted`, `input_modality_mismatch`, `first_interaction_delay_ms`, `click_id_invalid`, `has_js_beacon` + `beacon_integrity_ok`.

**Architectural implication:** the SDK ships *raw events*; the risk engine computes derived features (linearity, inter-event stats, all velocity, publisher ratios). Keeps the snippet dumb and lets features evolve without redeploying tenant pages.

---

## 8. Solution shape (agreed so far)

```
TelemetryGuard.sln
├── TelemetryGuard.Api                     ASP.NET Core: tracker, ingestion, decision, tenant middleware
├── TelemetryGuard.RiskEngine              feature extraction, rules, ML.NET/ONNX scoring
├── TelemetryGuard.RiskEngine.Contracts    FraudFeatureVector, verdict DTOs
├── TelemetryGuard.Analytics.Abstractions  IEventSink, IAnalyticsQueries, DTOs
├── TelemetryGuard.Analytics.ClickHouse    provider impl + /schema/*.sql
├── TelemetryGuard.Analytics.Kusto         (deferred) provider impl + /schema/*.kql
├── TelemetryGuard.Data                    Dapper repositories, TenantConnectionFactory, /migrations/*.sql (DbUp)
├── TelemetryGuard.Integrations            Google Ads client, Meta HttpClient wrapper, Turnstile verify
├── TelemetryGuard.Sdk                     TypeScript source + esbuild → static bundle artifact
└── tests/
    ├── TelemetryGuard.Tests.Contracts     shared analytics contract suite (runs per provider)
    ├── TelemetryGuard.Tests.Integration   Testcontainers: SQL Server + ClickHouse + Redis
    └── TelemetryGuard.Tests.Unit
```

*(Detailed folder layout, Compose file, and CI matrix are the next deliverable.)*

## 9. Testing strategy

- **Analytics contract tests:** one xUnit suite asserting `IAnalyticsQueries`/`IEventSink` behavior, executed against ClickHouse (Testcontainers) now and the Kusto emulator container when that provider lands. This is the guardrail that makes D7 safe.
- **Repository integration tests:** Testcontainers SQL Server + DbUp migrations + one execution of every Dapper method — the replacement for EF's compile-time checking, and the proof that RLS policies behave (include a deliberate cross-tenant read that must return empty).
- **SDK:** Playwright-driven fixture pages asserting beacon shape — and doubling as a source of *known-bot* telemetry for early model labels.

## 10. Phasing

| Phase | Contents |
|-------|----------|
| **1 — MVP** | SDK + web-pixel mode, tracker, ingestion, enrichment, **T1 rules + heuristic scorer behind `IScorer` (no trained model yet — D18)**, Redis velocity, ClickHouse events with per-tenant TTL (D20), SQL Server tenancy (RLS) + summary rollups (D23), Turnstile, Grafana, manual whitelist API. Docker Compose deploy. |
| **1.5** | Cloudflare fronting → `tls_ua_mismatch` live; Google Ads exclusion sync + Meta wrapper honoring `EnforcementMode` (D21); **pilot live dataset in listen-only; first LightGBM model trained and swapped in via `IScorer`**. |
| **2** | Publisher aggregates from ClickHouse history; retraining loop fed by review-screen labels (D19); customer portal incl. review/override screen and integration-level display; Azure Container Apps deployment. |
| **Later, on demand** | Kusto provider + contract-test matrix; Kafka-protocol streaming; ONNX trajectory model; elastic-pool tenant isolation for whales. |

## 11. Question resolutions & remaining defaults

The six open questions of v1.0 are resolved as decisions **D18–D23** (cold start, oversight, retention, enforcement mode, integration modes, data split).

Small defaults still to pin during the build (none block starting):

1. Default retention value within the 30–180 d range (proposed: 90 d).
2. Exact auto-enforce thresholds per band and per action under `AutoEnforce`.
3. CDN + pixel endpoint domains and naming.
4. Length of the listen-only window for the pilot dataset (proposed: 2–4 weeks).
5. Whether the review/override screen advances from Phase 2 into 1.5 if tenant demand appears early.

---

*End of specification v1.1 — D1–D17 baseline plus owner resolutions D18–D23. Amend via numbered decision entries (D24+) rather than editing history.*
