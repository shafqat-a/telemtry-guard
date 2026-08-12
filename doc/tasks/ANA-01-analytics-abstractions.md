---
id: ANA-01
title: Analytics abstractions (IEventSink, IAnalyticsQueries, ClickEvent)
phase: 1
workstream: analytics
depends_on: [FND-01, FND-04]
size: L
spec_refs: [D6, D7, D11, D18, D19, D20, D22, "section 7 (null semantics)", "section 8"]
detail_level: full
---

# ANA-01: Analytics abstractions (IEventSink, IAnalyticsQueries, ClickEvent)

## Objective

Create the `TelemetryGuard.Analytics.Abstractions` project: the narrow, intent-named seam between the application and any analytics engine (ClickHouse now, Kusto later). It contains ONLY contracts — interfaces, records, enums, and string constants. No engine SDK references, no SQL, no implementation logic. Every other analytics task (ANA-02..07) and the API/risk workstreams compile against this project.

## Spec context (self-contained)

- **D7 — abstract by intent, not by query.** The seam is a set of narrow interfaces named for what the app asks; each engine answers in its own native dialect. NEVER build a generic query layer, LINQ-over-both, or an expression translator. Exactly two provider implementations will ever exist: ClickHouse (now) and Kusto (later).
- **D7 — weak delivery guarantee.** `IEventSink` promises only *eventual, batched* delivery (Kusto favors queued ingestion measured in seconds; ClickHouse uses async batched inserts). Do not add any interface member that implies synchronous/immediate visibility.
- **D11 — tenancy.** `tenant_id` is never an optional parameter. `IAnalyticsQueries` implementations take the tenant from `ITenantContext` (scoped, from FND-04) and inject it into every query — the interface methods deliberately have NO tenant parameter. `ClickEvent` itself carries `TenantId` explicitly because the sink is a singleton background writer with no ambient request context.
- **Section 7 — null semantics: missing ≠ zero.** SDK-derived numeric features on no-beacon sessions (pixel mode, non-JS bots) are `float.NaN`, never 0. Absent boolean SDK flags are `null` (`bool?`), never `false`. Velocity counters are plain integers because 0 is a legitimate cold value for them. `has_js_beacon` is a real field (and a T2 model feature), not just a gate.
- **D18/D19 — labels.** The label loop (T1 rule hits = weak positives, synthetic Playwright bots = guaranteed positives, confirmed conversions / review-screen "real customer" marks = negatives) needs a write path to the analytics store: `ILabelSink`. Every verdict is stamped with `scorer_version` and `feature_set_version` so heuristic-era data is never confused with model-era data.
- **D20 — retention.** Per-tenant raw-event retention (30–180 days, default 90) is denormalized onto every event row at ingest as `retention_days`; the storage engine applies per-row TTL from it.
- **D22 — pixel mode** exists by design: a `ClickEvent` with `Kind = Pixel` has all SDK fields at their absent values (NaN / null) and `HasJsBeacon = false`.
- **RSK-01 boundary:** `FraudFeatureVector` (in `TelemetryGuard.RiskEngine.Contracts`, task RSK-01) is the DERIVED model input. `ClickEvent` is the RAW capture + verdict storage record. They overlap in concept but MUST NOT be merged, unified, or made to share a base type. Do not reference RiskEngine projects from this project.

## Prerequisites

- FND-01 created `TelemetryGuard.sln` with all projects at the **repo root** (no `src/` folder) and tests under `tests/`. `TelemetryGuard.Analytics.Abstractions/` already exists with a reference to `TelemetryGuard.Core` — verify rather than create.
- FND-04 created the primitives in `TelemetryGuard.Core` (see `doc/tasks/FND-04-core-primitives.md`):
  - `TelemetryGuard.Core.Tenancy.TenantId` — `readonly record struct TenantId(Guid Value)` with `IsEmpty`, `Parse`, `TryParse`;
  - `TelemetryGuard.Core.Tenancy.ITenantContext` — `TenantId TenantId { get; }`, `string? SiteKey { get; }`, `bool IsResolved { get; }` (reads throw `TenantNotResolvedException` before resolution);
  - `TelemetryGuard.Core.Time.IClock` — `DateTimeOffset UtcNow { get; }`.

