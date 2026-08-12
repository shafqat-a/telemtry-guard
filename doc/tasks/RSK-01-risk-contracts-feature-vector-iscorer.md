---
id: RSK-01
title: "Risk contracts: FraudFeatureVector and IScorer"
phase: 1
workstream: risk
depends_on: [FND-01]
size: L
spec_refs: [D4, D18, "§6.3", "§7"]
detail_level: full
---

# RSK-01: Risk contracts: FraudFeatureVector and IScorer

## Objective

Create the `TelemetryGuard.RiskEngine.Contracts` project content: the sealed `FraudFeatureVector` record carrying every fraud signal defined by the spec (tiers T1/T2/T3/CTX with exact null/NaN semantics), the `AsnType` and `ChallengeOutcome` enums, the `IScorer` interface with `ScoreResult`, and the `VerdictBand` enum with `BandMapper` (0–30 allow / 31–70 challenge / 71–100 block). Every other risk-engine task (RSK-02..RSK-08) and the API decision path build on these types, so names and semantics defined here are frozen as `feature_set_version = 1`.

**IMPORTANT — normative status:** the spec references a companion document `fraud-signal-feature-spec.md` holding the detailed signal contract. That file is ABSENT from this repository. The C# file you create in this task is therefore the NORMATIVE source for feature names and semantics. Record this fact in an XML doc comment on the record (text given below).

## Spec context (self-contained)

- The system scores every click/visit 0–100 in real time. Score bands: **0–30 allow**, **31–70 challenge (Cloudflare Turnstile)**, **71–100 block + exclude from attribution**.
- Signals are tiered: **T1** = near-deterministic when it fires (rule-eligible; absence proves nothing) · **T2** = strong model feature · **T3** = weak/supporting, never a rule (privacy-tool and edge-case false-positive risk) · **CTX** = context/conditioning only, never scored directly.
- **Null semantics — missing ≠ zero.** Sessions with no JS beacon (non-JS bots, web-pixel-mode tenants) carry `NaN` for every SDK-derived feature, never 0 (LightGBM branches on NaN natively). Velocity counters are legitimately 0 when cold, so they are plain numerics. `has_js_beacon` is itself a T2 feature, not just a gating flag.
- Deterministic T1 rules may only RAISE a score floor; rules never lower a score.
- The MVP ships without a trained model (D18): a heuristic scorer implements `IScorer`; a LightGBM model replaces it later via config swap only. Every verdict is stamped with `scorer_version` so heuristic-era data is never confused with model-era data.
- `device_ids_this_ip_hour` must later be normalized by `asn_type` (carrier-grade NAT produces many devices per mobile IP legitimately) — hence `AsnType` is a first-class enum here.
- Apple iCloud Private Relay is carved OUT of `ip_proxy_or_vpn` (it would flag legitimate Safari users); it is a CTX flag `is_private_relay` instead.
- The CTX fields `is_paid_click` (T1 rules `ip_tor` and `ip_datacenter_asn` fire only on paid clicks) and `challenge_outcome` (the decision endpoint re-scores after a Turnstile challenge) are required by RSK-05 and API-05.
- `feature_set_version = 1` for this contract; it is stamped into every `ScoreResult`.

## Prerequisites

FND-01 has created `TelemetryGuard.sln` with the project skeletons from spec §8, including `TelemetryGuard.RiskEngine.Contracts` and `tests/TelemetryGuard.Tests.Unit`. Read the FND-01 task file for the exact folder layout (projects may sit at repo root or under `src/`). If `TelemetryGuard.RiskEngine.Contracts.csproj` does not exist yet, create it (`net8.0`, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, no package references) and `dotnet sln add` it. This project must have ZERO NuGet dependencies — it is referenced by API, risk engine, and training code alike.

## Implementation steps

1. Create `TelemetryGuard.RiskEngine.Contracts/AsnType.cs`:

