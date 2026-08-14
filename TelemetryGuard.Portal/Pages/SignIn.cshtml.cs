using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using TelemetryGuard.Portal.Api;
using TelemetryGuard.Portal.Auth;
using TelemetryGuard.Portal.Options;

namespace TelemetryGuard.Portal.Pages;

/// <summary>@page "/signin" — the portal's only sign-in surface (D24: API-key-only
/// auth). Throttled per-IP because the admin API itself has no browser-facing rate
/// limiter on X-Api-Key resolution (see the class doc for the throttle rationale).</summary>
[AllowAnonymous]
public sealed class SignInModel(IAdminApiClient api, IMemoryCache cache, IOptions<PortalOptions> options) : PageModel
{
    private const string ThrottleKeyPrefix = "signin:";

    [BindProperty]
    public string? ApiKey { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? Error { get; private set; }

    public void OnGet(string? returnUrl)
    {
        ReturnUrl = returnUrl;
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl, CancellationToken ct)
    {
        ReturnUrl = returnUrl;

        var throttleKey = ThrottleKeyPrefix + (HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        var attempts = cache.TryGetValue<int>(throttleKey, out var existing) ? existing : 0;

        if (attempts >= options.Value.SignInAttemptsPerIpPer5Min)
        {
            // The API rejects unresolvable keys in middleware *before* its own rate
            // limiter, so this in-portal throttle is the only brake on key guessing —
            // do NOT call the API once tripped.
            Error = "Too many attempts. Wait a few minutes.";
            return Page();
        }

        var key = ApiKey?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            Error = "Enter your admin API key.";
            return Page();
        }

        var valid = await api.ValidateKeyAsync(key, ct);
        if (!valid)
        {
            cache.Set(throttleKey, attempts + 1, TimeSpan.FromMinutes(5));
            Error = "That API key was rejected by the admin API.";
            return Page();
        }

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            PortalAuth.PrincipalFor(key),
            new AuthenticationProperties { IsPersistent = false });

        return LocalRedirect(SafeReturn(returnUrl));
    }

    /// <summary>Open-redirect guard, same discipline as API-02's LandingUrl rule.</summary>
    private string SafeReturn(string? returnUrl)
        => Url.IsLocalUrl(returnUrl) ? returnUrl! : "/";
}
