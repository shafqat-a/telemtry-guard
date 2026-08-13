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
    /// <param name="ct">Cancellation token for the overall verification.</param>
    Task<TurnstileVerifyResult> VerifyAsync(string? token, string? remoteIp, CancellationToken ct);
}