```csharp
namespace TelemetryGuard.RiskEngine.Contracts;

/// <summary>Coarse classification of the autonomous system an IP belongs to.
/// Drives carrier-grade-NAT normalization of velocity features and the
/// ip_datacenter_asn T1 rule. One-hot encoded when fed to a model.</summary>
public enum AsnType
{
    Unknown = 0,     // enrichment DB missing or ASN not classified
    Residential = 1, // consumer ISP (IP2Proxy usage_type ISP)
    Mobile = 2,      // mobile carrier (usage_type MOB) — carrier-grade NAT expected
    Business = 3,    // corporate egress (usage_type COM/ORG)
    Datacenter = 4,  // hosting/cloud (usage_type DCH or seed ASN list)
    Education = 5,   // universities/libraries (usage_type EDU/LIB)
    Government = 6,  // usage_type GOV/MIL
    Cdn = 7          // usage_type CDN
}
```

2. Create `TelemetryGuard.RiskEngine.Contracts/ChallengeOutcome.cs`:

```csharp
namespace TelemetryGuard.RiskEngine.Contracts;

/// <summary>Outcome of a Cloudflare Turnstile challenge for this session.
/// CTX conditioning input for re-scoring (spec §6.3: 31–70 band → challenge → re-score).</summary>
public enum ChallengeOutcome
{
    NotChallenged = 0,
    Passed = 1,
    Failed = 2
}
```

3. Create `TelemetryGuard.RiskEngine.Contracts/FraudFeatureVector.cs`. Reproduce EXACTLY (property names, types, defaults, and tier grouping are the frozen v1 contract):

