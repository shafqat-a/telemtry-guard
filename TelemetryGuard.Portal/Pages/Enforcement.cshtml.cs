using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TelemetryGuard.Portal.Api;

namespace TelemetryGuard.Portal.Pages;

/// <summary>@page "/enforcement" — INT-02's approval queue: pending -> approved |
/// rejected. Nothing here ever pushes to an ad platform, and nothing here gates,
/// delays or disables the 31-70 mid-band Turnstile challenge (D21 — that band is
/// always automatic in both enforcement modes).</summary>
public sealed class EnforcementModel(IAdminApiClient api) : PortalPageModel
{
    /// <summary>All six dbo.ExclusionQueue statuses, CHECK-constraint order — mirrors
    /// TelemetryGuard.Data.Repositories.ExclusionStatuses.All. Re-declared, not
    /// referenced, because this project has zero project references (D23).</summary>
    public static readonly string[] AllStatuses =
        ["pending", "approved", "rejected", "pushed", "failed", "unsupported"];

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? Limit { get; set; }

    public AdminApiResult<IReadOnlyList<ExclusionQueueEntryDto>>? EntriesResult { get; private set; }

    [TempData]
    public string? Message { get; set; }

    [TempData]
    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (Status is null || Array.IndexOf(AllStatuses, Status) < 0)
            Status = "pending";
        Limit = Math.Clamp(Limit ?? 100, 1, 500);

        EntriesResult = await api.ListEnforcementAsync(Status, Limit.Value, ct);
        if (await HandleSessionExpiryAsync(EntriesResult) is { } expired) return expired;

        return Page();
    }

    public async Task<IActionResult> OnPostApproveAsync(long[] ids, CancellationToken ct)
    {
        if (ids.Length == 0)
        {
            Error = "Tick the rows you want to act on.";
            return RedirectToPage(new { Status, Limit });
        }

        var result = await api.ApproveEnforcementAsync(ids, ct);
        if (await HandleSessionExpiryAsync(result) is { } expired) return expired;

        if (result.IsSuccess && result.Value is { } value)
        {
            Message = $"{value.Approved} of {value.Requested} approved. Rows that were no longer pending were skipped.";
        }
        else
        {
            Error = result.Error;
        }

        return RedirectToPage(new { Status, Limit });
    }

    public async Task<IActionResult> OnPostRejectAsync(long[] ids, string? note, CancellationToken ct)
    {
        if (ids.Length == 0)
        {
            Error = "Tick the rows you want to act on.";
            return RedirectToPage(new { Status, Limit });
        }

        var result = await api.RejectEnforcementAsync(ids, note, ct);
        if (await HandleSessionExpiryAsync(result) is { } expired) return expired;

        if (result.IsSuccess && result.Value is { } value)
        {
            Message = $"{value.Rejected} of {value.Requested} rejected. Rows that were no longer pending were skipped.";
        }
        else
        {
            Error = result.Error;
        }

        return RedirectToPage(new { Status, Limit });
    }
}
