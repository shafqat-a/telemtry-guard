---
id: INT-05
title: Cloudflare fronting and TLS fingerprint intake
phase: 1.5
workstream: integrations
depends_on: [API-02, RSK-04, API-03, API-04]
size: M
spec_refs: [D13, D3, "§7 tls_ua_mismatch", "§4 component 3"]
detail_level: full
---

# INT-05: Cloudflare fronting and TLS fingerprint intake

## Objective

Put Cloudflare in front of the API and make its edge signals (JA3/JA4 TLS fingerprints, ASN, bot score) flow into scoring: an ops runbook for DNS proxying + SSL Full (strict) + origin lockdown, a Cloudflare Worker snippet that forwards `request.cf` signals as `X-TG-*` headers (stripping inbound spoofs), and the API side — Cloudflare IP ranges merged into the ForwardedHeaders trusted-proxy set (embedded list + refresh script), an `Edge:Provider` config flag gating header intake, and population of `ClickEvent.TlsJa3/TlsJa4/CfAsn` plus the Redis click-context fields RSK-04 reads to compute `tls_ua_mismatch`. Includes a mandatory honesty table: most of these signals require Cloudflare Bot Management (Enterprise), not the free tier the spec optimistically implies.

## Spec context (self-contained)

- **D13 — Cloudflare in front (even in front of Azure)**: Azure Front Door does not pass JA3/JA4 to origin; Cloudflare hands TLS fingerprints, bot scores, and ASN in request metadata — powering the T1 `tls_ua_mismatch` signal ("UA says Chrome, TLS handshake says Go binary"). If Cloudflare is absent or the plan lacks a signal, **the signal degrades to NaN/null — the model tolerates it** (this graceful degradation is the load-bearing design fact of this task).
- **HONESTY TABLE (required by this task — normative for the runbook and tenant docs).** What `request.cf` actually provides, by plan:

  | Signal | `request.cf` field | Header set by our Worker | Plan required |
  |---|---|---|---|
  | ASN of client | `cf.asn` | `X-TG-ASN` | **All plans, including Free** |
  | Bot score (1–99) | `cf.botManagement.score` | `X-TG-Bot-Score` | **Bot Management add-on (Enterprise)** |
  | JA3 hash | `cf.botManagement.ja3Hash` | `X-TG-JA3` | **Bot Management (Enterprise)** |
  | JA4 | `cf.botManagement.ja4` | `X-TG-JA4` | **Bot Management (Enterprise)** |

  The spec's "free tier upward" wording (D13) is **optimistic**: on Free/Pro/Business only ASN (plus country, TLS version, etc.) is available; JA3/JA4 and bot score require Enterprise Bot Management. Consequence: on non-Enterprise plans `tls_ua_mismatch` stays `null` and `TlsJa3/TlsJa4` stay null — by design (spec §7 null semantics: missing ≠ zero; `FraudFeatureVector.TlsUaMismatch` is `bool?`, null = no TLS fingerprint header, RSK-01).
- **Spoofing defense is two-layered and both layers are mandatory**: (1) the Worker deletes any client-supplied `X-TG-*` headers before setting its own; (2) the origin honors `X-TG-*` and `X-Forwarded-For` ONLY when `Edge:Provider == "Cloudflare"` AND the direct peer is a Cloudflare address — plus the runbook's origin lockdown (firewall to CF ranges / Authenticated Origin Pulls), because an attacker hitting the origin directly must not be able to inject fingerprints or IPs.
- **API-01 contract**: ForwardedHeaders trusts only configured CIDRs; `ForwardedHeaders:TrustedProxyCidrs` ships empty and *"INT-05 owns populating it"* with Cloudflare ranges. Keep it config-driven — the embedded list is merged at startup when the provider is Cloudflare, never hardcoded at call sites.
- **The Worker is edge configuration, not backend runtime**: D1 forbids server-side Python/Node in *our* backend; a Cloudflare Worker is Cloudflare's deployment artifact (same exception class as the D2 browser SDK). It lives under `infra/cloudflare/` and is never executed by our services.
- Hot-path budget (D3): header reads are in-memory; this task adds zero I/O to any request path.

## Prerequisites