```csharp
namespace TelemetryGuard.RiskEngine.Contracts;

/// <summary>
/// The complete fraud-signal vector for one session/click, feature_set_version = 1.
/// NORMATIVE NOTE: the companion document fraud-signal-feature-spec.md referenced by
/// doc/spec.md §7 is absent from this repository; THIS record is the normative
/// contract for feature names, types, and null semantics. Do not rename members
/// without bumping FeatureSetVersion.
///
/// Null/NaN semantics (spec §7, "missing ≠ zero"):
///  - float properties default to float.NaN = signal absent (e.g. no JS beacon).
///  - bool? properties: null = signal absent/indeterminate; never treat null as false.
///  - plain int/long/float-zero velocity counters are legitimately 0 when cold.
/// </summary>
public sealed record FraudFeatureVector
{
    /// <summary>Version of this feature contract. Stamped into every ScoreResult.</summary>
    public const int FeatureSetVersion = 1;

    // ================= T1 — near-deterministic when they fire (rule-eligible) =================

    /// <summary>SDK: an invisible honeypot form field was focused/filled. null = no beacon.</summary>
    public bool? HoneypotTouched { get; init; }

    /// <summary>SDK: click event observed before first paint/render completed. null = no beacon.</summary>
    public bool? ClickBeforeRender { get; init; }

    /// <summary>SDK: pointer event with isTrusted == false (synthetic dispatch). null = no beacon.</summary>
    public bool? PointerUntrusted { get; init; }

    /// <summary>SDK: navigator.webdriver was true. null = no beacon.</summary>
    public bool? WebdriverFlag { get; init; }

    /// <summary>SDK: Botd detected a headless/automated browser. null = no beacon.</summary>
    public bool? HeadlessBrowser { get; init; }

    /// <summary>SDK: beacon signature/integrity check result. false = tampered payload
    /// (rule fires on FALSE, not on null). null = no beacon received.</summary>
    public bool? BeaconIntegrityOk { get; init; }

    /// <summary>Enrichment: IP is a Tor exit node. Rule fires only when IsPaidClick.
    /// null = proxy DB unavailable.</summary>
    public bool? IpTor { get; init; }

    /// <summary>TLS (JA3/JA4 via Cloudflare header) contradicts the User-Agent
    /// ("UA says Chrome, handshake says Go binary"). null = no TLS fingerprint header
    /// (not fronted by Cloudflare yet — degrades gracefully per D13).</summary>
    public bool? TlsUaMismatch { get; init; }

    /// <summary>Enrichment: IP belongs to a hosting/datacenter ASN. Rule fires only when
    /// IsPaidClick. null = enrichment DBs unavailable.</summary>
    public bool? IpDatacenterAsn { get; init; }

    /// <summary>Velocity (Redis): clicks from this IP in the sliding last minute.
    /// Plain int — legitimately 0 when cold.</summary>
    public int IpClicksLastMin { get; init; }

    /// <summary>User-Agent OS contradicts Client Hints platform. null = Client Hints absent.</summary>
    public bool? UaOsMismatch { get; init; }

    /// <summary>Derived: straight-line(first,last) / sum(segment lengths) over mouse points.
    /// Near 1.0 = perfectly straight scripted movement. NaN when &lt; 5 points or no beacon.</summary>
    public float MousePathLinearity { get; init; } = float.NaN;

    /// <summary>Derived: population std-dev of inter-input-event gaps in ms.
    /// Near 0 = metronomic scripted input. NaN when &lt; 3 events or no beacon.</summary>
    public float StdInterEventMs { get; init; } = float.NaN;

    /// <summary>Paid click with missing OR replayed (Redis-deduped) gclid/fbclid.
    /// null = organic traffic (no click id expected).</summary>
    public bool? ClickIdInvalid { get; init; }

    // ================= T2 — strong model features =================

    /// <summary>A JS beacon arrived within the grace period. Itself a T2 feature
    /// (always known → plain bool). false ⇒ every SDK-derived member above/below is NaN/null.</summary>
    public bool HasJsBeacon { get; init; }

    /// <summary>UA/device heuristics indicate an emulator or VM. null = UA absent/no signal.</summary>
    public bool? EmulatorOrVm { get; init; }

    /// <summary>Reported screen/viewport geometry is implausible. null = no beacon.</summary>
    public bool? ScreenResAnomalous { get; init; }

    /// <summary>Velocity (Redis fpz counter): times this fingerprint presented zero-age
    /// first-party storage in the last 7 days (bot farm wiping state). Count 1 is an
    /// innocent first visit. float because NaN when no fingerprint (no beacon).</summary>
    public float StorageAgeZeroRepeat { get; init; } = float.NaN;

    /// <summary>Enrichment: proxy or VPN egress. FORCED to false when IsPrivateRelay
    /// (Apple Private Relay carve-out). null = proxy DB unavailable.</summary>
    public bool? IpProxyOrVpn { get; init; }

    /// <summary>IP country is outside the campaign's configured geo targets.
    /// null = no geo lookup, organic traffic, or campaign has no geo targets.</summary>
    public bool? IpGeoTargetMismatch { get; init; }

    /// <summary>SDK: seconds on page at beacon flush. NaN = no beacon.</summary>
    public float TimeOnPageSec { get; init; } = float.NaN;

    /// <summary>SDK: seconds from first form-field focus to submit. Condition on
    /// AutofillDetected before scoring low values. NaN = no form activity/no beacon.</summary>
    public float FormFillTimeSec { get; init; } = float.NaN;

    /// <summary>SDK: ms from navigation start to first input event. NaN = no beacon/no input.</summary>
    public float FirstInteractionDelayMs { get; init; } = float.NaN;

    /// <summary>Input events contradict claimed device class (e.g. mobile UA, mouse-only
    /// input). null = no beacon or too few events.</summary>
    public bool? InputModalityMismatch { get; init; }

    /// <summary>Derived: mean inter-input-event gap in ms. NaN when &lt; 3 events/no beacon.</summary>
    public float MeanInterEventMs { get; init; } = float.NaN;

    /// <summary>Velocity (Redis HLL): distinct sessions for this device fingerprint,
    /// last hour. 0 when cold or no fingerprint.</summary>
    public long DeviceSessionsLastHour { get; init; }

    /// <summary>Velocity (Redis HLL): distinct User-Agents seen from this IP, last hour.
    /// 0 when cold.</summary>
    public long IpDistinctUasLastHour { get; init; }

    /// <summary>Velocity (Redis HLL): distinct device fingerprints from this IP, last hour.
    /// Normalize by AsnType (carrier-grade NAT) before judging. 0 when cold.</summary>
    public long DeviceIdsThisIpHour { get; init; }

    // ================= T3 — weak/supporting; NEVER rule-eligible =================
    // (privacy tools and edge cases fire these on real humans — Brave, Firefox,
    //  password managers, corporate proxies)

    /// <summary>SDK: cookies disabled. null = no beacon.</summary>
    public bool? CookiesDisabled { get; init; }

    /// <summary>SDK: canvas fingerprint blocked/randomized (Brave/Firefox do this by
    /// design). null = no beacon.</summary>
    public bool? CanvasFpBlocked { get; init; }

    /// <summary>Browser-reported IANA timezone vs IP geolocation timezone offset differ
    /// materially. null = either side missing.</summary>
    public bool? TimezoneIpMismatch { get; init; }

    /// <summary>Accept-Language primary language implausible for IP country.
    /// null = either side missing.</summary>
    public bool? LanguageGeoMismatch { get; init; }

    /// <summary>Decaying 0..1 bad-reputation score for this IP. 0 = no negative history
    /// (legitimately 0 when cold — plain float, not NaN). No producer in Phase 1;
    /// always 0 until a reputation store exists.</summary>
    public float IpReputationBad { get; init; }

    /// <summary>SDK: paste events into identity fields (email/name/phone). Password
    /// managers do this legitimately. null = no beacon.</summary>
    public bool? PasteInIdentityFields { get; init; }

    /// <summary>HTTP: Referer header absent on the tracked request. Plain bool (always
    /// observable). Largely superseded by ClickIdInvalid but retained as weak T3.</summary>
    public bool ReferrerMissing { get; init; }

    /// <summary>SDK: total count of input timing events (mouse/key/touch). Used by rules
    /// as a minimum-evidence gate. NaN = no beacon.</summary>
    public float InputEventCount { get; init; } = float.NaN;

    /// <summary>SDK: count of scroll events. NaN = no beacon.</summary>
    public float ScrollEvents { get; init; } = float.NaN;

    /// <summary>SDK: pages viewed this session (SPA navigations included). NaN = no beacon.</summary>
    public float PagesViewed { get; init; } = float.NaN;

    // ================= CTX — context/conditioning only; never scored directly =================

    /// <summary>Device class is mobile (UA/Client Hints). null = UA absent.</summary>
    public bool? IsMobile { get; init; }

    /// <summary>ASN classification; Unknown when enrichment unavailable. One-hot for models.</summary>
    public AsnType AsnType { get; init; } = AsnType.Unknown;

    /// <summary>IP is in Apple iCloud Private Relay egress ranges. When true,
    /// IpProxyOrVpn is forced false. Always known (embedded range list) → plain bool.</summary>
    public bool IsPrivateRelay { get; init; }

    /// <summary>SDK: a monitored form was submitted. null = no beacon.</summary>
    public bool? FormSubmitted { get; init; }

    /// <summary>SDK: browser autofill detected on the form. Conditions FormFillTimeSec.
    /// null = no beacon/no form.</summary>
    public bool? AutofillDetected { get; init; }

    /// <summary>This session originated from a paid ad click (tracker hit /c with a click
    /// id). Gates the ip_tor and ip_datacenter_asn T1 rules. Always known → plain bool.</summary>
    public bool IsPaidClick { get; init; }

    /// <summary>Turnstile outcome for re-scoring (API-05). Default NotChallenged.</summary>
    public ChallengeOutcome ChallengeOutcome { get; init; } = ChallengeOutcome.NotChallenged;
}
```

