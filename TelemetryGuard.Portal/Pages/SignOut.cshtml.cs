using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TelemetryGuard.Portal.Pages;

/// <summary>@page "/signout" — POST-only sign-out handler. Reachable only while
/// authenticated (Program.cs's AuthorizeFolder("/") covers this page; it is not in
/// the AllowAnonymousToPage list, matching the fact that signing out only makes
/// sense from an authenticated session).</summary>
public sealed class SignOutModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/SignIn");

    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToPage("/SignIn");
    }
}