## Implementation steps

1. **Project file.** Verify `TelemetryGuard.Analytics.Abstractions/TelemetryGuard.Analytics.Abstractions.csproj` exists (FND-01 scaffold; net8.0, Nullable/ImplicitUsings via the root `Directory.Build.props`) and references `../TelemetryGuard.Core/TelemetryGuard.Core.csproj`. No NuGet packages — the only project reference is `TelemetryGuard.Core` (for `TenantId`).

2. **`EventKind.cs`** — namespace `TelemetryGuard.Analytics.Abstractions` for this and all files below:
   ```csharp
   public enum EventKind { Tracker = 0, Pixel = 1, Beacon = 2, Verdict = 3 }

   public static class EventKindWire
   {
       public static string ToWire(this EventKind kind) => kind switch
       {
           EventKind.Tracker => "tracker",
           EventKind.Pixel   => "pixel",
           EventKind.Beacon  => "beacon",
           EventKind.Verdict => "verdict",
           _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
       };
   }
   ```
   The wire strings `tracker|pixel|beacon|verdict` are the storage contract every provider must use.

3. **`VerdictConstants.cs`** — canonical band/action strings (storage contract):
   ```csharp
   public static class VerdictBands
   {
       public const string Allow = "allow";        // score 0–30
       public const string Challenge = "challenge"; // score 31–70
       public const string Block = "block";         // score 71–100
   }
   ```

