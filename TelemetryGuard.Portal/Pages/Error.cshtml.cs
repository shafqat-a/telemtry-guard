using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TelemetryGuard.Portal.Pages;

/// <summary>@page "/error" — the app's exception + status-code handler target
/// (UseExceptionHandler / UseStatusCodePagesWithReExecute in Program.cs). Never
/// renders exception details or the API key — only the status code, if any.</summary>
[AllowAnonymous]
[ResponseCache(NoStore = true)]
public sealed class ErrorModel : PageModel
{
    public string? Code { get; private set; }

    public void OnGet(string? code)
    {
        Code = code;
    }
}
