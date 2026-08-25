using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
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
/// Most D23 reports read the small RLS-protected SQL aggregate tables. The explicit
/// domain-traffic report is a protected, redacted session read through the narrow
/// <see cref="IAnalyticsQueries"/> abstraction; it never returns cookies or header values.
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
        admin.MapGet("/reports/domain-traffic", GetDomainTrafficAsync);
        admin.MapGet("/conversions/goals", ListConversionGoalsAsync);
        admin.MapPost("/conversions/goals", UpsertConversionGoalAsync);
        admin.MapDelete("/conversions/goals/{goalId:guid}", DeleteConversionGoalAsync);
        admin.MapGet("/conversions/export", ExportConversionGoalsAsync);
        admin.MapPost("/conversions/import", ImportConversionGoalsAsync);
        admin.MapPost("/conversions/events", RecordServerConversionAsync);
        return app;
    }

    private static readonly string[] ConversionTriggerTypes =
        ["time_on_page","link_click","button_click","form_submitted","server"];

    private static async Task<IResult> ListConversionGoalsAsync(
        string? siteKey,IConversionRepository conversions,CancellationToken ct)
        => Results.Ok((await conversions.ListGoalsAsync(siteKey,ct)).Select(ToConversionGoal));

    private static async Task<IResult> UpsertConversionGoalAsync(
        ConversionGoalRequest request,IConversionRepository conversions,ISiteRepository sites,
        ITenantContext tenant,CancellationToken ct)
    {
        var error=ValidateConversionGoal(request);
        if(error is not null) return error;
        if(await sites.GetBySiteKeyAsync(request.SiteKey!,ct) is null)
            return ValidationProblem("siteKey","Unknown site key.");
        var id=request.GoalId is { } supplied&&supplied!=Guid.Empty?supplied:Guid.NewGuid();
        var existing=await conversions.GetGoalAsync(id,ct);
        var now=DateTime.UtcNow;
        await conversions.UpsertGoalAsync(new ConversionGoalRecord(
            tenant.TenantId.Value,id,request.SiteKey!,request.Name!.Trim(),request.TriggerType!,
            JsonSerializer.Serialize(NormalizePaths(request.PagePaths!)),NullIfWhiteSpace(request.Selector),
            request.MinimumSeconds,request.IsPrimary,request.SendMarketIq,request.SendMeta,
            request.SendGoogleAds,request.SendGa4,request.SendTikTok,request.IsActive,
            existing?.CreatedUtc??now,now),ct);
        return Results.Ok(ToConversionGoal((await conversions.GetGoalAsync(id,ct))!));
    }

    private static async Task<IResult> DeleteConversionGoalAsync(
        Guid goalId,IConversionRepository conversions,CancellationToken ct)
        => await conversions.DeleteGoalAsync(goalId,ct)?Results.NoContent():
            ValidationProblem("goalId","Goal was not found or already has conversion events; deactivate it instead.");

    private static async Task<IResult> ExportConversionGoalsAsync(
        IConversionRepository conversions,CancellationToken ct)
    {
        var goals=(await conversions.ListGoalsAsync(null,ct)).Select(r=>ToConversionRequest(r)).ToArray();
        return Results.Json(new ConversionGoalsDocument(1,goals));
    }

    private static async Task<IResult> ImportConversionGoalsAsync(
        ConversionGoalsDocument document,IConversionRepository conversions,ISiteRepository sites,
        ITenantContext tenant,CancellationToken ct)
    {
        if(document.Version!=1) return ValidationProblem("version","Only conversion document version 1 is supported.");
        if(document.Goals is null) return ValidationProblem("goals","Goals array is required.");
        if(document.Goals.Count>500) return ValidationProblem("goals","At most 500 goals may be imported at once.");
        var known=(await sites.ListAsync(ct)).Select(s=>s.SiteKey).ToHashSet(StringComparer.Ordinal);
        foreach(var goal in document.Goals)
        {
            if(ValidateConversionGoal(goal) is { } invalid) return invalid;
            if(!known.Contains(goal.SiteKey!)) return ValidationProblem("siteKey",$"Unknown site key: {goal.SiteKey}");
        }
        foreach(var request in document.Goals)
        {
            var id=request.GoalId is { } supplied&&supplied!=Guid.Empty?supplied:Guid.NewGuid();
            var existing=await conversions.GetGoalAsync(id,ct);
            var now=DateTime.UtcNow;
            await conversions.UpsertGoalAsync(new ConversionGoalRecord(
                tenant.TenantId.Value,id,request.SiteKey!,request.Name!.Trim(),request.TriggerType!,
                JsonSerializer.Serialize(NormalizePaths(request.PagePaths!)),NullIfWhiteSpace(request.Selector),
                request.MinimumSeconds,request.IsPrimary,request.SendMarketIq,request.SendMeta,
                request.SendGoogleAds,request.SendGa4,request.SendTikTok,request.IsActive,
                existing?.CreatedUtc??now,now),ct);
        }
        return Results.Ok(new ConversionImportResponse(document.Goals.Count));
    }

    private static IResult? ValidateConversionGoal(ConversionGoalRequest r)
    {
        if(string.IsNullOrWhiteSpace(r.SiteKey)||r.SiteKey.Length>64) return ValidationProblem("siteKey","Site key is required.");
        if(string.IsNullOrWhiteSpace(r.Name)||r.Name.Length>160) return ValidationProblem("name","Name is required and must be at most 160 characters.");
        if(r.TriggerType is null||!ConversionTriggerTypes.Contains(r.TriggerType,StringComparer.Ordinal))
            return ValidationProblem("triggerType","Unsupported trigger type.");
        if(r.PagePaths is null||r.PagePaths.Length==0||r.PagePaths.Length>50)
            return ValidationProblem("pagePaths","Provide between 1 and 50 page paths.");
        if(r.PagePaths.Any(p=>string.IsNullOrWhiteSpace(p)||p.Length>512||!p.Trim().StartsWith('/')))
            return ValidationProblem("pagePaths","Every page path must begin with '/' and be at most 512 characters.");
        if(r.TriggerType=="time_on_page"&&r.MinimumSeconds is not (>=1 and <=86400))
            return ValidationProblem("minimumSeconds","Time goals require 1–86400 seconds.");
        if(r.TriggerType is "link_click" or "button_click" or "form_submitted"&&string.IsNullOrWhiteSpace(r.Selector))
            return ValidationProblem("selector","This trigger type requires a CSS selector.");
        if(r.Selector?.Length>512) return ValidationProblem("selector","Selector must be at most 512 characters.");
        return null;
    }
    private static string[] NormalizePaths(IEnumerable<string> paths)=>paths.Select(p=>p.Trim().Length>1?p.Trim().TrimEnd('/'):"/").Distinct(StringComparer.Ordinal).ToArray();
    private static string? NullIfWhiteSpace(string? value)=>string.IsNullOrWhiteSpace(value)?null:value.Trim();
    private static ConversionGoalResponse ToConversionGoal(ConversionGoalRecord r)=>new(
        r.GoalId,r.SiteKey,r.Name,r.TriggerType,JsonSerializer.Deserialize<string[]>(r.PagePathsJson)??[],r.Selector,r.MinimumSeconds,
        r.IsPrimary,r.SendMarketIq,r.SendMeta,r.SendGoogleAds,r.SendGa4,r.SendTikTok,r.IsActive,r.CreatedUtc,r.UpdatedUtc);
    private static ConversionGoalRequest ToConversionRequest(ConversionGoalRecord r)=>new(
        r.GoalId,r.SiteKey,r.Name,r.TriggerType,JsonSerializer.Deserialize<string[]>(r.PagePathsJson)??[],r.Selector,r.MinimumSeconds,
        r.IsPrimary,r.SendMarketIq,r.SendMeta,r.SendGoogleAds,r.SendGa4,r.SendTikTok,r.IsActive);

    private static async Task<IResult> RecordServerConversionAsync(
        RecordServerConversionRequest request,IConversionRepository conversions,ISiteRepository sites,
        IMarketIqOutboxRepository outbox,IConnectionMultiplexer redis,ITenantContext tenant,CancellationToken ct)
    {
        var goal=await conversions.GetGoalAsync(request.GoalId,ct);
        if(goal is null||!goal.IsActive) return ValidationProblem("goalId","Active conversion goal not found.");
        if(goal.TriggerType!="server") return ValidationProblem("goalId","Only a server-trigger goal accepts trusted server conversions.");
        if(request.PageToConversionMs is <0 or >86400000) return ValidationProblem("pageToConversionMs","Must be between 0 and 86400000.");
        if(request.Currency is { Length:>0 } currency&&(currency.Length!=3||!currency.All(char.IsAsciiLetter)))
            return ValidationProblem("currency","Currency must be a three-letter ISO code.");
        var eventId=request.EventId is { } supplied&&supplied!=Guid.Empty?supplied:Guid.NewGuid();
        var occurred=(request.OccurredAt??DateTime.UtcNow).ToUniversalTime();
        var created=await conversions.InsertEventAsync(new ConversionEventRecord(
            tenant.TenantId.Value,eventId,goal.GoalId,goal.SiteKey,NullIfWhiteSpace(request.SessionId),
            NullIfWhiteSpace(request.VisitId),NullIfWhiteSpace(request.PageUrl),occurred,request.PageToConversionMs,
            true,"server",request.Value,request.Currency?.ToUpperInvariant()),ct);
        if(!created||!goal.SendMarketIq) return Results.Ok(new RecordServerConversionResponse(eventId,created));
        var site=await sites.GetBySiteKeyAsync(goal.SiteKey,ct);
        if(site is not { MarketIqEnabled:true,MarketIqCompanyId:>0 }||string.IsNullOrWhiteSpace(site.MarketIqCollectUrl))
            return Results.Ok(new RecordServerConversionResponse(eventId,true));
        var miqEventId=$"conversion-{eventId:D}";
        var payload=JsonSerializer.Serialize(new Dictionary<string,object?>
        {
            ["companyId"]=site.MarketIqCompanyId,["event_id"]=miqEventId,["session_id"]=NullIfWhiteSpace(request.SessionId),
            ["occurred_at"]=occurred.ToString("O"),["ip"]=NullIfWhiteSpace(request.Ip),
            ["page_to_conversion_ms"]=request.PageToConversionMs,["verified_conversion"]=true,
            ["conversion_goal_id"]=goal.GoalId,["conversion_goal_name"]=goal.Name,
            ["conversion_value"]=request.Value,["conversion_currency"]=request.Currency?.ToUpperInvariant(),
            ["tg_export_mode"]="live_conversion"
        }.Where(x=>x.Value is not null).ToDictionary());
        var deliveryId=new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{tenant.TenantId.Value:D}|{goal.SiteKey}|{miqEventId}")).AsSpan(0,16));
        if(await outbox.EnqueueAsync(new(deliveryId,goal.SiteKey,miqEventId,site.MarketIqCollectUrl,payload,
            site.MarketIqRelayKeyRef,DateTime.UtcNow),ct))
            await redis.GetDatabase().StreamAddAsync("tg:marketiq:deliveries",
                [new NameValueEntry("tenant_id",tenant.TenantId.Value.ToString("D")),new NameValueEntry("delivery_id",deliveryId.ToString("D"))],
                maxLength:100000,useApproximateMaxLength:true);
        return Results.Ok(new RecordServerConversionResponse(eventId,true));
    }

    private static async Task<IResult> GetDomainTrafficAsync(
        string? host, int? page, int? pageSize, bool? botsOnly,
        IAnalyticsQueries analytics, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host)
            || host.Length > 253
            || !Uri.CheckHostName(host).Equals(UriHostNameType.Dns))
            return ValidationProblem("host", "host is required and must be a valid DNS hostname.");

        var effectivePage = Math.Max(page ?? 1, 1);
        var effectivePageSize = Math.Clamp(pageSize ?? 50, 1, 200);
        var report = await analytics.GetDomainTrafficPageAsync(
            host.Trim().ToLowerInvariant(), effectivePage, effectivePageSize, botsOnly ?? false, ct);
        return Results.Ok(report);
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
