using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using StackExchange.Redis;
using TelemetryGuard.Api.Auth;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Core.Time;
using TelemetryGuard.Data.Models;
using TelemetryGuard.Data.Repositories;

namespace TelemetryGuard.Api.Endpoints;

/// <summary>
/// API-07: the `/admin` route group (spec D19/D22/D23) — whitelist CRUD, a
/// read-only verdict summary report, and a per-site integration-status report.
/// Every route is gated by <see cref="AdminScopeFilter"/>; DAT-04's middleware
/// guarantees only X-Api-Key-resolved requests ever reach these handlers.
///
/// D23: reads here go ONLY through the small RLS-protected SQL aggregate tables
/// (<see cref="IVerdictSummaryRepository"/>, <see cref="IPublisherSummaryRepository"/>) —
/// never ClickHouse/IAnalyticsQueries.
/// D19: whitelist mutations go through DAT-07's <see cref="IWhitelistRepository"/>
/// exactly as-is; the Redis cache rebuild and the review-screen negative training
/// label are BOTH side effects of that repository's AddAsync/Remove* methods —
/// this file never touches Redis or ILabelSink for whitelist operations, which
/// would double-write the label and race the repository's own cache rebuild.
/// </summary>
public static partial class AdminEndpoints
{
    // Same sid shape as API-04/API-05's SDK-issued/organic session ids.
    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex SidShape();

    private static readonly string[] AllowedSourceTypes = ["ip", "device_id", "fingerprint"];
    private static readonly string[] AllowedSources = ["manual", "review_screen"];

    private const int MaxSummaryRangeDays = 366;
    private const int LastBeaconJsWindowSeconds = 24 * 60 * 60; // D22: "recent" = within 24h

