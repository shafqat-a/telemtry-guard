using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TelemetryGuard.Portal.Api;

namespace TelemetryGuard.Portal.Pages;

/// <summary>@page "/review" — the D19 override screen, source-level (per
/// doc/tasks/P2-03's spec context: per-event review would require reading raw
/// events, which D23 forbids; this reviews dbo.FlaggedSourcesDaily rows instead).</summary>
public sealed partial class ReviewModel(IAdminApiClient api) : PortalPageModel
{
    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex SessionIdShape();

    private static readonly string[] WhitelistableSourceTypes = ["ip", "device_id", "fingerprint"];

    [BindProperty(SupportsGet = true)]
    public DateOnly? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? To { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? Limit { get; set; }

    public AdminApiResult<FlaggedSourcesReportDto>? FlaggedResult { get; private set; }

    [TempData]
    public string? Message { get; set; }

    [TempData]
    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        To ??= DateOnly.FromDateTime(DateTime.UtcNow);
        From ??= To.Value.AddDays(-6);
        Limit ??= 100;

        FlaggedResult = await api.GetFlaggedSourcesAsync(From.Value, To.Value, Limit.Value, ct);
        if (await HandleSessionExpiryAsync(FlaggedResult) is { } expired) return expired;

        return Page();
    }

    public async Task<IActionResult> OnPostOverrideAsync(
        string sourceType, string value, string? sessionId, string? reason, CancellationToken ct)
    {
        // dbo.WhitelistEntries.SourceType allows ip|device_id|fingerprint only —
        // 'placement' rows (allowed by dbo.FlaggedSourcesDaily's CHECK) are NOT
        // whitelistable and the view renders an explanation instead of a button.
        if (Array.IndexOf(WhitelistableSourceTypes, sourceType) < 0)
        {
            Error = "Placements cannot be whitelisted — reject the pending exclusion "
                  + "in the approval queue instead.";
            return RedirectToPage(new { From, To, Limit });
        }

        // D19: a session id turns this into a review_screen add, and API-07 -> DAT-07's
        // WhitelistRepository.AddAsync emits exactly one negative training label
        // (LabelValues.Legit / LabelSources.ReviewScreen). Without one the API would
        // reject source='review_screen' (its sessionId rule), so we send 'manual' and NO
        // label is written — say so in the UI rather than faking a label.
        var trimmedSession = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
        if (trimmedSession is not null && !SessionIdShape().IsMatch(trimmedSession))
        {
            Error = "Session id must match ^[A-Za-z0-9_-]{8,64}$.";
            return RedirectToPage(new { From, To, Limit });
        }

        var request = new AddWhitelistRequestDto(
            Type: sourceType,
            Value: value,
            Reason: Truncate(reason ?? "Marked real customer in portal review", 400), // nvarchar(400)
            Source: trimmedSession is null ? "manual" : "review_screen",
            SessionId: trimmedSession);

        var result = await api.AddWhitelistAsync(request, ct);
        if (await HandleSessionExpiryAsync(result) is { } expired) return expired;

        if (result.IsSuccess)
        {
            Message = trimmedSession is null
                ? $"{value} whitelisted (no training label — no session id supplied)."
                : $"{value} whitelisted and a negative training label was recorded for session {trimmedSession}.";
        }
        else
        {
            Error = result.Error;
        }

        return RedirectToPage(new { From, To, Limit });
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