4. Create `TelemetryGuard.RiskEngine.Contracts/IScorer.cs`:

```csharp
namespace TelemetryGuard.RiskEngine.Contracts;

/// <summary>Result of scoring one session. RuleHits are the T1 rule names that fired
/// (attached by the scoring pipeline, RSK-07 — IScorer implementations themselves
/// return an empty array). FeatureSetVersion echoes FraudFeatureVector.FeatureSetVersion.</summary>
public sealed record ScoreResult(
    int Score,                        // 0..100 inclusive
    IReadOnlyList<string> RuleHits,   // e.g. ["honeypot_touched"]; empty when none
    string ScorerVersion,             // e.g. "heuristic-1", "lgbm-20260901-a1b2c3d4"
    int FeatureSetVersion);           // always FraudFeatureVector.FeatureSetVersion today

/// <summary>The pluggable scorer seam (D18): heuristic at MVP, ML.NET LightGBM later,
/// swapped by config only. Implementations MUST be pure, thread-safe, allocation-light,
/// and synchronous — they run in-process inside the &lt; 50 ms scoring budget (D3).
/// Implementations return their model/heuristic score with EMPTY RuleHits; the pipeline
/// (RSK-07) applies the T1 rule floor via Max(score, floor) and attaches hits.</summary>
public interface IScorer
{
    /// <summary>Stable identifier stamped on every verdict (D18).</summary>
    string ScorerVersion { get; }

    ScoreResult Score(in FraudFeatureVector vector);
}
```