- **API-02** (read its task file): `TelemetryGuard.Api/Endpoints/TrackerEndpoints.cs` captures HTTP signals, writes the Redis click-context hash `t:{tid}:click:{sid}` (fields like `ip`, `ua`, `header_order`, empty string = absent) and emits `ClickEvent` (kind `tracker`). **API-03** (`/p.gif`) and **API-04** (`/i`, `/i/init`) are declared dependencies of this task (their endpoint files are modified in step 7) and also build `ClickEvent`s.
- **RSK-04** (read its task file): feature extraction computes `FraudFeatureVector.TlsUaMismatch` (`bool?`, RSK-01) from the TLS fingerprint + UA; verify the exact click-context field names it reads (expected `tls_ja3`, `tls_ja4`, `cf_asn`, `cf_bot_score`) and match them exactly — if RSK-04 chose different names, RSK-04's names win.
- **ANA-01**: `ClickEvent` already has the columns waiting: `TlsJa3 string?`, `TlsJa4 string?`, `CfAsn uint?` (null unless Cloudflare-fronted). There is NO bot-score column in `ClickEvent` v1 — the bot score feeds scoring via the click context only (see guardrails).
- **API-01**: `ForwardedHeadersOptions` wiring reading `ForwardedHeaders:TrustedProxyCidrs`, and `appsettings.json` as the options aggregation point.

## Implementation steps

1. **Runbook** — create `doc/runbooks/cloudflare-fronting.md` covering, in order:
   1. DNS: proxied (orange-cloud) `A`/`CNAME` records for the API host(s) (tracker + ingestion share one host at MVP).
   2. SSL/TLS: mode **Full (strict)**; origin certificate installed on the host (Cloudflare Origin CA cert is fine); minimum TLS 1.2.
   3. **Origin lockdown**: firewall inbound 443 to Cloudflare ranges (`https://www.cloudflare.com/ips/`) and/or enable Authenticated Origin Pulls; state plainly that without lockdown, direct-to-origin requests bypass every edge signal and the spoof-rejection below is the only defense.
   4. Cache/feature bypass: Cache Rules to BYPASS `/c`, `/p.gif`, `/i*`, `/decide`, `/admin*`; disable Rocket Loader & email obfuscation for these paths (they must not rewrite responses).
   5. Worker deployment: `wrangler deploy` of `infra/cloudflare/tg-edge-worker.js` with a route covering the API hostname (`track.example.com/*`).
   6. The honesty table above, verbatim, plus: "On non-Enterprise plans expect only `X-TG-ASN`; `tls_ua_mismatch` remains null and scoring proceeds — degraded, not broken (D13)."
   7. App config: set `Edge:Provider` to `Cloudflare` in the deployed environment (and only there — local dev stays `None`).

2. **Worker** — create `infra/cloudflare/tg-edge-worker.js`:

   ```js
   // TelemetryGuard edge worker (INT-05). Deployed to Cloudflare via wrangler — this is
   // edge configuration, NOT part of the .NET backend (see doc/spec.md D1/D13).
   // Forwards request.cf signals to origin as X-TG-* headers; strips inbound spoofs.
   export default {
     async fetch(request) {
       const h = new Headers(request.headers);
       // Anti-spoof: never trust client-supplied values for our signal headers.
       for (const n of ["X-TG-JA3", "X-TG-JA4", "X-TG-ASN", "X-TG-Bot-Score"]) h.delete(n);

       const cf = request.cf ?? {};
       if (cf.asn) h.set("X-TG-ASN", String(cf.asn));                    // all plans
       const bm = cf.botManagement ?? {};                                 // Enterprise Bot Management only
       if (bm.ja3Hash) h.set("X-TG-JA3", bm.ja3Hash);
       if (bm.ja4) h.set("X-TG-JA4", bm.ja4);
       if (typeof bm.score === "number") h.set("X-TG-Bot-Score", String(bm.score));

       return fetch(new Request(request, { headers: h }));
     }
   };
   ```
   Also add `infra/cloudflare/wrangler.toml` (name `tg-edge-worker`, `main = "tg-edge-worker.js"`, compatibility_date = today; route left as a commented example).