    /// <summary>Below this many scored events a placement's FlaggedRatio is noise;
    /// the response flags it rather than hiding the row (§7 — surface the volume,
    /// never fabricate confidence).</summary>
    private const int LowVolumePlacementEvents = 30;

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/admin").AddEndpointFilter<AdminScopeFilter>();
        admin.MapGet("/whitelist", ListWhitelistAsync);
        admin.MapPost("/whitelist", AddWhitelistAsync);
        admin.MapDelete("/whitelist/{id:long}", DeleteWhitelistAsync);
        admin.MapGet("/reports/summary", GetSummaryAsync);
        admin.MapGet("/sites/integration-status", GetIntegrationStatusAsync);
        admin.MapGet("/campaigns", ListCampaignsAsync);
        admin.MapGet("/reports/flagged-sources", GetFlaggedSourcesAsync);
        admin.MapGet("/reports/publishers", GetPublisherReportAsync);
        admin.MapGet("/reports/sites", GetSiteReportAsync);
        return app;
    }

    // ------------------------------------------------------ whitelist: list --

    private static async Task<IResult> ListWhitelistAsync(
        string? type, int? offset, int? limit, IWhitelistRepository whitelist, CancellationToken ct)
    {
        if (type is not null && !AllowedSourceTypes.Contains(type, StringComparer.Ordinal))
            return TypeValidationProblem();

        var effectiveOffset = Math.Max(offset ?? 0, 0);
        var effectiveLimit = Math.Clamp(limit ?? 50, 1, 200);

        var entries = await whitelist.ListAsync(type, effectiveOffset, effectiveLimit, ct);
        return Results.Ok(entries.Select(ToResponse));
    }

    // ------------------------------------------------------- whitelist: add --

    private static async Task<IResult> AddWhitelistAsync(
        AddWhitelistRequest req, IWhitelistRepository whitelist, CancellationToken ct)
    {
        if (req.Type is null || !AllowedSourceTypes.Contains(req.Type, StringComparer.Ordinal))
            return TypeValidationProblem();

        if (string.IsNullOrEmpty(req.Value))
            return ValidationProblem("value", "value must not be empty.");

        if (req.Type == "ip" && !IPAddress.TryParse(req.Value, out _))
            return ValidationProblem("value", "value must be a valid IP address when type is 'ip'.");

        if (req.Source is null || !AllowedSources.Contains(req.Source, StringComparer.Ordinal))
            return ValidationProblem("source", "source must be one of: manual, review_screen.");

        if (req.Source == "review_screen")
        {
            if (req.SessionId is null || !SidShape().IsMatch(req.SessionId))
            {
                return ValidationProblem(
                    "sessionId",
                    "sessionId is required and must match ^[A-Za-z0-9_-]{8,64}$ when source is 'review_screen'.");
            }
        }
        else if (req.SessionId is not null && !SidShape().IsMatch(req.SessionId))
        {
            return ValidationProblem("sessionId", "sessionId must match ^[A-Za-z0-9_-]{8,64}$.");
        }

        // ONE call: DAT-07's AddAsync already rebuilds the Redis cache set AND
        // emits the D19 negative label (Legit/ReviewScreen) itself when
        // Source=='review_screen' with a SessionId. Do not touch Redis or
        // ILabelSink here — that would double-write the label and race the
        // repository's own rebuild.
        var id = await whitelist.AddAsync(
            new NewWhitelistEntry(
                req.Type, req.Value, req.Reason, req.Source,
                CreatedBy: null, // no ambient API-key identifier is exposed yet
                ExpiresUtc: null,
                SessionId: req.SessionId),
            ct);

        return Results.Created($"/admin/whitelist/{id}", new AddWhitelistResponse(id));
    }

    // ---------------------------------------------------- whitelist: delete --

    private static async Task<IResult> DeleteWhitelistAsync(
        long id, IWhitelistRepository whitelist, CancellationToken ct)
    {
        var existing = await whitelist.GetByIdAsync(id, ct);
        if (existing is null)
            return NotFoundProblem("Whitelist entry not found.");

        // The repository rebuilds the cache set for the removed entry's
        // SourceType itself — no endpoint-side invalidation.
        await whitelist.RemoveByIdAsync(id, ct);
        return Results.NoContent();
    }

    // ------------------------------------------------------------- summary --

    private static async Task<IResult> GetSummaryAsync(
        string? campaignId, string? from, string? to,
        IVerdictSummaryRepository summaries, CancellationToken ct)
    {
        Guid? campaignGuid = null;
        if (campaignId is not null)
        {
            if (!Guid.TryParse(campaignId, out var parsedCampaignGuid))
                return ValidationProblem("campaignId", "campaignId must be a GUID when supplied.");
            campaignGuid = parsedCampaignGuid;
        }

        if (from is null || !TryParseDate(from, out var fromDate))
            return ValidationProblem("from", "from is required and must be yyyy-MM-dd.");

        if (to is null || !TryParseDate(to, out var toDate))
            return ValidationProblem("to", "to is required and must be yyyy-MM-dd.");

        if (fromDate > toDate)
            return ValidationProblem("from", "from must be <= to.");

        if (toDate.DayNumber - fromDate.DayNumber > MaxSummaryRangeDays)
            return ValidationProblem("to", $"date range must not exceed {MaxSummaryRangeDays} days.");

        var rows = await summaries.GetDailySummariesAsync(campaignGuid, fromDate, toDate, ct);

        var response = new SummaryReportResponse(
            fromDate, toDate, campaignGuid,
            rows.Select(ToSummaryDay).ToList());
        return Results.Ok(response);
    }

    private static SummaryDayResponse ToSummaryDay(VerdictDailySummaryRow r)
        => new SummaryDayResponse(
            r.Date, r.Events, r.Allowed, r.Challenged, r.Blocked,
            r.Events == 0 ? null : Math.Round((double)r.ScoreSum / r.Events, 1))
        {
            ScoreHistogram = ToHistogram(r.ScoreDistribution),
        };

    // ----------------------------------------------------- integration status --

    private static async Task<IResult> GetIntegrationStatusAsync(
        ISiteRepository sites, IConnectionMultiplexer redis,
        ITenantContext tenant, IClock clock, CancellationToken ct)
    {
        var siteList = await sites.ListAsync(ct);
        var db = redis.GetDatabase();
        var tid = tenant.TenantId.Value.ToString("D");
        var nowUnix = clock.UtcNow.ToUnixTimeSeconds();

        var results = new List<SiteIntegrationStatusResponse>(siteList.Count);
        foreach (var site in siteList)
        {
            var raw = await db.StringGetAsync($"t:{tid}:site:{site.SiteKey}:lastbeacon");

            DateTime? lastBeaconAt = null;
            var effectiveLevel = "http-only";
            if (raw.HasValue && long.TryParse(raw.ToString(), out var lastBeaconUnix))
            {
                lastBeaconAt = DateTimeOffset.FromUnixTimeSeconds(lastBeaconUnix).UtcDateTime;
                if (nowUnix - lastBeaconUnix <= LastBeaconJsWindowSeconds)
                    effectiveLevel = "js";
            }

            results.Add(new SiteIntegrationStatusResponse(
                site.SiteKey, site.Domain, site.IntegrationMode, lastBeaconAt, effectiveLevel));
        }

        return Results.Ok(new IntegrationStatusReportResponse(results));
    }

    // ----------------------------------------------------------- campaigns --
    // P2-03: the summary report requires a campaignId and nothing else exposed the
    // tenant's campaign list. Read-only projection of DAT-05's ICampaignRepository;
    // LandingUrl/GeoTargets are deliberately NOT returned (the portal never needs
    // them, and LandingUrl is the /c open-redirect guardrail's own business).
    private static async Task<IResult> ListCampaignsAsync(
        ICampaignRepository campaigns, CancellationToken ct)
    {
        var rows = await campaigns.ListAsync(ct);
        return Results.Ok(new CampaignListResponse(
            rows.Select(c => new CampaignSummaryResponse(
                    c.CampaignId, c.Platform, c.ExternalCampaignId, c.Status))
                .ToList()));
    }

    // ------------------------------------------------------ flagged sources --
    // P2-03: D23 read of dbo.FlaggedSourcesDaily (SQL aggregate table) — the
    // review/override screen's data source. NEVER ClickHouse.
    private static async Task<IResult> GetFlaggedSourcesAsync(
        string? from, string? to, int? limit,
        IVerdictSummaryRepository summaries, CancellationToken ct)
    {
        if (from is null || !TryParseDate(from, out var fromDate))
            return ValidationProblem("from", "from is required and must be yyyy-MM-dd.");

        if (to is null || !TryParseDate(to, out var toDate))
            return ValidationProblem("to", "to is required and must be yyyy-MM-dd.");

        if (fromDate > toDate)
            return ValidationProblem("from", "from must be <= to.");

        if (toDate.DayNumber - fromDate.DayNumber > MaxSummaryRangeDays)
            return ValidationProblem("to", $"date range must not exceed {MaxSummaryRangeDays} days.");

        // DAT-06's repository throws outside 1..1000 — clamp, never forward blindly.
        var effectiveLimit = Math.Clamp(limit ?? 100, 1, 1000);

        var rows = await summaries.GetTopFlaggedSourcesAsync(fromDate, toDate, effectiveLimit, ct);
        return Results.Ok(new FlaggedSourcesReportResponse(
            fromDate, toDate,
            rows.Select(ToFlaggedSource).ToList()));
    }

    private static FlaggedSourceResponse ToFlaggedSource(FlaggedSourceDailyRow r) =>
        new(r.Date, r.SourceType, r.Value, r.FlaggedCount, r.BlockedCount, r.ScoreSum)
        {
            ScoreHistogram = ToHistogram(r.ScoreDistribution),
        };

    // -------------------------------------------------------- publishers --
    // P2-01: D23 reads of dbo.PublisherDailySummaries / dbo.SiteDailySummaries
    // (SQL aggregate tables materialized by RollupService) — NEVER ClickHouse.

    private static async Task<IResult> GetPublisherReportAsync(
        string? from, string? to, int? limit,
        IPublisherSummaryRepository publishers, CancellationToken ct)
    {
        if (from is null || !TryParseDate(from, out var fromDate))
            return ValidationProblem("from", "from is required and must be yyyy-MM-dd.");
        if (to is null || !TryParseDate(to, out var toDate))
            return ValidationProblem("to", "to is required and must be yyyy-MM-dd.");
        if (fromDate > toDate)
            return ValidationProblem("from", "from must be <= to.");
        if (toDate.DayNumber - fromDate.DayNumber > MaxSummaryRangeDays)
            return ValidationProblem("to", $"date range must not exceed {MaxSummaryRangeDays} days.");

        var effectiveLimit = Math.Clamp(limit ?? 50, 1, 200);
        var rows = await publishers.GetTopPlacementsAsync(fromDate, toDate, effectiveLimit, ct);
        return Results.Ok(new PublisherReportResponse(
            fromDate, toDate, rows.Select(ToPublisherRow).ToList()));
    }

    private static PublisherReportRowResponse ToPublisherRow(PlacementRangeTotalsRow r)
    {
        var flagged = r.Challenged + r.Blocked;
        return new PublisherReportRowResponse(
            r.Placement, r.Events, r.Allowed, r.Challenged, r.Blocked, flagged,
            r.Events == 0 ? null : Math.Round((double)flagged / r.Events, 4),
            r.Events == 0 ? null : Math.Round((double)r.ScoreSum / r.Events, 1),
            r.NoJsBeaconCount, r.Events < LowVolumePlacementEvents,
            r.FirstDay, r.LastDay)
        {
            ScoreHistogram = ToHistogram(r.ScoreDistribution),
        };
    }

    private static async Task<IResult> GetSiteReportAsync(
        string? from, string? to,
        IPublisherSummaryRepository publishers, CancellationToken ct)
    {
        if (from is null || !TryParseDate(from, out var fromDate))
            return ValidationProblem("from", "from is required and must be yyyy-MM-dd.");
        if (to is null || !TryParseDate(to, out var toDate))
            return ValidationProblem("to", "to is required and must be yyyy-MM-dd.");
        if (fromDate > toDate)
            return ValidationProblem("from", "from must be <= to.");
        if (toDate.DayNumber - fromDate.DayNumber > MaxSummaryRangeDays)
            return ValidationProblem("to", $"date range must not exceed {MaxSummaryRangeDays} days.");

        var rows = await publishers.GetSiteDailyAsync(fromDate, toDate, ct);
        return Results.Ok(new SiteReportResponse(
            fromDate, toDate, rows.Select(ToSiteReportDay).ToList()));
    }

    private static SiteReportDayResponse ToSiteReportDay(SiteDailySummaryRow r)
        => new SiteReportDayResponse(
            r.Date, r.SiteKey, r.TotalEvents, r.Events, r.Allowed, r.Challenged, r.Blocked,
            r.Events == 0 ? null : Math.Round((double)r.ScoreSum / r.Events, 1),
            r.NoJsBeaconCount)
        {
            ScoreHistogram = ToHistogram(r.ScoreDistribution),
        };

    private static ScoreHistogramResponse ToHistogram(ScoreDistribution distribution) => new(
        ScoreDistribution.BucketWidth,
        ScoreDistribution.Edges,
        distribution.Counts,
        distribution.SumSq);

    // ------------------------------------------------------------- helpers --

    private static bool TryParseDate(string s, out DateOnly date)
        => DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static WhitelistEntryResponse ToResponse(WhitelistEntry e)
        => new(e.Id, e.SourceType, e.Value, e.Reason, e.Source, e.CreatedBy, e.CreatedUtc, e.ExpiresUtc);

    private static IResult TypeValidationProblem()
        => ValidationProblem("type", "type must be one of: ip, device_id, fingerprint.");

    private static IResult ValidationProblem(string field, string message)
        => Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static IResult NotFoundProblem(string detail)
        => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found",
            detail: detail,
            type: "https://httpstatuses.io/404");
}