4. **`ClickEvent.cs`** — the raw event + verdict record. Every field below is REQUIRED to exist with exactly this name and type (ANA-02's ClickHouse DDL and ANA-03's bulk-copy column mapping mirror it one-to-one):
   ```csharp
   using TelemetryGuard.Core.Tenancy;

   namespace TelemetryGuard.Analytics.Abstractions;

   /// <summary>
   /// RAW capture + verdict storage record. NOT the model input —
   /// FraudFeatureVector (RSK-01) is derived from this; never merge the two.
   /// Null semantics (spec §7): SDK float = NaN when absent; SDK bool? = null when
   /// absent; velocity ints are 0 when legitimately cold (never NaN).
   /// </summary>
   public sealed record ClickEvent
   {
       // ---- identity ----
       public required TenantId TenantId { get; init; }
       public required string SiteKey { get; init; }
       public required string SessionId { get; init; }
       public required EventKind Kind { get; init; }

       // ---- click ids (one field per supported ad platform — API-02 extracts all four) ----
       public string CampaignId { get; init; } = "";
       public string Gclid { get; init; } = "";
       public string Fbclid { get; init; } = "";
       public string Msclkid { get; init; } = "";         // Microsoft Ads
       public string Ttclid { get; init; } = "";          // TikTok Ads
       public bool? ClickIdInvalid { get; init; }        // null = not applicable (organic)

       // ---- HTTP layer ----
       public required string Ip { get; init; }           // textual IPv4 or IPv6
       public IReadOnlyList<string> HeaderNames { get; init; } = Array.Empty<string>(); // ordered as received
       public string? UserAgent { get; init; }
       public string? SecChUa { get; init; }
       public string? SecChUaMobile { get; init; }
       public string? SecChUaPlatform { get; init; }
       public string? AcceptLanguage { get; init; }
       public string? Referrer { get; init; }
       public string? TlsJa3 { get; init; }               // null unless Cloudflare-fronted (D13)
       public string? TlsJa4 { get; init; }
       public uint? CfAsn { get; init; }                  // ASN as reported by Cloudflare header

       // ---- enrichment snapshot (null = lookup unavailable/failed) ----
       public string? Country { get; init; }              // ISO 3166-1 alpha-2
       public uint? Asn { get; init; }
       public string? AsnOrg { get; init; }
       public string? AsnType { get; init; }              // e.g. "hosting"|"isp"|"business"|"education"|"unknown"
       public bool? IsDatacenter { get; init; }
       public bool? IsProxy { get; init; }
       public bool? IsVpn { get; init; }
       public bool? IsTor { get; init; }
       public bool? IsPrivateRelay { get; init; }

       // ---- SDK summary (absent => NaN / null; pixel mode & non-JS bots hit this path) ----
       public required bool HasJsBeacon { get; init; }
       public bool? BeaconIntegrityOk { get; init; }
       public string? FingerprintVisitorId { get; init; }
       public float StorageAgeSec { get; init; } = float.NaN;
       public bool? WebdriverFlag { get; init; }
       public bool? HeadlessBrowser { get; init; }
       public float ScreenWidth { get; init; } = float.NaN;
       public float ScreenHeight { get; init; } = float.NaN;
       public string? Timezone { get; init; }             // IANA name, e.g. "Europe/Stockholm"
       public string? Language { get; init; }             // navigator.language
       public float MouseEventCount { get; init; } = float.NaN;
       public float KeyEventCount { get; init; } = float.NaN;
       public float TouchEventCount { get; init; } = float.NaN;
       public float ScrollEventCount { get; init; } = float.NaN;
       public float MeanInterEventMs { get; init; } = float.NaN;
       public float StdInterEventMs { get; init; } = float.NaN;
       public float MousePathLinearity { get; init; } = float.NaN;   // ~1.0 = scripted straight line
       public float FirstInteractionDelayMs { get; init; } = float.NaN;
       public float FormFillTimeSec { get; init; } = float.NaN;
       public bool? AutofillDetected { get; init; }
       public bool? PasteInIdentityFields { get; init; }
       public bool? HoneypotTouched { get; init; }
       public bool? PointerUntrusted { get; init; }
       public bool? InputModalityMismatch { get; init; }
       public float TimeOnPageSec { get; init; } = float.NaN;
       public float PagesViewed { get; init; } = float.NaN;

       // ---- velocity snapshot (server-computed at scoring time; 0 = cold, never NaN) ----
       public int IpClicksLastMin { get; init; }
       public int IpDistinctUasLastHour { get; init; }
       public int DeviceSessionsLastHour { get; init; }
       public int DeviceIdsThisIpHour { get; init; }

       // ---- verdict block (null/empty until Kind == Verdict) ----
       public int? Score { get; init; }                   // 0–100
       public string? Band { get; init; }                 // VerdictBands constants
       public string? Action { get; init; }               // enforced outcome; may differ from Band (whitelist, ApprovalQueue)
       public IReadOnlyList<string> RuleHits { get; init; } = Array.Empty<string>(); // T1 rule names that fired
       public string? ScorerVersion { get; init; }        // e.g. "heuristic-1" (D18)
       public int? FeatureSetVersion { get; init; }       // 1 for the MVP contract

       // ---- storage control ----
       public required ushort RetentionDays { get; init; } // denormalized from tenant config at ingest (D20), 30–180
       public required DateTime TimestampUtc { get; init; } // must be DateTimeKind.Utc
   }
   ```

5. **`IEventSink.cs`** — verbatim from spec D7:
   ```csharp
   public interface IEventSink
   {
       ValueTask WriteBatchAsync(ReadOnlyMemory<ClickEvent> events, CancellationToken ct);
   }
   ```
   XML-doc it with the guarantee: *eventual, batched; implementations must never block the caller on storage I/O; delivery is best-effort under backpressure.*

6. **`IAnalyticsQueries.cs`** — verbatim from spec D7 (note: NO tenant parameter anywhere; implementations read `ITenantContext`):
   ```csharp
   public interface IAnalyticsQueries
   {
       Task<IpVelocityStats> GetIpVelocityAsync(string ip, TimeSpan window, CancellationToken ct);
       Task<CampaignFraudReport> GetCampaignReportAsync(string campaignId, DateRange range, CancellationToken ct);
       Task<IReadOnlyList<FlaggedSource>> GetTopFlaggedSourcesAsync(DateRange range, int limit, CancellationToken ct);
   }
   ```

