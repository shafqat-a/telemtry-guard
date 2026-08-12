---
id: INT-01
title: Turnstile verification service
phase: 1
workstream: integrations
depends_on: [FND-01]
size: S
spec_refs: [D14, "§6.3", D3]
detail_level: full
---

# INT-01: Turnstile verification service

## Objective

Implement `ITurnstileVerifier` in `TelemetryGuard.Integrations`: a typed `HttpClient` service that verifies Cloudflare Turnstile challenge tokens server-side against `https://challenges.cloudflare.com/turnstile/v0/siteverify`, with a 2-second per-attempt timeout, up to 2 retries on transient failures, and fail-closed semantics on outage (an unverifiable challenge is treated as NOT passed). This is the server half of the 31–70 challenge band; API-05 (`POST /decide`) is the consumer.

## Spec context (self-contained)

- **D14 — Challenge = Cloudflare Turnstile.** Free, invisible-first, hosting-agnostic: a JS widget on the tenant page produces a one-time token; the backend verifies it via a server-side POST from C#. Turnstile serves the 31–70 score band (spec §6.3: 0–30 allow · 31–70 challenge, re-score with challenge outcome · 71–100 block).
- **Fail-closed on the challenge path**: if the siteverify endpoint is unreachable/erroring after retries, the challenge outcome is "unverified" → the caller must treat it exactly like a failed challenge (do NOT allow through). Log a warning so outages are visible. Fail-closed here never *lowers* any score — it only means a challenged session stays challenged/denied.
- **Platform-level keys for MVP**: one `SiteKey`/`SecretKey` pair for the whole platform, from configuration. Per-tenant Turnstile keys are a known later enhancement (note it in an XML doc comment; do NOT build per-tenant key storage now).
- **Hot-path discipline (D3)**: scoring has a <50 ms in-process budget. Turnstile verification is NOT part of the scoring hot path — it happens only on the `/decide` round-trip after a challenge was issued. Never call this service from the tracker (`/c`), pixel, or beacon paths.
- Backend is .NET end-to-end (D1); no server-side Python/Node.

### Turnstile siteverify wire contract (external API facts)

- Request: `POST https://challenges.cloudflare.com/turnstile/v0/siteverify`, body `application/x-www-form-urlencoded` with fields:
  - `secret` (required) — the secret key,
  - `response` (required) — the token the widget produced (client sends it to `/decide` as `cf-turnstile-response`),
  - `remoteip` (optional) — the visitor's IP.
- Response: JSON like `{"success": true, "challenge_ts": "2026-08-12T10:00:00.000Z", "hostname": "example.com", "error-codes": [], "action": "", "cdata": ""}`. Note the field name **`error-codes`** contains a hyphen — it needs an explicit `[JsonPropertyName("error-codes")]`.
- Known error codes: `missing-input-secret`, `invalid-input-secret`, `missing-input-response`, `invalid-input-response`, `bad-request`, `timeout-or-duplicate`, `internal-error`. Tokens are single-use and expire after 300 s — a replayed token yields `timeout-or-duplicate` with `success=false`. Do NOT retry on any definitive `success=false` answer; retry only network errors, timeouts, and HTTP 5xx.

## Prerequisites

After FND-01 completes, the repo contains everything this task needs:

- `TelemetryGuard.Integrations` classlib (net8.0) at `/home/shafqat/git/shafqat/telemtry-guard/TelemetryGuard.Integrations/`, referencing `TelemetryGuard.Core` (FND-01).
- `tests/TelemetryGuard.Tests.Unit` xunit project.

API-side wiring is deliberately NOT a prerequisite: the `Turnstile` appsettings section (API-01 seeds `SiteKey`/`SecretKey`/`VerifyUrl`) and the `Program.cs` registration line ship with **API-05** — the consumer, which depends on both API-01 and this task. Steps 6–7 below record that wiring as API-05's contract, keeping this self-contained IHttpClientFactory wrapper off the API-01 chain's critical path.

## Implementation steps

1. **Add NuGet packages** to `TelemetryGuard.Integrations/TelemetryGuard.Integrations.csproj`:
   ```bash
   dotnet add TelemetryGuard.Integrations package Microsoft.Extensions.Http
   dotnet add TelemetryGuard.Integrations package Microsoft.Extensions.Options.ConfigurationExtensions
   dotnet add TelemetryGuard.Integrations package Microsoft.Extensions.Logging.Abstractions
   ```
   (Latest stable versions; `System.Text.Json` comes with the framework.)

