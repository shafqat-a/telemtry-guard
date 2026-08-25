using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using TelemetryGuard.Portal.Api;

namespace TelemetryGuard.Portal.Pages;

public sealed class ConversionsModel(IAdminApiClient api) : PortalPageModel
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){WriteIndented=true};
    public AdminApiResult<IReadOnlyList<ConversionGoalDto>>? GoalsResult { get; private set; }
    public AdminApiResult<IntegrationStatusReportDto>? SitesResult { get; private set; }
    [TempData] public string? Message { get; set; }
    [TempData] public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        GoalsResult=await api.ListConversionGoalsAsync(ct);
        if(await HandleSessionExpiryAsync(GoalsResult) is { } expired)return expired;
        SitesResult=await api.GetIntegrationStatusAsync(ct);
        if(await HandleSessionExpiryAsync(SitesResult) is { } expiredSites)return expiredSites;
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(string siteKey,string name,string triggerType,
        string pagePaths,string? selector,int? minimumSeconds,bool isPrimary,bool sendMarketIq,
        bool sendMeta,bool sendGoogleAds,bool sendGa4,bool sendTikTok,CancellationToken ct)
    {
        var request=new ConversionGoalRequestDto(null,siteKey,name,triggerType,
            pagePaths.Split([',','\r','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries),
            selector,minimumSeconds,isPrimary,sendMarketIq,sendMeta,sendGoogleAds,sendGa4,sendTikTok,true);
        var result=await api.UpsertConversionGoalAsync(request,ct);
        if(await HandleSessionExpiryAsync(result) is { } expired)return expired;
        Message=result.IsSuccess?"Conversion goal saved.":null; Error=result.Error;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid goalId,CancellationToken ct)
    {
        var result=await api.DeleteConversionGoalAsync(goalId,ct);
        if(await HandleSessionExpiryAsync(result) is { } expired)return expired;
        Message=result.IsSuccess?"Conversion goal deleted.":null; Error=result.Error;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnGetExportAsync(CancellationToken ct)
    {
        var result=await api.ExportConversionGoalsAsync(ct);
        if(await HandleSessionExpiryAsync(result) is { } expired)return expired;
        if(!result.IsSuccess||result.Value is null){ Error=result.Error; return RedirectToPage(); }
        return File(JsonSerializer.SerializeToUtf8Bytes(result.Value,Json),"application/json","telemetryguard-conversion-goals.json");
    }

    public async Task<IActionResult> OnPostImportAsync(IFormFile? jsonFile,CancellationToken ct)
    {
        if(jsonFile is null||jsonFile.Length==0||jsonFile.Length>1024*1024){ Error="Choose a JSON file no larger than 1 MB."; return RedirectToPage(); }
        try
        {
            await using var stream=jsonFile.OpenReadStream();
            var doc=await JsonSerializer.DeserializeAsync<ConversionGoalsDocumentDto>(stream,Json,ct);
            if(doc is null){ Error="The JSON document is empty."; return RedirectToPage(); }
            var result=await api.ImportConversionGoalsAsync(doc,ct);
            if(await HandleSessionExpiryAsync(result) is { } expired)return expired;
            Message=result.IsSuccess?$"Imported {result.Value!.Imported} conversion goals.":null; Error=result.Error;
        }
        catch(JsonException ex){ Error=$"Invalid JSON: {ex.Message}"; }
        return RedirectToPage();
    }
}