7. **`ILabelSink.cs`** + **`LabelEvent.cs`** (D18/D19 label loop):
   ```csharp
   public interface ILabelSink
   {
       ValueTask WriteAsync(LabelEvent label, CancellationToken ct);
   }

   public sealed record LabelEvent(
       TenantId TenantId,
       string SessionId,
       string Label,          // LabelValues constants
       string LabelSource,    // LabelSources constants
       DateTime CreatedAtUtc);

   public static class LabelValues
   {
       public const string Fraud = "fraud";
       public const string Legit = "legit";
   }

   public static class LabelSources
   {
       public const string T1Rule = "t1_rule";
       public const string SyntheticBot = "synthetic_bot";
       public const string Conversion = "conversion";
       public const string ReviewScreen = "review_screen";
   }
   ```

8. **`DateRange.cs`** — half-open UTC interval `[FromUtc, ToUtc)`:
   ```csharp
   public sealed record DateRange
   {
       public DateRange(DateTime fromUtc, DateTime toUtc)
       {
           if (fromUtc.Kind != DateTimeKind.Utc || toUtc.Kind != DateTimeKind.Utc)
               throw new ArgumentException("DateRange bounds must be DateTimeKind.Utc.");
           if (toUtc < fromUtc)
               throw new ArgumentException("ToUtc must be >= FromUtc.");
           FromUtc = fromUtc;
           ToUtc = toUtc;
       }

       public DateTime FromUtc { get; }
       /// <summary>Exclusive upper bound.</summary>
       public DateTime ToUtc { get; }

       public static DateRange LastDays(int days, DateTime nowUtc) =>
           new(nowUtc.AddDays(-days), nowUtc);
   }
   ```

9. **`IpVelocityStats.cs`**:
   ```csharp
   public sealed record IpVelocityStats(
       string Ip,
       TimeSpan Window,
       DateTime WindowEndUtc,
       long ClickCount,            // all tracker/pixel/beacon events from this IP in window
       long DistinctSessions,
       long DistinctUserAgents,
       long DistinctFingerprints,
       long FlaggedCount);         // events with Score >= 71 in window
   ```

10. **`CampaignFraudReport.cs`**:
    ```csharp
    public sealed record CampaignDailyCounts(
        DateOnly Day,
        long TotalEvents,          // all kinds
        long ScoredEvents,         // Kind == verdict
        long Allowed,              // verdicts with band 'allow'
        long Challenged,
        long Blocked,
        long ScoreSum,             // sum of scores over scored events; 0 when none (mergeable — feeds DAT-06 ScoreSum)
        double AvgScore,           // NaN when ScoredEvents == 0 (missing != zero)
        long NoJsBeaconCount);     // verdicts with has_js_beacon = false

    public sealed record CampaignFraudReport(
        string CampaignId,
        DateRange Range,
        long TotalEvents,
        long ScoredEvents,
        long Allowed,
        long Challenged,
        long Blocked,
        double AvgScore,           // NaN when ScoredEvents == 0
        long NoJsBeaconCount,
        IReadOnlyList<CampaignDailyCounts> Days);  // ordered ascending by Day
    ```

11. **`FlaggedSource.cs`** (field split matches the DAT-06 `FlaggedSourceDailyRow` consumer: FlaggedCount/BlockedCount/ScoreSum):
    ```csharp
    public sealed record FlaggedSource(
        string SourceType,         // "ip" for MVP; DAT-06 also allows "placement"|"device_id"|"fingerprint" later
        string SourceValue,        // e.g. "203.0.113.7"
        long FlaggedEvents,        // verdicts in challenge OR block band
        long BlockedEvents,        // verdicts in block band only (score 71–100)
        long TotalEvents,
        long ScoreSum,             // sum of scores over scored events; 0 when none
        double AvgScore,           // NaN when no scored events
        DateTime FirstSeenUtc,
        DateTime LastSeenUtc);
    ```

