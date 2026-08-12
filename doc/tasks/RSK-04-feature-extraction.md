---
id: RSK-04
title: "Feature extraction"
phase: 1
workstream: risk
depends_on: [RSK-01, RSK-02, RSK-03]
size: L
spec_refs: [D4, D13, D22, "§6.1", "§7"]
detail_level: full
---

# RSK-04: Feature extraction

## Objective

Implement `IFeatureExtractor.Extract(RawSessionData) -> FraudFeatureVector` in `TelemetryGuard.RiskEngine` as a PURE, synchronous function: every piece of I/O (enrichment, velocity, session state, campaign config) is pre-fetched by the caller into `RawSessionData`; extraction itself performs no I/O, no clock reads, no randomness. This task defines `RawSessionData` (and its parts `BeaconData` — an aggregate-carrying record mirroring API-04's ingest hash — and `CampaignContext`), the exact derivation formulas that finish mouse-path linearity and inter-event statistics from those aggregates, UA-based heuristics via the `DeviceDetector.NET` package, the config-gated JA3/JA4 `tls_ua_mismatch` map, geo/timezone/language mismatches, the Apple Private Relay carve-out, and the full NaN-propagation table for degraded inputs.

## Spec context (self-contained)

- The SDK ships RAW events; the risk engine computes ALL derived features (spec §7 "architectural implication") — linearity, inter-event stats, velocity, mismatches. This keeps the browser snippet dumb and lets features evolve server-side. Pipeline split: the SDK ships raw events to `/i`; API-04 reduces them to RUNNING AGGREGATES at ingest (Welford mean/M2, path length + endpoints, counters — raw event points are never stored server-side, per API-04's guardrail); THIS task finishes the derived statistics from those aggregates.
- **Missing ≠ zero**: a session without a JS beacon (non-JS bot, or web-pixel-mode tenant — D22) gets `NaN` for every SDK-derived float and `null` for every SDK-derived `bool?`, plus `HasJsBeacon=false` (itself a T2 feature). Velocity counters remain plain numbers (0 when cold).
- D4: trajectory analysis v1 = three derived statistics (`mouse_path_linearity`, `mean_inter_event_ms`, `std_inter_event_ms`) feeding the scorer — deliberately NOT a sequence model.
- D13: `tls_ua_mismatch` ("UA says Chrome, TLS handshake says Go binary") depends on Cloudflare passing JA3/JA4 headers; when the header is absent the signal degrades to NaN/null — the scorer tolerates it. Cloudflare fronting arrives in Phase 1.5 (INT-05), so this signal is config-gated OFF by default.
- §7 Private Relay carve-out: when the IP is in Apple iCloud Private Relay egress ranges, `is_private_relay` (CTX) is true and `ip_proxy_or_vpn` is FORCED false — Private Relay users are legitimate Safari users.
- §6.1: no beacon within the ~10 s grace period → score on HTTP + velocity features alone; `click_id_invalid` = missing or replayed gclid/fbclid on paid clicks.
- Timing metadata only — the SDK never captures key values; the ingest aggregates carry counters and timing statistics only, never content (spec §3 non-goal).
- Scoring budget < 50 ms in-process (D3): extraction must be allocation-conscious and synchronous.

## Prerequisites

- RSK-01 (`TelemetryGuard.RiskEngine.Contracts`): `FraudFeatureVector` (all member names used below are defined there), `AsnType`, `ChallengeOutcome`. This task consumes them verbatim.
- RSK-02 (`TelemetryGuard.RiskEngine/Enrichment`): `IpEnrichment` record with `CountryCode, City, Latitude, Longitude, TimeZone, AsnNumber, AsnOrganization, AsnType, IsProxyOrVpn (raw), IsTor, IsDatacenter, IsPrivateRelay`.
- RSK-03 (`TelemetryGuard.RiskEngine/Velocity`): `VelocitySnapshot(IpClicksLastMin, IpDistinctUasLastHour, DeviceSessionsLastHour, DeviceIdsThisIpHour, StorageAgeZeroRepeat)`.
- `TelemetryGuard.RiskEngine` project references Contracts and contains the Enrichment/Velocity namespaces.

## Implementation steps

1. Add NuGet package `DeviceDetector.NET` to `TelemetryGuard.RiskEngine.csproj` (UA parser; namespace `DeviceDetectorNET`). Wrap it behind a small internal `UaInfo Parse(string ua)` helper (`internal sealed record UaInfo(string? OsFamily, string? BrowserFamily, bool IsMobileDevice, string? DeviceModel, bool IsBot)`) with an LRU/memory cache (`IMemoryCache` or a bounded `ConcurrentDictionary`, cap 10_000 entries) because DeviceDetector parsing is not free and UAs repeat heavily. The cache lives in the extractor instance; it does not make `Extract` impure (same input → same output).

2. Create `TelemetryGuard.RiskEngine/Features/RawSessionData.cs` — the assembled, pre-fetched input:

```csharp
using TelemetryGuard.RiskEngine.Contracts;
using TelemetryGuard.RiskEngine.Enrichment;
using TelemetryGuard.RiskEngine.Velocity;

namespace TelemetryGuard.RiskEngine.Features;

/// <summary>Everything the extractor needs, pre-fetched by the scoring pipeline (RSK-07).
/// Extraction is pure: no I/O may hide behind these members.</summary>
public sealed record RawSessionData
{
    public required string SessionId { get; init; }
    public required string Ip { get; init; }
    public string? UserAgent { get; init; }
    /// <summary>Selected request headers, case-insensitive keys. Relevant:
    /// "Accept-Language", "Referer", "Sec-CH-UA-Platform", "Sec-CH-UA-Mobile".</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>JA3/JA4 fingerprint from the Cloudflare header; null when not fronted (D13).</summary>
    public string? TlsFingerprint { get; init; }

    public bool IsPaidClick { get; init; }
    public string? ClickId { get; init; }          // gclid/fbclid as received
    /// <summary>From IVelocityStore.RecordClickAsync at capture time:
    /// true = fresh, false = replayed, null = no click id was present.</summary>
    public bool? ClickIdFresh { get; init; }

    /// <summary>null = no beacon arrived within the grace period (non-JS bot or pixel mode).</summary>
    public BeaconData? Beacon { get; init; }

    public required IpEnrichment Enrichment { get; init; }
    public required VelocitySnapshot Velocity { get; init; }
    public CampaignContext? Campaign { get; init; }
    public ChallengeOutcome ChallengeOutcome { get; init; } = ChallengeOutcome.NotChallenged;
}

/// <summary>Campaign config subset needed for extraction. The pipeline maps this from the
/// campaign entity exposed by the DAT-05 config repositories.</summary>
public sealed record CampaignContext
{
    /// <summary>ISO 3166-1 alpha-2 codes the campaign targets; empty = no geo targeting.</summary>
    public IReadOnlyList<string> GeoTargets { get; init; } = Array.Empty<string>();
}
```

3. Create `TelemetryGuard.RiskEngine/Features/BeaconData.cs` — PRE-AGGREGATED, timing-only behavioral state (this shape is normative for the risk side; the scoring pipeline RSK-07 maps it field-for-field from the Redis session hash that API-04 maintains at ingest — API-04 stores running aggregates ONLY and never raw event points, so this record carries the aggregates, not an event list):

```csharp
namespace TelemetryGuard.RiskEngine.Features;

public sealed record BeaconData
{
    public string? VisitorId { get; init; }              // FingerprintJS visitor id

    // --- Mouse-movement aggregates (API-04 step 6: Welford over inter-`mm` gaps +
    //     accumulated path length and endpoints; hash fields named in comments) ---
    /// <summary>Number of inter-move gaps (`mm_n`). Mouse POINT count = MmN + 1
    /// when any move was seen (FirstX is not NaN), else 0.</summary>
    public int MmN { get; init; }
    public double MmMeanMs { get; init; } = double.NaN;  // mm_mean_ms (Welford mean of gaps)
    public double MmM2 { get; init; } = double.NaN;      // mm_m2 (sum of squared deviations)
    public double MmPathLen { get; init; }               // mm_path_len (Σ segment lengths)
    public float FirstX { get; init; } = float.NaN;      // mm_first_x; NaN = no move seen
    public float FirstY { get; init; } = float.NaN;      // mm_first_y
    public float PrevX { get; init; } = float.NaN;       // mm_prev_x (latest point)
    public float PrevY { get; init; } = float.NaN;       // mm_prev_y

    /// <summary>ms from navigation start to first interaction, computed AT INGEST
    /// (hash `first_interaction_delay_ms`). API-04's definition is authoritative
    /// because API-04 computes it: earliest click/key/scroll/touch event minus
    /// nav_ts (mouse moves excluded). null = not observed. Consumed verbatim —
    /// this task never recomputes it.</summary>
    public double? FirstInteractionDelayMs { get; init; }

    // --- Event counters (hash n_click / n_key / n_scroll / n_touch) ---
    public int ClickCount { get; init; }
    public int KeyCount { get; init; }
    public int ScrollEventCount { get; init; }
    public int TouchCount { get; init; }

    // Flags observed by the SDK (already booleans at capture; absence within a present
    // beacon should be sent as false by the SDK — a present beacon reports all checks):
    public bool WebdriverFlag { get; init; }             // navigator.webdriver
    public bool HeadlessBrowser { get; init; }           // Botd verdict
    public bool HoneypotTouched { get; init; }
    public bool PointerUntrusted { get; init; }          // any pointer event with isTrusted=false
    public bool ClickBeforeRender { get; init; }
    public bool IntegrityOk { get; init; } = true;       // beacon signature check (API-04 verifies)

    public int? ScreenWidth { get; init; }
    public int? ScreenHeight { get; init; }
    public int? ViewportWidth { get; init; }
    public int? ViewportHeight { get; init; }
    public double? DevicePixelRatio { get; init; }
    public string? Timezone { get; init; }               // IANA from Intl API
    public string? Language { get; init; }               // navigator.language
    public bool CookiesEnabled { get; init; } = true;
    public bool CanvasFpBlocked { get; init; }
    public double? StorageAgeSec { get; init; }          // age of our first-party cookie/localStorage stamp
    public double SessionDurationMs { get; init; }       // last_beacon_ts − nav_ts (RSK-07 maps it)
    public int PagesViewed { get; init; } = 1;

    // Form telemetry (timing only):
    public bool FormSubmitted { get; init; }
    public double? FormFirstFocusTMs { get; init; }
    public double? FormSubmitTMs { get; init; }
    public bool AutofillDetected { get; init; }
    public bool PasteInIdentityFields { get; init; }
}
```

4. Create `TelemetryGuard.RiskEngine/Features/IFeatureExtractor.cs` and `FeatureExtractor.cs`:

```csharp
public interface IFeatureExtractor
{
    /// <summary>Pure function: all I/O pre-fetched into RawSessionData. Deterministic,
    /// thread-safe, synchronous (50 ms budget, D3).</summary>
    FraudFeatureVector Extract(RawSessionData raw);
}
```

`FeatureExtractor` constructor: `FeatureExtractor(IOptionsMonitor<FeatureExtractionOptions> options)` — options gate `tls_ua_mismatch` (step 8). Register as singleton.

5. **Derived-statistics formulas (exact — implement in `TrajectoryStats` static class, `TelemetryGuard.RiskEngine/Features/TrajectoryStats.cs`; these FINISH the math from API-04's running aggregates — no raw points exist server-side):**
   - Mouse point count: `MousePoints = float.IsNaN(Beacon.FirstX) ? 0 : Beacon.MmN + 1` (a single observed move yields MmN = 0 and counts as 1 point).
   - `mouse_path_linearity`: require **MousePoints ≥ 5, else NaN**; `MmPathLen <= 0` → NaN. `straight = Dist((FirstX, FirstY), (PrevX, PrevY))`. Result = `(float)(straight / MmPathLen)` (range (0,1]).
   - `mean_inter_event_ms` / `std_inter_event_ms`: over the `MmN` inter-move gaps aggregated at ingest. Require **MmN ≥ 2, else both NaN**. `mean = (float)MmMeanMs`; `std = (float)Math.Sqrt(MmM2 / MmN)` — **population** standard deviation (divide by the gap count m = MmN, not m−1). NOTE: API-04 step 12's parenthetical mentions `sqrt(mm_m2/(mm_n-1))` (sample); the POPULATION divisor here is normative — RSK-01's `StdInterEventMs` doc-comment says population, and API-04 only stores the ingredients (`mm_m2`, `mm_n`) without computing a std itself, so no producer change is required.
   - `first_interaction_delay_ms`: `(float)Beacon.FirstInteractionDelayMs` verbatim (ingest-computed, see step 3); null → NaN.
   - `input_event_count`: `MousePoints + ClickCount + KeyCount + TouchCount` (float; scroll EXCLUDED — browsers coalesce scroll non-uniformly). `scroll_events`: `ScrollEventCount`. `pages_viewed`: `PagesViewed`.
   - `time_on_page_sec = (float)(SessionDurationMs / 1000.0)`.
   - `form_fill_time_sec = (FormSubmitTMs − FormFirstFocusTMs) / 1000.0`; either null → NaN.

6. **UA-derived heuristics** (all `null` when `UserAgent` is null/empty):
   - `ua_os_mismatch`: parse UA → `OsFamily`. Read header `Sec-CH-UA-Platform` (strip quotes). Normalize both to {`Windows`, `macOS`, `Android`, `iOS`, `Linux`, `Chrome OS`}. Header absent → `null` (indeterminate). Both present and different (after treating `iPadOS`→`iOS`) → `true`, else `false`.
   - `emulator_or_vm` (`bool?`): true when UA or DeviceDetector model contains any of (case-insensitive): `"Android SDK built for x86"`, `"sdk_gphone"`, `"Emulator"`, `"Genymotion"`, `"Droid4X"`, `"Nox"`, `"BlueStacks"`, `"MuMu"`, `"Andy"`, or DeviceDetector reports brand `"Generic"` on an Android x86 UA. Else false. Keep the list in a private static readonly array with a comment: heuristic seed list, extend during listen-only tuning.
   - `screen_res_anomalous` (`bool?`, needs beacon; null when beacon or dimensions absent): true when ANY of: `ScreenWidth <= 0 || ScreenHeight <= 0`; `DevicePixelRatio is <= 0 or > 5`; `ViewportWidth > ScreenWidth || ViewportHeight > ScreenHeight` (viewport larger than screen); aspect ratio `max(w,h)/min(w,h) > 3.6`; desktop (per `is_mobile == false`) with `ScreenWidth < 800`; else false.
   - `is_mobile` (`bool?`): header `Sec-CH-UA-Mobile` (`"?1"` → true, `"?0"` → false) when present; else DeviceDetector device type ∈ {smartphone, tablet, phablet}; UA absent → null.

7. **Click-id validity**: `IsPaidClick == false` → `ClickIdInvalid = null`. Paid: `ClickId == null` → `true` (missing); `ClickIdFresh == false` → `true` (replayed); `ClickIdFresh == true` → `false`; `ClickId != null && ClickIdFresh == null` (capture-time dedupe unavailable) → `null`.

8. **`tls_ua_mismatch`** — config-gated map. Create `FeatureExtractionOptions`:

```csharp
public sealed class FeatureExtractionOptions
{
    /// <summary>OFF until Cloudflare fronting (INT-05) supplies JA3/JA4 headers.</summary>
    public bool TlsUaMismatchEnabled { get; set; } = false;
}
```

Config section `"FeatureExtraction": { "TlsUaMismatchEnabled": false }`. Embed `TelemetryGuard.RiskEngine/Features/Data/tls-fingerprints.json` as `<EmbeddedResource>`: a JSON object mapping fingerprint string → family label. Seed it with a commented structure and well-known values (families: `"go-http"`, `"python"`, `"curl"`, `"okhttp"`, `"java"`, `"chrome"`, `"firefox"`, `"safari"`):

```json
{
  "_comment": "JA3 MD5 / JA4 → client family. Seed list; extend during listen-only. Sources: public JA3 corpora (e.g. salesforce/ja3, ja4db). Non-browser families fire tls_ua_mismatch when the UA claims a browser.",
  "473cd7cb9faa642487833865d516e578": "go-http",
  "3faa4ad39f690c4ef1c3160caa375465": "go-http",
  "8d9f7747675e24454cd9b7ed35c58707": "python",
  "b32309a26951912be7dba376398abc3b": "python",
  "e7d705a3286e19ea42f587b344ee6865": "curl",
  "3e860202fc555b939e83e7a7ab518c38": "okhttp",
  "2c9e5e39d0fac6e63c308443c38fca28": "java"
}
```

(These exact hashes are seed values from public JA3 corpora — verify/extend from https://github.com/salesforce/ja3 lists while implementing; the mechanism, not the seed contents, is the contract.) Logic: gate off OR `TlsFingerprint == null` → `null`. Fingerprint not in map → `null` (unknown proves nothing — avoid false positives). Mapped family is a non-browser family (`go-http|python|curl|okhttp|java`) AND UA parses to a browser family (Chrome/Firefox/Safari/Edge/Opera) → `true`. Mapped family is a browser family that differs from the UA browser family → `true`. Otherwise `false`.

9. **Geo/timezone/language mismatches** (all `bool?`):
   - `ip_geo_target_mismatch`: `Campaign == null || Campaign.GeoTargets.Count == 0 || Enrichment.CountryCode == null` → null; else `!GeoTargets.Contains(CountryCode, StringComparer.OrdinalIgnoreCase)`.
   - `timezone_ip_mismatch`: needs `Beacon.Timezone` and `Enrichment.TimeZone`; either null → null. Resolve both via `TimeZoneInfo.FindSystemTimeZoneById` (IANA ids work cross-platform on .NET 8; wrap in try/catch → null on failure). Compare `BaseUtcOffset` difference: `abs ≥ 2 hours` → true, else false (2 h tolerance avoids neighboring-zone false positives).
   - `language_geo_mismatch`: primary tag of `Accept-Language` header (first entry before `,`/`;`, take 2-letter language subtag lowercase; fall back to `Beacon.Language`). Null language or null `CountryCode` → null. Embed `Features/Data/country-languages.json` (`<EmbeddedResource>`): map of ~60 common ISO country codes → array of plausible language codes (e.g. `"DE": ["de","en"]`, `"US": ["en","es"]`, `"FR": ["fr","en"]`, `"IN": ["hi","en","ta","te","bn","mr","ur"]`, `"CH": ["de","fr","it","en"]` …). Country not in map → null. `lang == "en"` → false (English is globally plausible — explicit FP guard). Else `true` iff lang not in the country's list.

10. **Assembly** — `Extract` builds the vector. Full mapping table (Vector member ← source):

| Member | Source / rule |
|---|---|
| `HoneypotTouched, ClickBeforeRender, PointerUntrusted, WebdriverFlag, HeadlessBrowser` | `Beacon.<flag>`; `Beacon == null` → null |
| `BeaconIntegrityOk` | `Beacon?.IntegrityOk`; null when no beacon |
| `IpTor` | `Enrichment.IsTor` (null passes through) |
| `TlsUaMismatch` | step 8 |
| `IpDatacenterAsn` | `Enrichment.IsDatacenter` |
| `IpClicksLastMin` | `Velocity.IpClicksLastMin` |
| `UaOsMismatch` | step 6 |
| `MousePathLinearity, StdInterEventMs, MeanInterEventMs, FirstInteractionDelayMs, InputEventCount` | step 5; no beacon → NaN |
| `ClickIdInvalid` | step 7 |
| `HasJsBeacon` | `Beacon != null` |
| `EmulatorOrVm, ScreenResAnomalous, IsMobile` | step 6 |
| `StorageAgeZeroRepeat` | `Beacon?.VisitorId == null` → NaN; else `(float)Velocity.StorageAgeZeroRepeat` |
| `IpProxyOrVpn` | **carve-out**: `Enrichment.IsPrivateRelay ? false : Enrichment.IsProxyOrVpn` |
| `IpGeoTargetMismatch` | step 9 |
| `TimeOnPageSec, FormFillTimeSec` | step 5; no beacon → NaN |
| `InputModalityMismatch` | null when no beacon or `InputEventCount < 10`; else true when (`IsMobile == true` && MousePoints > 10 && `TouchCount == 0`) OR (`IsMobile == false` && `TouchCount > 10` && MousePoints == 0); else false |
| `DeviceSessionsLastHour, IpDistinctUasLastHour, DeviceIdsThisIpHour` | `Velocity.*` verbatim |
| `CookiesDisabled` | `Beacon == null ? null : !Beacon.CookiesEnabled` |
| `CanvasFpBlocked, PasteInIdentityFields` | `Beacon?.<flag>` |
| `TimezoneIpMismatch, LanguageGeoMismatch` | step 9 |
| `IpReputationBad` | `0f` constant — NO producer exists in ANY planned task (checked Phases 1–2 / P2-01..P2-05); the signal is explicitly DEFERRED here. A decaying per-IP reputation store is backlog work (suggested id P2-06, phase later, owned by the P2 workstream). Leave `// TODO(P2-06): reputation store — no producer in current plan; RSK-06's default weight is 0 until one exists` |
| `ReferrerMissing` | `!Headers.ContainsKey("Referer") || string.IsNullOrEmpty(Headers["Referer"])` |
| `ScrollEvents, PagesViewed` | `Beacon == null` → NaN; else counts as float |
| `AsnType` | `Enrichment.AsnType` |
| `IsPrivateRelay` | `Enrichment.IsPrivateRelay` |
| `FormSubmitted, AutofillDetected` | `Beacon?.<flag>` |
| `IsPaidClick` | `raw.IsPaidClick` |
| `ChallengeOutcome` | `raw.ChallengeOutcome` |

11. **NaN-propagation table** — reproduce this as a doc comment on `Extract` and enforce with tests:

| Degraded input | Effect on vector |
|---|---|
| `Beacon == null` (no JS — bot or pixel mode) | `HasJsBeacon=false`; NaN: `MousePathLinearity, MeanInterEventMs, StdInterEventMs, FirstInteractionDelayMs, TimeOnPageSec, FormFillTimeSec, StorageAgeZeroRepeat, InputEventCount, ScrollEvents, PagesViewed`; null: `HoneypotTouched, ClickBeforeRender, PointerUntrusted, WebdriverFlag, HeadlessBrowser, BeaconIntegrityOk, ScreenResAnomalous, CookiesDisabled, CanvasFpBlocked, PasteInIdentityFields, TimezoneIpMismatch (beacon side), InputModalityMismatch, FormSubmitted, AutofillDetected` |
| Enrichment DBs missing | null: `IpTor, IpProxyOrVpn, IpDatacenterAsn, IpGeoTargetMismatch, TimezoneIpMismatch, LanguageGeoMismatch`; `AsnType=Unknown`; velocity untouched |
| `UserAgent` null | null: `UaOsMismatch, EmulatorOrVm, IsMobile` (and `InputModalityMismatch` when IsMobile null) |
| `TlsFingerprint` null or gate off | `TlsUaMismatch = null` |
| Organic (not paid) | `ClickIdInvalid = null`; `IsPaidClick=false` |

Velocity members are NEVER NaN (legitimately 0 cold) — only `StorageAgeZeroRepeat` maps to NaN, and only because it is keyed by a fingerprint that does not exist without a beacon.

## Files to create or modify

- `TelemetryGuard.RiskEngine/TelemetryGuard.RiskEngine.csproj` (add `DeviceDetector.NET`, `Microsoft.Extensions.Caching.Memory`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Options.ConfigurationExtensions`, `Microsoft.Extensions.DependencyInjection.Abstractions` — needed by `IOptionsMonitor`, the config binding, and the `AddFeatureExtraction` extension; plus embedded resources)
- `TelemetryGuard.RiskEngine/Features/RawSessionData.cs`
- `TelemetryGuard.RiskEngine/Features/BeaconData.cs`
- `TelemetryGuard.RiskEngine/Features/IFeatureExtractor.cs`
- `TelemetryGuard.RiskEngine/Features/FeatureExtractor.cs`
- `TelemetryGuard.RiskEngine/Features/TrajectoryStats.cs`
- `TelemetryGuard.RiskEngine/Features/FeatureExtractionOptions.cs`
- `TelemetryGuard.RiskEngine/Features/UaHeuristics.cs` (DeviceDetector wrapper + lists)
- `TelemetryGuard.RiskEngine/Features/Data/tls-fingerprints.json` (embedded)
- `TelemetryGuard.RiskEngine/Features/Data/country-languages.json` (embedded)
- `tests/TelemetryGuard.Tests.Unit/Features/*` (see Testing)

## Acceptance criteria

- `dotnet build` passes; `FeatureExtractor` registered singleton via an `AddFeatureExtraction(IServiceCollection, IConfiguration)` extension.
- Purity: `Extract` called twice with the same `RawSessionData` returns equal records (record equality); `Extract` performs no I/O (no async, no clock, verified by code review + determinism test).
- Formula tests pass (inputs are the AGGREGATES a straight-line/etc. session would produce):
  - straight 5-point line (0,0)→(4,0): `MmN=4, First=(0,0), Prev=(4,0), MmPathLen=4` → `MousePathLinearity == 1.0f` (±1e-4); square-wave aggregates (path length ≫ chord) → < 0.5; 4 points (`MmN=3`) → NaN; all points identical (`MmPathLen=0`) → NaN.
  - moves at t = 0,100,200,300 → aggregates `MmN=3, MmMeanMs=100, MmM2=0` → `MeanInterEventMs == 100`, `StdInterEventMs == 0`; moves at t = 0,100,300 → `MmN=2, MmMeanMs=150, MmM2=5000` → `MeanInterEventMs == 150`, `StdInterEventMs == 50` (population: `sqrt(5000/2)`; sample std for the same input is ≈70.71 — if the test sees that value, the divisor is wrong); 2 moves (`MmN=1`) → both NaN.
  - `FirstInteractionDelayMs` passthrough: `Beacon.FirstInteractionDelayMs = 450` → vector 450f; null → NaN.
- No-beacon vector matches the NaN-propagation table EXACTLY (a test constructs `RawSessionData` with `Beacon = null` and asserts every row of the table).
- Private Relay carve-out test: `Enrichment { IsPrivateRelay = true, IsProxyOrVpn = true }` → vector has `IsPrivateRelay == true` and `IpProxyOrVpn == false`.
- `ClickIdInvalid` truth table test covers all five rows of step 7.
- `TlsUaMismatch` returns null when gate off (default), null for unknown fingerprints, true for a seeded `go-http` hash + Chrome UA, false for matching browser family.
- Geo/timezone/language mismatch tests cover null-side, match, mismatch, and the English FP-guard.
- `dotnet test --filter "FullyQualifiedName~Features"` passes.

## Testing

Unit tests only (pure function): `tests/TelemetryGuard.Tests.Unit/Features/` — `TrajectoryStatsTests`, `NoBeaconPropagationTests`, `PrivateRelayCarveOutTests`, `ClickIdValidityTests`, `TlsUaMismatchTests`, `UaHeuristicsTests` (real UA strings for Chrome/Windows, Safari/iOS, Android emulator, HeadlessChrome), `GeoLanguageTimezoneTests`. Build a `RawSessionDataBuilder` test helper with sane defaults (empty headers, an all-null/`Unknown` `IpEnrichment` — add a `public static readonly IpEnrichment Empty` member to RSK-02's record if it does not already exist, or construct one inline in the builder — and a zero `VelocitySnapshot`) so each test mutates only what it asserts — RSK-05/06/07 tests reuse this builder.

## Out of scope / guardrails

- NO I/O inside `Extract` — no Redis, SQL, ClickHouse, HTTP, `DateTime.Now`, or `Random`. All inputs arrive pre-fetched in `RawSessionData` (testability + the < 50 ms budget). Prefetching is RSK-07's job.
- NEVER map a missing SDK signal to 0/false — NaN for floats, null for `bool?` (missing ≠ zero, spec §7). Never turn a velocity 0 into NaN either — cold counters are legitimately 0.
- No keystroke CONTENT anywhere: `BeaconData` carries counters and timing aggregates only; reject any temptation to add key codes/values or raw event lists (spec §3 compliance line; API-04's guardrail forbids storing raw points).
- Do not fire or score anything here — no rule evaluation (RSK-05), no weights (RSK-06), no band mapping. Extraction produces the vector, nothing else.
- Do not query campaign config, whitelists, or session state from here; `CampaignContext` is a plain pre-fetched record. `tenant_id` is not a vector field and not a parameter — tenancy stays in `ITenantContext` upstream.
- `tls_ua_mismatch` stays config-gated OFF until INT-05 lands (Cloudflare fronting); absent header → null, never false.
- No publisher-aggregate features (Phase 2 / P2-01) and no `ip_reputation_bad` producer (no source exists in ANY planned task — constant 0, deferral recorded in the step-10 table; RSK-06's default weight for it is 0 until a reputation-store task, suggested P2-06, exists).
- No server-side Python/Node (D1); no EF (D9).