5. Create `TelemetryGuard.RiskEngine.Contracts/VerdictBand.cs`:

```csharp
namespace TelemetryGuard.RiskEngine.Contracts;

/// <summary>Enforcement band (spec §6.3).</summary>
public enum VerdictBand
{
    Allow = 0,      // 0–30: count conversion / valid click
    Challenge = 1,  // 31–70: Cloudflare Turnstile; re-score with outcome
    Block = 2       // 71–100: block; exclude from attribution; feed exclusion sync
}

public static class BandMapper
{
    /// <summary>Maps a 0–100 score to its band. Throws outside 0–100 — an out-of-range
    /// score is a scorer bug and must never be silently clamped into an enforcement band.</summary>
    public static VerdictBand ToBand(int score) => score switch
    {
        >= 0  and <= 30  => VerdictBand.Allow,
        >= 31 and <= 70  => VerdictBand.Challenge,
        >= 71 and <= 100 => VerdictBand.Block,
        _ => throw new ArgumentOutOfRangeException(nameof(score), score, "Score must be within 0–100.")
    };
}
```

6. Add unit tests in `tests/TelemetryGuard.Tests.Unit` (create folder `RiskContracts/`):
   - `BandMapperTests.cs`: `[Theory]` over `(0, Allow) (15, Allow) (30, Allow) (31, Challenge) (50, Challenge) (70, Challenge) (71, Block) (100, Block)`; `(-1)` and `(101)` throw `ArgumentOutOfRangeException`.
   - `FraudFeatureVectorDefaultsTests.cs`: construct `new FraudFeatureVector()` and assert:
     - every SDK-derived float defaults to `float.IsNaN(...)`: `MousePathLinearity`, `StdInterEventMs`, `MeanInterEventMs`, `StorageAgeZeroRepeat`, `TimeOnPageSec`, `FormFillTimeSec`, `FirstInteractionDelayMs`, `InputEventCount`, `ScrollEvents`, `PagesViewed`;
     - every velocity counter defaults to 0: `IpClicksLastMin`, `DeviceSessionsLastHour`, `IpDistinctUasLastHour`, `DeviceIdsThisIpHour`; `IpReputationBad == 0f`;
     - every `bool?` member is `null` (use reflection: iterate `typeof(FraudFeatureVector).GetProperties()` where `PropertyType == typeof(bool?)` and assert null — this also future-proofs the test);
     - `AsnType == AsnType.Unknown`, `ChallengeOutcome == ChallengeOutcome.NotChallenged`, `HasJsBeacon == false`;
     - `FraudFeatureVector.FeatureSetVersion == 1`.
   - `ScoreResultTests.cs`: record equality + `with` cloning smoke test.