2. **Create `TelemetryGuard.Integrations/Turnstile/TurnstileOptions.cs`:**

   ```csharp
   namespace TelemetryGuard.Integrations.Turnstile;

   /// <summary>
   /// Platform-level Turnstile configuration (spec D14). MVP uses ONE key pair for the
   /// whole platform; per-tenant keys are a known later enhancement — when that lands,
   /// the secret becomes a per-tenant lookup and this options class keeps the defaults.
   /// Bound from configuration section "Turnstile" (see TelemetryGuard.Api/appsettings.json).
   /// </summary>
   public sealed class TurnstileOptions
   {
       public const string SectionName = "Turnstile";

       /// <summary>Public site key rendered into the widget (used by API-05 / SDK; not used for verification).</summary>
       public string SiteKey { get; set; } = "";

       /// <summary>Secret key for siteverify. Empty in dev ⇒ every verification returns unavailable (fail-closed).</summary>
       public string SecretKey { get; set; } = "";

       public string VerifyUrl { get; set; } = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

       /// <summary>Per-attempt timeout in seconds.</summary>
       public int TimeoutSeconds { get; set; } = 2;

       /// <summary>Retries AFTER the first attempt (2 ⇒ up to 3 attempts total).</summary>
       public int MaxRetries { get; set; } = 2;
   }
   ```

3. **Create `TelemetryGuard.Integrations/Turnstile/ITurnstileVerifier.cs`:**

   ```csharp
   namespace TelemetryGuard.Integrations.Turnstile;

   /// <summary>
   /// Outcome of a server-side Turnstile token verification.
   /// Success == false covers BOTH "Cloudflare said no" and "we could not verify"
   /// (outage after retries) — the challenge path is fail-closed either way.
   /// ErrorCodes contains Cloudflare's error-codes verbatim, or the synthetic code
   /// "tg-verifier-unavailable" when siteverify was unreachable/erroring.
   /// </summary>
   public sealed record TurnstileVerifyResult(
       bool Success,
       IReadOnlyList<string> ErrorCodes,
       string? Hostname = null,
       DateTimeOffset? ChallengeTimestamp = null)
   {
       public const string UnavailableErrorCode = "tg-verifier-unavailable";
       public bool WasUnavailable => ErrorCodes.Contains(UnavailableErrorCode);
   }

   /// <summary>Server-side Turnstile verification (spec D14). Consumed by API-05 (/decide).
   /// NEVER call from the tracker/pixel/beacon hot paths (D3 &lt;50 ms budget).</summary>
   public interface ITurnstileVerifier
   {
       /// <param name="token">The widget token (cf-turnstile-response). Null/empty short-circuits
       /// to a failed result with error code "missing-input-response" — no HTTP call.</param>
       /// <param name="remoteIp">The visitor's IP, when known (optional, forwarded as remoteip).</param>
       Task<TurnstileVerifyResult> VerifyAsync(string? token, string? remoteIp, CancellationToken ct);
   }
   ```

