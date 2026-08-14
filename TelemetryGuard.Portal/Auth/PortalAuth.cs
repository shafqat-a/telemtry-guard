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