7. Ensure `tests/TelemetryGuard.Tests.Unit` has a `ProjectReference` to `TelemetryGuard.RiskEngine.Contracts`.

8. `dotnet build` and `dotnet test --filter "FullyQualifiedName~RiskContracts"` must pass.

## Files to create or modify

- `TelemetryGuard.RiskEngine.Contracts/AsnType.cs`
- `TelemetryGuard.RiskEngine.Contracts/ChallengeOutcome.cs`
- `TelemetryGuard.RiskEngine.Contracts/FraudFeatureVector.cs`
- `TelemetryGuard.RiskEngine.Contracts/IScorer.cs`
- `TelemetryGuard.RiskEngine.Contracts/VerdictBand.cs`
- `TelemetryGuard.RiskEngine.Contracts/TelemetryGuard.RiskEngine.Contracts.csproj` (only if FND-01 did not create it)
- `tests/TelemetryGuard.Tests.Unit/RiskContracts/BandMapperTests.cs`
- `tests/TelemetryGuard.Tests.Unit/RiskContracts/FraudFeatureVectorDefaultsTests.cs`
- `tests/TelemetryGuard.Tests.Unit/RiskContracts/ScoreResultTests.cs`
- `tests/TelemetryGuard.Tests.Unit/TelemetryGuard.Tests.Unit.csproj` (add project reference)

## Acceptance criteria

- `dotnet build TelemetryGuard.sln` succeeds with zero warnings from the Contracts project.
- `TelemetryGuard.RiskEngine.Contracts.csproj` has NO `PackageReference` entries.
- `FraudFeatureVector` contains all 45 properties listed above (14 T1 + 14 T2 + 10 T3 + 7 CTX) plus the `FeatureSetVersion` const, with the exact names, types, defaults, and tier-grouping comments; `FeatureSetVersion` equals `1`.
- `new FraudFeatureVector()` yields NaN for the ten SDK floats, 0 for the four velocity counters and `IpReputationBad`, null for all `bool?` members.
- `BandMapper.ToBand` maps 30→Allow, 31→Challenge, 70→Challenge, 71→Block and throws for -1 and 101.
- `dotnet test --filter "FullyQualifiedName~RiskContracts"` passes.
- The normative-contract XML doc note (absence of `fraud-signal-feature-spec.md`) is present on the record.

## Testing

Unit tests only (steps 6–8). No integration tests — this project is pure contracts.

## Out of scope / guardrails

- NO scoring logic, rule logic, extraction logic, or I/O here — contracts only. Rules live in RSK-05, heuristics in RSK-06, extraction in RSK-04.
- Do NOT add NuGet packages (no ML.NET, no Redis, no JSON attributes). Serialization concerns belong to consumers.
- Do NOT collapse `bool?` to `bool` or NaN floats to 0-defaults anywhere: missing ≠ zero is a hard spec constraint; LightGBM branches on NaN natively and `has_js_beacon` exists precisely so absence is a feature, not a default value.
- Do NOT make `tenant_id` part of this vector — tenancy flows through `ITenantContext` (FND-04) and is never an optional parameter; the vector is tenant-agnostic input to a per-tenant pipeline.
- Rules only RAISE scores (floor via Max) — nothing in these contracts may allow a rule to lower a score (e.g. no "negative floor" concept).
- Renaming/reordering members later requires bumping `FeatureSetVersion`; do not design for silent evolution.
- No server-side Python/Node anywhere (D1); .NET only.
