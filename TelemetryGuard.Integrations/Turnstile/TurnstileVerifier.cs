using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TelemetryGuard.Integrations.Turnstile;

/// <summary>
/// Typed-HttpClient implementation of <see cref="ITurnstileVerifier"/> against
/// Cloudflare's siteverify endpoint (spec D14). Per-attempt timeout with up to
/// <see cref="TurnstileOptions.MaxRetries"/> retries on transient failures;
/// fail-closed (unavailable) when siteverify cannot give a definitive answer.
/// </summary>
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