4. **Create `TelemetryGuard.Integrations/Turnstile/TurnstileVerifier.cs`:**

   ```csharp
   using System.Text.Json;
   using System.Text.Json.Serialization;
   using Microsoft.Extensions.Logging;
   using Microsoft.Extensions.Options;

   namespace TelemetryGuard.Integrations.Turnstile;

   public sealed class TurnstileVerifier(
       HttpClient http, IOptions<TurnstileOptions> options, ILogger<TurnstileVerifier> logger)
       : ITurnstileVerifier
   {
       private sealed class SiteverifyResponse
       {
           [JsonPropertyName("success")] public bool Success { get; set; }
           [JsonPropertyName("error-codes")] public string[]? ErrorCodes { get; set; }
           [JsonPropertyName("hostname")] public string? Hostname { get; set; }
           [JsonPropertyName("challenge_ts")] public DateTimeOffset? ChallengeTs { get; set; }
       }

       private static readonly TurnstileVerifyResult Unavailable =
           new(false, new[] { TurnstileVerifyResult.UnavailableErrorCode });

       public async Task<TurnstileVerifyResult> VerifyAsync(
           string? token, string? remoteIp, CancellationToken ct)
       {
           var opts = options.Value;
           if (string.IsNullOrEmpty(token))
               return new TurnstileVerifyResult(false, new[] { "missing-input-response" });
           if (string.IsNullOrEmpty(opts.SecretKey))
           {
               logger.LogWarning("Turnstile SecretKey is not configured; treating challenge as unverified (fail-closed).");
               return Unavailable;
           }

           var attempts = 1 + Math.Max(0, opts.MaxRetries);
           for (var attempt = 1; attempt <= attempts; attempt++)
           {
               try
               {
                   using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                   timeoutCts.CancelAfter(TimeSpan.FromSeconds(opts.TimeoutSeconds));

                   var form = new List<KeyValuePair<string, string>>
                   {
                       new("secret", opts.SecretKey),
                       new("response", token)
                   };
                   if (!string.IsNullOrEmpty(remoteIp)) form.Add(new("remoteip", remoteIp));

                   using var resp = await http.PostAsync(
                       opts.VerifyUrl, new FormUrlEncodedContent(form), timeoutCts.Token);

                   if ((int)resp.StatusCode >= 500)
                       throw new HttpRequestException($"siteverify returned {(int)resp.StatusCode}");

                   // 4xx is a definitive answer shape too (bad-request etc.) — parse, don't retry.
                   await using var stream = await resp.Content.ReadAsStreamAsync(timeoutCts.Token);
                   var body = await JsonSerializer.DeserializeAsync<SiteverifyResponse>(stream, cancellationToken: timeoutCts.Token);
                   if (body is null) throw new HttpRequestException("siteverify returned empty/invalid JSON");

                   return new TurnstileVerifyResult(
                       body.Success, body.ErrorCodes ?? Array.Empty<string>(),
                       body.Hostname, body.ChallengeTs);
               }
               catch (OperationCanceledException) when (ct.IsCancellationRequested)
               {
                   throw; // caller cancelled — propagate, don't swallow into "unavailable"
               }
               catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
               {
                   if (attempt < attempts)
                   {
                       await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), ct);
                       continue;
                   }
                   logger.LogWarning(ex,
                       "Turnstile siteverify unavailable after {Attempts} attempts; challenge treated as unverified (fail-closed).",
                       attempts);
                   return Unavailable;
               }
           }
           return Unavailable; // unreachable, satisfies compiler
       }
   }
   ```

5. **Create `TelemetryGuard.Integrations/Turnstile/TurnstileServiceCollectionExtensions.cs`:**

   ```csharp
   using Microsoft.Extensions.Configuration;
   using Microsoft.Extensions.DependencyInjection;

   namespace TelemetryGuard.Integrations.Turnstile;

   public static class TurnstileServiceCollectionExtensions
   {
       public static IServiceCollection AddTurnstileVerification(
           this IServiceCollection services, IConfiguration configuration)
       {
           services.Configure<TurnstileOptions>(configuration.GetSection(TurnstileOptions.SectionName));
           services.AddHttpClient<ITurnstileVerifier, TurnstileVerifier>(c =>
           {
               // Per-attempt timeout is enforced inside the verifier via a linked CTS;
               // this outer timeout is a safety net only.
               c.Timeout = TimeSpan.FromSeconds(10);
           });
           return services;
       }
   }
   ```

6. **Registration — DEFERRED to API-05** (recorded here as its contract; do not touch `TelemetryGuard.Api` in this task). API-05 adds to `TelemetryGuard.Api/Program.cs` (services section, near the other integration registrations):
   ```csharp
   using TelemetryGuard.Integrations.Turnstile;
   // ...
   builder.Services.AddTurnstileVerification(builder.Configuration);
   ```

