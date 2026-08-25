using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TelemetryGuard.Portal.Auth;

namespace TelemetryGuard.Portal.Api;

/// <summary>The single place that speaks HTTP to the admin API. Page handlers
/// never throw on API failure — every call returns an <see cref="AdminApiResult{T}"/>.</summary>
public sealed class PortalApiClient(HttpClient http, IHttpContextAccessor accessor) : IAdminApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<bool> ValidateKeyAsync(string apiKey, CancellationToken ct)
        => (await SendAsync<CampaignListDto>(HttpMethod.Get, "/admin/campaigns", null, apiKey, ct)).IsSuccess;

    public Task<AdminApiResult<CampaignListDto>> ListCampaignsAsync(CancellationToken ct)
        => SendAsync<CampaignListDto>(HttpMethod.Get, "/admin/campaigns", null, SessionKey(), ct);

    public Task<AdminApiResult<SummaryReportDto>> GetSummaryAsync(
        Guid campaignId, DateOnly from, DateOnly to, CancellationToken ct)
        => SendAsync<SummaryReportDto>(HttpMethod.Get,
            $"/admin/reports/summary?campaignId={campaignId:D}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}",
            null, SessionKey(), ct);

    public Task<AdminApiResult<IntegrationStatusReportDto>> GetIntegrationStatusAsync(CancellationToken ct)
        => SendAsync<IntegrationStatusReportDto>(HttpMethod.Get, "/admin/sites/integration-status", null, SessionKey(), ct);

    public Task<AdminApiResult<FlaggedSourcesReportDto>> GetFlaggedSourcesAsync(
        DateOnly from, DateOnly to, int limit, CancellationToken ct)
        => SendAsync<FlaggedSourcesReportDto>(HttpMethod.Get,
            $"/admin/reports/flagged-sources?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&limit={limit}",
            null, SessionKey(), ct);

    public Task<AdminApiResult<IReadOnlyList<WhitelistEntryDto>>> ListWhitelistAsync(
        string? type, int offset, int limit, CancellationToken ct)
    {
        var query = $"?offset={offset}&limit={limit}" + (type is null ? "" : $"&type={Uri.EscapeDataString(type)}");
        return SendAsync<IReadOnlyList<WhitelistEntryDto>>(HttpMethod.Get, $"/admin/whitelist{query}", null, SessionKey(), ct);
    }

    public Task<AdminApiResult<AddWhitelistResponseDto>> AddWhitelistAsync(
        AddWhitelistRequestDto request, CancellationToken ct)
        => SendAsync<AddWhitelistResponseDto>(HttpMethod.Post, "/admin/whitelist", request, SessionKey(), ct);

    public Task<AdminApiResult<NoBody>> DeleteWhitelistAsync(long id, CancellationToken ct)
        => SendAsync<NoBody>(HttpMethod.Delete, $"/admin/whitelist/{id}", null, SessionKey(), ct);

    public Task<AdminApiResult<IReadOnlyList<ExclusionQueueEntryDto>>> ListEnforcementAsync(
        string status, int limit, CancellationToken ct)
        => SendAsync<IReadOnlyList<ExclusionQueueEntryDto>>(HttpMethod.Get,
            $"/admin/enforcement?status={Uri.EscapeDataString(status)}&limit={limit}", null, SessionKey(), ct);

    public Task<AdminApiResult<EnforcementApproveResponseDto>> ApproveEnforcementAsync(
        IReadOnlyList<long> ids, CancellationToken ct)
        => SendAsync<EnforcementApproveResponseDto>(HttpMethod.Post, "/admin/enforcement/approve",
            new EnforcementBatchRequestDto([.. ids], null), SessionKey(), ct);

    public Task<AdminApiResult<EnforcementRejectResponseDto>> RejectEnforcementAsync(
        IReadOnlyList<long> ids, string? note, CancellationToken ct)
        => SendAsync<EnforcementRejectResponseDto>(HttpMethod.Post, "/admin/enforcement/reject",
            new EnforcementBatchRequestDto([.. ids], note), SessionKey(), ct);

    public Task<AdminApiResult<IReadOnlyList<ConversionGoalDto>>> ListConversionGoalsAsync(CancellationToken ct)
        => SendAsync<IReadOnlyList<ConversionGoalDto>>(HttpMethod.Get,"/admin/conversions/goals",null,SessionKey(),ct);
    public Task<AdminApiResult<ConversionGoalDto>> UpsertConversionGoalAsync(ConversionGoalRequestDto request,CancellationToken ct)
        => SendAsync<ConversionGoalDto>(HttpMethod.Post,"/admin/conversions/goals",request,SessionKey(),ct);
    public Task<AdminApiResult<NoBody>> DeleteConversionGoalAsync(Guid goalId,CancellationToken ct)
        => SendAsync<NoBody>(HttpMethod.Delete,$"/admin/conversions/goals/{goalId:D}",null,SessionKey(),ct);
    public Task<AdminApiResult<ConversionGoalsDocumentDto>> ExportConversionGoalsAsync(CancellationToken ct)
        => SendAsync<ConversionGoalsDocumentDto>(HttpMethod.Get,"/admin/conversions/export",null,SessionKey(),ct);
    public Task<AdminApiResult<ConversionImportResponseDto>> ImportConversionGoalsAsync(ConversionGoalsDocumentDto document,CancellationToken ct)
        => SendAsync<ConversionImportResponseDto>(HttpMethod.Post,"/admin/conversions/import",document,SessionKey(),ct);

    /// <summary>The signed-in operator's API key, from the auth cookie's claim.
    /// Every page that calls this is behind AuthorizeFolder("/"), so a missing key
    /// is a wiring bug, not a user state.</summary>
    private string SessionKey()
        => PortalAuth.ApiKeyFor(accessor.HttpContext?.User)
           ?? throw new InvalidOperationException("No portal session API key on this request.");

    private async Task<AdminApiResult<T>> SendAsync<T>(
        HttpMethod method, string path, object? body, string apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);   // never logged, never in a URL
        if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return AdminApiResult<T>.Unreachable(ex.Message);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return AdminApiResult<T>.Rejected();

            if (!response.IsSuccessStatusCode)
                return AdminApiResult<T>.Problem((int)response.StatusCode, await DescribeProblemAsync(response, ct));

            if (typeof(T) == typeof(NoBody) || response.StatusCode == HttpStatusCode.NoContent)
                return AdminApiResult<T>.Ok((T)(object)new NoBody());

            var value = await response.Content.ReadFromJsonAsync<T>(Json, ct);
            return value is null
                ? AdminApiResult<T>.Problem((int)response.StatusCode, "The admin API returned an empty body.")
                : AdminApiResult<T>.Ok(value);
        }
    }

    /// <summary>Renders RFC 7807 problem+json into one line. ValidationProblem bodies
    /// carry `errors: {field: [messages]}` — surface the API's own wording; the portal
    /// never invents validation text.</summary>
    private static async Task<string> DescribeProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var doc = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            var title = doc.TryGetProperty("title", out var t) ? t.GetString() : null;
            var detail = doc.TryGetProperty("detail", out var d) ? d.GetString() : null;
            var fields = new List<string>();
            if (doc.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
                foreach (var field in errors.EnumerateObject())
                    foreach (var message in field.Value.EnumerateArray())
                        fields.Add($"{field.Name}: {message.GetString()}");

            var parts = new[] { title, detail, fields.Count == 0 ? null : string.Join("; ", fields) }
                .Where(p => !string.IsNullOrEmpty(p));
            var text = string.Join(" — ", parts);
            return string.IsNullOrEmpty(text) ? $"Admin API returned {(int)response.StatusCode}." : text;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return $"Admin API returned {(int)response.StatusCode}.";
        }
    }
}
