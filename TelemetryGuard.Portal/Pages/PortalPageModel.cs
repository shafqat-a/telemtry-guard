using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TelemetryGuard.Portal.Api;

namespace TelemetryGuard.Portal.Pages;

/// <summary>Shared behavior for every authenticated Razor page: a single place to
/// react to a mid-session key rejection (401/403 from the admin API) by signing the
/// operator out and sending them back to /signin. One rejected call must never
/// 500 the page — callers render <c>result.Error</c> in their panel otherwise.</summary>
public abstract class PortalPageModel : PageModel
{
    /// <summary>Returns a redirect-to-signin result (after signing the operator out)
    /// when <paramref name="result"/>.SessionExpired is true; otherwise null, meaning
    /// the caller should continue rendering the page with result.Error in its panel.</summary>
    protected async Task<IActionResult?> HandleSessionExpiryAsync<T>(AdminApiResult<T> result)
    {
        if (!result.SessionExpired)
            return null;

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToPage("/SignIn");
    }
}