3. **Embedded Cloudflare ranges** — create `TelemetryGuard.Api/Edge/cloudflare-ips.txt` (mark as `<EmbeddedResource>` in the csproj), one CIDR per line, `#` comments allowed. Seed with the published list (stable for years; refresh via the script below):

   ```
   # https://www.cloudflare.com/ips-v4  (refresh: scripts/update-cloudflare-ips.sh)
   173.245.48.0/20
   103.21.244.0/22
   103.22.200.0/22
   103.31.4.0/22
   141.101.64.0/18
   108.162.192.0/18
   190.93.240.0/20
   188.114.96.0/20
   197.234.240.0/22
   198.41.128.0/17
   162.158.0.0/15
   104.16.0.0/13
   104.24.0.0/14
   172.64.0.0/13
   131.0.72.0/22
   # https://www.cloudflare.com/ips-v6
   2400:cb00::/32
   2606:4700::/32
   2803:f800::/32
   2405:b500::/32
   2405:8100::/32
   2a06:98c0::/29
   2c0f:f248::/32
   ```

   Create `scripts/update-cloudflare-ips.sh` (ops-time shell, not runtime — permitted; +x bit):

   ```bash
   #!/usr/bin/env bash
   set -euo pipefail
   out="$(dirname "$0")/../TelemetryGuard.Api/Edge/cloudflare-ips.txt"
   {
     echo "# https://www.cloudflare.com/ips-v4  (refresh: scripts/update-cloudflare-ips.sh)"
     curl -fsS https://www.cloudflare.com/ips-v4
     echo "# https://www.cloudflare.com/ips-v6"
     curl -fsS https://www.cloudflare.com/ips-v6
   } > "$out"
   echo "updated $out"
   ```

4. **Edge options + range loader** — create `TelemetryGuard.Api/Edge/EdgeOptions.cs` and `CloudflareIpRanges.cs`:

   ```csharp
   namespace TelemetryGuard.Api.Edge;

   public sealed class EdgeOptions
   {
       public const string SectionName = "Edge";
       /// <summary>"None" (default) or "Cloudflare". Gates ALL X-TG-* header intake and
       /// the merging of embedded Cloudflare CIDRs into the trusted-proxy set.</summary>
       public string Provider { get; set; } = "None";
       public bool IsCloudflare => string.Equals(Provider, "Cloudflare", StringComparison.OrdinalIgnoreCase);
   }

   public static class CloudflareIpRanges
   {
       /// <summary>Parses the embedded cloudflare-ips.txt into (address, prefix) pairs.</summary>
       public static IReadOnlyList<(System.Net.IPAddress Address, int PrefixLength)> Load()
       { /* read embedded resource "TelemetryGuard.Api.Edge.cloudflare-ips.txt",
            skip blanks/#, split on '/', parse */ }

       /// <summary>True when ip falls inside any Cloudflare range (v4 or v6).</summary>
       public static bool Contains(System.Net.IPAddress ip) { /* prefix match over Load() cache */ }
   }
   ```
   Add to `appsettings.json`: `"Edge": { "Provider": "None" }`.

5. **ForwardedHeaders merge** — in `TelemetryGuard.Api/Program.cs`, inside the existing `Configure<ForwardedHeadersOptions>` block (API-01), after the config-driven CIDR loop, add:

   ```csharp
   if (builder.Configuration.GetValue<string>("Edge:Provider")?.Equals("Cloudflare",
           StringComparison.OrdinalIgnoreCase) == true)
   {
       foreach (var (addr, prefix) in CloudflareIpRanges.Load())
           o.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(addr, prefix));
   }
   ```
   `ForwardedHeaders:TrustedProxyCidrs` remains available for additional proxies; local dev (`Provider: None`) keeps API-01's empty-trust behavior.

