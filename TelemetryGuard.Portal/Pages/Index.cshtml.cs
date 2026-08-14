using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TelemetryGuard.Portal.Api;

namespace TelemetryGuard.Portal.Pages;

/// <summary>@page "/" — the dashboard: per-campaign verdict summaries,
/// exclusion-queue counts (pending/failed/unsupported), and the D22 per-site
/// integration-level panel. Every panel calls the admin API independently and
/// renders its own error without failing the rest of the page (no 500 on a
/// partial outage).</summary>
public sealed class IndexModel(IAdminApiClient api) : PortalPageModel
{
    /// <summary>API-06's sentinel for campaign-less (pixel/organic) sessions.</summary>
    public static readonly Guid NoCampaign = Guid.Empty;

    [BindProperty(SupportsGet = true)]
    public Guid? CampaignId { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? To { get; set; }

    public AdminApiResult<CampaignListDto>? CampaignsResult { get; private set; }
    public AdminApiResult<SummaryReportDto>? SummaryResult { get; private set; }
    public AdminApiResult<IReadOnlyList<ExclusionQueueEntryDto>>? PendingResult { get; private set; }
    public AdminApiResult<IReadOnlyList<ExclusionQueueEntryDto>>? FailedResult { get; private set; }
    public AdminApiResult<IReadOnlyList<ExclusionQueueEntryDto>>? UnsupportedResult { get; private set; }
    public AdminApiResult<IntegrationStatusReportDto>? IntegrationResult { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        To ??= DateOnly.FromDateTime(DateTime.UtcNow);
        From ??= To.Value.AddDays(-13);

        CampaignsResult = await api.ListCampaignsAsync(ct);
        if (await HandleSessionExpiryAsync(CampaignsResult) is { } expired1) return expired1;

        CampaignId ??= CampaignsResult.Value?.Campaigns.FirstOrDefault()?.CampaignId ?? NoCampaign;

        SummaryResult = await api.GetSummaryAsync(CampaignId.Value, From.Value, To.Value, ct);
        if (await HandleSessionExpiryAsync(SummaryResult) is { } expired2) return expired2;

        PendingResult = await api.ListEnforcementAsync("pending", 500, ct);
        if (await HandleSessionExpiryAsync(PendingResult) is { } expired3) return expired3;

        FailedResult = await api.ListEnforcementAsync("failed", 500, ct);
        if (await HandleSessionExpiryAsync(FailedResult) is { } expired4) return expired4;

        UnsupportedResult = await api.ListEnforcementAsync("unsupported", 500, ct);
        if (await HandleSessionExpiryAsync(UnsupportedResult) is { } expired5) return expired5;

        IntegrationResult = await api.GetIntegrationStatusAsync(ct);
        if (await HandleSessionExpiryAsync(IntegrationResult) is { } expired6) return expired6;

        return Page();
    }

    public static string CampaignLabel(CampaignSummaryDto c)
    {
        var label = $"{c.Platform} · {c.ExternalCampaignId ?? "(no external id)"} · {c.CampaignId:D}";
        return c.Status switch
        {
            1 => label + " (paused)",
            2 => label + " (archived)",
            _ => label,
        };
    }

    public static string CountBadge(int? count) => count switch
    {
        null => "—",
        500 => "500+",
        _ => count.Value.ToString(),
    };
}
