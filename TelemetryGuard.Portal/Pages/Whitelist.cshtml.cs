using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TelemetryGuard.Portal.Api;

namespace TelemetryGuard.Portal.Pages;

/// <summary>@page "/whitelist" — whitelist management: list/add/delete, fronting
/// API-07's /admin/whitelist endpoints directly (no session-id semantics differ
/// from the review screen: an empty sessionId on add means source='manual').</summary>
public sealed class WhitelistModel(IAdminApiClient api) : PortalPageModel
{
    private const int PageSize = 50;
    private static readonly string[] AllowedTypes = ["ip", "device_id", "fingerprint"];

    [BindProperty(SupportsGet = true)]
    public string? Type { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? Offset { get; set; }

    public AdminApiResult<IReadOnlyList<WhitelistEntryDto>>? EntriesResult { get; private set; }

    public bool ShowNext => EntriesResult?.Value?.Count == PageSize;

    [TempData]
    public string? Message { get; set; }

    [TempData]
    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (Type is not null && Array.IndexOf(AllowedTypes, Type) < 0)
            Type = null;
        Offset = Math.Max(Offset ?? 0, 0);

        EntriesResult = await api.ListWhitelistAsync(Type, Offset.Value, PageSize, ct);
        if (await HandleSessionExpiryAsync(EntriesResult) is { } expired) return expired;

        return Page();
    }

    public async Task<IActionResult> OnPostAddAsync(
        string type, string value, string? reason, string? sessionId, CancellationToken ct)
    {
        var trimmedSession = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
        var request = new AddWhitelistRequestDto(
            Type: type, Value: value, Reason: reason,
            Source: trimmedSession is null ? "manual" : "review_screen",
            SessionId: trimmedSession);

        var result = await api.AddWhitelistAsync(request, ct);
        if (await HandleSessionExpiryAsync(result) is { } expired) return expired;

        // The API's own validation messages (bad IP, bad session id, unknown type, …)
        // are rendered verbatim from its problem+json body — the portal never
        // invents validation text.
        Message = result.IsSuccess ? $"{value} added to the whitelist." : null;
        Error = result.IsSuccess ? null : result.Error;

        return RedirectToPage(new { Type, Offset });
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id, CancellationToken ct)
    {
        var result = await api.DeleteWhitelistAsync(id, ct);
        if (await HandleSessionExpiryAsync(result) is { } expired) return expired;

        Message = result.IsSuccess ? "Entry deleted." : null;
        Error = result.IsSuccess ? null : result.Error;

        return RedirectToPage(new { Type, Offset });
    }
}