6. **Edge signal reader** — create `TelemetryGuard.Api/Edge/EdgeSignals.cs`:

   ```csharp
   namespace TelemetryGuard.Api.Edge;

   /// <summary>Signals forwarded by the edge worker. All-null when not Cloudflare-fronted,
   /// when the plan lacks Bot Management, or when the direct peer was not Cloudflare —
   /// downstream this is NaN/null, never zero (spec §7).</summary>
   public sealed record EdgeSignals(string? Ja3, string? Ja4, uint? Asn, int? BotScore)
   {
       public static readonly EdgeSignals None = new(null, null, null, null);
   }

   public interface IEdgeSignalReader { EdgeSignals Read(HttpContext ctx); }

   public sealed class EdgeSignalReader(IOptions<EdgeOptions> options) : IEdgeSignalReader
   {
       public EdgeSignals Read(HttpContext ctx)
       {
           if (!options.Value.IsCloudflare) return EdgeSignals.None;
           // Defense-in-depth: only honor headers when the ORIGINAL peer was Cloudflare.
           // After UseForwardedHeaders ran, the original peer address is preserved in the
           // X-Original-For header; before/without it, RemoteIpAddress IS the peer.
           var peer = GetOriginalPeer(ctx);
           if (peer is null || !CloudflareIpRanges.Contains(peer)) return EdgeSignals.None;

           string? H(string name) =>
               ctx.Request.Headers.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v) ? v.ToString() : null;
           return new EdgeSignals(
               H("X-TG-JA3"), H("X-TG-JA4"),
               uint.TryParse(H("X-TG-ASN"), out var asn) ? asn : null,
               int.TryParse(H("X-TG-Bot-Score"), out var bs) ? bs : null);
       }
       // GetOriginalPeer: parse ctx.Request.Headers["X-Original-For"] (set by the
       // ForwardedHeaders middleware when it swapped RemoteIpAddress; strip port),
       // falling back to ctx.Connection.RemoteIpAddress.
   }
   ```
   Register scoped-free: `builder.Services.AddSingleton<IEdgeSignalReader, EdgeSignalReader>();` and `builder.Services.Configure<EdgeOptions>(builder.Configuration.GetSection(EdgeOptions.SectionName));`.