12. Ensure the project is in the solution (FND-01 added it; verify) and `dotnet build` passes.

## Files to create or modify

- `TelemetryGuard.Analytics.Abstractions/TelemetryGuard.Analytics.Abstractions.csproj` (verify only)
- `TelemetryGuard.Analytics.Abstractions/EventKind.cs`
- `TelemetryGuard.Analytics.Abstractions/VerdictConstants.cs`
- `TelemetryGuard.Analytics.Abstractions/ClickEvent.cs`
- `TelemetryGuard.Analytics.Abstractions/IEventSink.cs`
- `TelemetryGuard.Analytics.Abstractions/IAnalyticsQueries.cs`
- `TelemetryGuard.Analytics.Abstractions/ILabelSink.cs`
- `TelemetryGuard.Analytics.Abstractions/LabelEvent.cs`
- `TelemetryGuard.Analytics.Abstractions/DateRange.cs`
- `TelemetryGuard.Analytics.Abstractions/IpVelocityStats.cs`
- `TelemetryGuard.Analytics.Abstractions/CampaignFraudReport.cs`
- `TelemetryGuard.Analytics.Abstractions/FlaggedSource.cs`

## Acceptance criteria

- `dotnet build TelemetryGuard.sln` succeeds with zero warnings-as-errors regressions.
- The Abstractions project has NO NuGet package references and references only the FND-04 core project.
- `IEventSink` and `IAnalyticsQueries` signatures match spec D7 character-for-character (member names, parameter names, return types).
- `ClickEvent` contains every field listed in step 4 (68 storage-relevant members, including all FOUR click-id fields `Gclid`/`Fbclid`/`Msclkid`/`Ttclid` that API-02 extracts) with exact names/types; `new ClickEvent { ... }` with only `required` members compiles, and the resulting instance has `float.NaN` for all SDK numerics, `null` for all SDK `bool?` flags, `0` for all velocity ints, empty arrays for `HeaderNames`/`RuleHits`.
- `DateRange` throws `ArgumentException` for non-UTC or inverted bounds.
- No type in this project references `FraudFeatureVector`, any RiskEngine project, or any database/engine SDK.

## Testing

Add `tests/TelemetryGuard.Tests.Unit/Analytics/ClickEventDefaultsTests.cs` (the Unit test project exists from FND-01 and already references this project):
- `Defaults_AreAbsent_NotZero`: construct a minimal `ClickEvent` (identity + `Ip` + `HasJsBeacon=false` + `RetentionDays=90` + `TimestampUtc=DateTime.UtcNow`) and assert `float.IsNaN(e.StorageAgeSec)`, `float.IsNaN(e.MousePathLinearity)`, `e.WebdriverFlag is null`, `e.IpClicksLastMin == 0`, `e.RuleHits.Count == 0`.
- `EventKindWire_MapsAllValues`: asserts the four wire strings.
- `DateRange_Validates`: UTC-kind and ordering rules.

## Out of scope / guardrails

- **Contracts only.** No ClickHouse/Kusto/SQL/Redis packages, no I/O, no DI registrations (ANA-05 owns registration), no implementation classes.
- **No generic cross-engine query layer** (D7): do not add IQueryable, expression trees, query-builder helpers, or "engine-neutral SQL" strings to this project. Intent-named methods only.
- **Do not merge `ClickEvent` with `FraudFeatureVector`** (RSK-01) or create a shared base type between them.
- **Missing ≠ zero:** never change SDK float defaults to `0`, never make `bool?` flags default to `false`, never make velocity counters nullable.
- **`TenantId` is never optional:** do not add tenant-less overloads or make `ClickEvent.TenantId` defaultable; do not add tenant parameters to `IAnalyticsQueries` (tenant comes from `ITenantContext` in implementations).
- No server-side Python/Node anywhere (D1). Backend is .NET only.
- Do not implement Kusto anything — that is deferred to phase "later" (P2-05).