7. **Appsettings — DEFERRED to API-05.** API-05 extends the `TelemetryGuard.Api/appsettings.json` `Turnstile` section with the two new keys (keeping API-01's existing three):
   ```json
   "Turnstile": {
     "SiteKey": "",
     "SecretKey": "",
     "VerifyUrl": "https://challenges.cloudflare.com/turnstile/v0/siteverify",
     "TimeoutSeconds": 2,
     "MaxRetries": 2
   }
   ```

8. **Unit tests** — `tests/TelemetryGuard.Tests.Unit/Integrations/TurnstileVerifierTests.cs` using a scripted `HttpMessageHandler` fake (no real network):

   ```csharp
   private sealed class StubHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> responder)
       : HttpMessageHandler
   {
       public int Calls;
       public List<string> CapturedBodies { get; } = new();
       protected override async Task<HttpResponseMessage> SendAsync(
           HttpRequestMessage request, CancellationToken ct)
       {
           Calls++;
           if (request.Content is not null)
               CapturedBodies.Add(await request.Content.ReadAsStringAsync(ct));
           return await responder(request, Calls);
       }
   }
   ```
   Construct the verifier directly: `new TurnstileVerifier(new HttpClient(handler), Options.Create(opts), NullLogger<TurnstileVerifier>.Instance)`.

## Files to create or modify

- `TelemetryGuard.Integrations/TelemetryGuard.Integrations.csproj` (add packages)
- `TelemetryGuard.Integrations/Turnstile/TurnstileOptions.cs`
- `TelemetryGuard.Integrations/Turnstile/ITurnstileVerifier.cs`
- `TelemetryGuard.Integrations/Turnstile/TurnstileVerifier.cs`
- `TelemetryGuard.Integrations/Turnstile/TurnstileServiceCollectionExtensions.cs`
- `TelemetryGuard.Api/Program.cs` (deferred to API-05 — step 6 records the one registration line)
- `TelemetryGuard.Api/appsettings.json` (deferred to API-05 — step 7 records the `Turnstile` section)
- `tests/TelemetryGuard.Tests.Unit/Integrations/TurnstileVerifierTests.cs`

## Acceptance criteria

- `dotnet build TelemetryGuard.sln` succeeds with zero warnings.
- `dotnet test tests/TelemetryGuard.Tests.Unit --filter "FullyQualifiedName~TurnstileVerifier"` passes, covering at least:
  - `{"success":true,...}` → `Success == true`, empty `ErrorCodes`, hostname/challenge_ts populated.
  - `{"success":false,"error-codes":["invalid-input-response"]}` → `Success == false`, error code surfaced verbatim, exactly **1** HTTP call (no retry on definitive answers).
  - HTTP 500 then HTTP 500 then `{"success":true}` → `Success == true`, exactly 3 calls (1 + 2 retries).
  - Handler that always throws `HttpRequestException` → result is `Success == false` with `ErrorCodes == ["tg-verifier-unavailable"]`, `WasUnavailable == true`, exactly 3 calls.
  - Handler that delays past `TimeoutSeconds` → unavailable result (per-attempt timeout fires), not a hang.
  - Null/empty token → `Success == false`, `ErrorCodes == ["missing-input-response"]`, **0** HTTP calls.
  - Empty `SecretKey` → unavailable result, 0 HTTP calls.
  - Captured request body contains `secret=`, `response=` and (when supplied) `remoteip=` form fields, content type `application/x-www-form-urlencoded`.
- The secret key never appears in any log message (grep test code + implementation: no logging of `opts.SecretKey` or the form body).
- Once API-05 has wired the service: `grep -rn "siteverify" TelemetryGuard.Api/` shows no direct HTTP calls — only `ITurnstileVerifier` is consumed. (Not checkable before the Api wiring exists; this task builds and tests the Integrations classlib alone.)

## Testing

Unit tests only (step 8) with the mocked handler; no Testcontainers, no live Cloudflare calls anywhere in CI. API-05's own tests will fake `ITurnstileVerifier` at the interface level — keep the interface exactly as specified so that seam holds.

## Out of scope / guardrails

- **Fail-closed, never fail-open**: an unavailable verifier must never be interpreted as a passed challenge. Conversely, nothing here touches scores — rules/challenge outcomes only ever RAISE or maintain enforcement; a failed verification must not lower any score or band.
- **Not on the scoring hot path**: never call `VerifyAsync` from `/c`, `/p.gif`, `/i`, or inside feature extraction/scoring (D3 <50 ms budget). Only `/decide` (API-05) consumes it.
- **No per-tenant key storage** — platform-level options only for MVP; the per-tenant extension is a documented note, not code.
- **No extra resilience libraries** (Polly etc.) — the hand-rolled retry loop above is the whole policy; keep the dependency surface small.
- **No EF Core, no SQL, no Redis** in this task — it is a pure HTTP integration. No server-side Python/Node (D1).
- Do not implement the `/decide` endpoint, challenge-token issuance, or re-scoring — that is API-05.
- `TenantId` plays no role here (platform-level verification); do NOT add an optional tenant parameter "for later" — when per-tenant keys arrive they come via a required contract change, never an optional tenant (D11 discipline).