7. **Wire into event capture** (all three capture endpoints build `ClickEvent`s):
   - `TrackerEndpoints.cs` (API-02): call `edgeSignals.Read(ctx)` once; add to the click-context hash the fields `tls_ja3`, `tls_ja4`, `cf_asn`, `cf_bot_score` (empty string when null, per API-02's absent-value convention — RSK-04 must see absence, not `"0"`); set `ClickEvent.TlsJa3/TlsJa4/CfAsn` from the same values.
   - `PixelEndpoints.cs` (API-03) and beacon endpoints (API-04): set the three `ClickEvent` fields the same way (no click-context write exists on paths that don't create it — match each endpoint's existing structure).
   Field names must match what RSK-04 reads (Prerequisites) — verify before writing, adjust to RSK-04 if it differs.

8. **RSK-04 handshake**: confirm RSK-04's `tls_ua_mismatch` computation activates purely on data presence (ja3/ja4 non-empty in the click context) and not on a separate flag; if RSK-04 gates on a config flag, point that flag at `Edge:Provider` rather than adding a second switch. No change to RSK-04's logic here — INT-05 only starts feeding it real values.

## Files to create or modify

- `doc/runbooks/cloudflare-fronting.md` (new)
- `infra/cloudflare/tg-edge-worker.js` (new)
- `infra/cloudflare/wrangler.toml` (new)
- `scripts/update-cloudflare-ips.sh` (new, executable)
- `TelemetryGuard.Api/Edge/cloudflare-ips.txt` (new, embedded resource)
- `TelemetryGuard.Api/Edge/EdgeOptions.cs` (new)
- `TelemetryGuard.Api/Edge/CloudflareIpRanges.cs` (new)
- `TelemetryGuard.Api/Edge/EdgeSignals.cs` (new: record + reader)
- `TelemetryGuard.Api/TelemetryGuard.Api.csproj` (embedded resource entry)
- `TelemetryGuard.Api/Program.cs` (Edge options, reader registration, ForwardedHeaders merge)
- `TelemetryGuard.Api/appsettings.json` (`Edge` section)
- `TelemetryGuard.Api/Endpoints/TrackerEndpoints.cs` (modify: context fields + ClickEvent fields)
- API-03 / API-04 endpoint files (modify: ClickEvent fields)
- `tests/TelemetryGuard.Tests.Unit/Api/EdgeSignalsTests.cs` (new)

## Acceptance criteria

- `dotnet build TelemetryGuard.sln` succeeds; `CloudflareIpRanges.Load()` returns 15 IPv4 + 7 IPv6 ranges from the embedded resource.
- With `Edge:Provider = "None"` (default): a request carrying `X-TG-JA3: aaaa` yields `EdgeSignals.None`; `ClickEvent.TlsJa3` is null; ForwardedHeaders trusts nothing extra (API-01's spoof test still passes).
- With `Edge:Provider = "Cloudflare"`:
  - A request whose peer is inside a Cloudflare range with `X-TG-JA3/JA4/ASN/Bot-Score` set → click-context hash contains `tls_ja3`, `tls_ja4`, `cf_asn`, `cf_bot_score`; the emitted `ClickEvent` has `TlsJa3/TlsJa4/CfAsn` populated.
  - The same headers from a NON-Cloudflare peer → all signals null (spoof rejected).
  - Headers absent (free-plan reality: only `X-TG-ASN`) → `Ja3/Ja4/BotScore` null, `Asn` set; nothing crashes; `ClickEvent.TlsJa3` null. Missing ≠ zero: no field is ever populated with `0`/`""`-as-value.
  - `X-Forwarded-For` from a Cloudflare-range peer updates `RemoteIpAddress`; from a non-CF peer it does not.
- Worker snippet: deleting inbound `X-TG-*` headers precedes setting them (read the file); no other headers are modified; the worker never blocks/redirects (fail-open at the edge — scoring happens at origin).
- Runbook contains the honesty table verbatim and the origin-lockdown section; `grep -i "free tier" doc/runbooks/cloudflare-fronting.md` shows the corrective note.
- `bash scripts/update-cloudflare-ips.sh` rewrites the txt file in the documented format (manual check; not run in CI).
- End-to-end (manual, Enterprise plan or header-injected staging): a curl with Chrome UA but a Go-client JA3 produces a verdict whose `FraudFeatureVector.TlsUaMismatch == true` (RSK-04's rule now live).

## Testing

- `EdgeSignalsTests.cs` (unit, `WebApplicationFactory<Program>` with `Edge:Provider` overridden per test + a test endpoint echoing `IEdgeSignalReader.Read`): provider-off ignores headers; provider-on + CF peer reads all four; provider-on + non-CF peer reads none; partial headers (ASN only); malformed `X-TG-ASN`/`X-TG-Bot-Score` → null (no exception); `X-Original-For` path (simulate ForwardedHeaders having run).
- `CloudflareIpRangesTests`: `Contains` true for `104.16.1.1` and `2606:4700::1`, false for `8.8.8.8`; comment/blank lines skipped.
- Tracker integration: extend API-02's endpoint tests asserting the four new click-context fields and three `ClickEvent` fields, present and absent cases.
- No test may call Cloudflare; the Worker is validated by reading + manual deploy (runbook step 5).

## Out of scope / guardrails

- **Never trust unverified edge headers**: intake requires `Edge:Provider == Cloudflare` AND a Cloudflare-range peer. Do not soften either check "for testing" — use config overrides in tests instead.
- **Missing ≠ zero (spec §7)**: absent JA3/JA4/bot score stays null/empty end-to-end → `TlsUaMismatch` stays `null` (never `false`-when-unknown, never 0). The whole design degrades, it never fabricates.
- **No `ClickEvent`/ClickHouse schema change**: `TlsJa3/TlsJa4/CfAsn` already exist (ANA-01/ANA-02); the bot score is click-context-only in this task — adding a `CfBotScore` column is a separate contract-versioned change (proposed separately), not a sneak edit here.
- **No scoring logic changes**: RSK-04 owns `tls_ua_mismatch` computation; RSK-05 owns the rule; rules only ever RAISE scores. This task is transport + gating only, and adds zero I/O to the hot path (<50 ms budget, D3).
- **The Worker stays dumb**: forward-only, never blocks/challenges at the edge (enforcement lives in our decision layer), no fetch to third parties, no state. It is edge config — do not add Node tooling to the backend build for it (D1).
- **No hardcoded CF ranges at call sites** — embedded resource + refresh script only; the runbook owns telling operators to refresh.
- Tenancy untouched: edge signals are per-request facts, never a tenant-resolution input; `TenantId` handling (DAT-04) is not modified. No EF Core, no new SQL (D9).
