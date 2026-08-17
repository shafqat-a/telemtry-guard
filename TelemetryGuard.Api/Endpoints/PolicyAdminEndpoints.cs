using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using TelemetryGuard.Api.Auth;
using TelemetryGuard.Api.Options;
using TelemetryGuard.Api.Services;
using TelemetryGuard.Data.Repositories;

namespace TelemetryGuard.Api.Endpoints;

public static class PolicyAdminEndpoints
{
    private const string ApiKeyHeader = "X-Api-Key";

    public static IEndpointRouteBuilder MapPolicyAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/admin").AddEndpointFilter<AdminScopeFilter>();
        admin.MapGet("/policy", GetAsync);
        admin.MapPut("/policy", PutAsync);
        return app;
    }

    private static async Task<IResult> GetAsync(ITenantPolicyProvider provider, CancellationToken ct)
        => Results.Ok(ToResponse(await provider.GetAsync(ct)));

    private static async Task<IResult> PutAsync(
        PolicyPutRequest request,
        HttpContext http,
        ITenantRepository tenants,
        ITenantPolicyProvider provider,
        IOptions<ScoringBandOptions> deploymentBands,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.AllowMax is < 0 or > 100)
            errors["allowMax"] = ["allowMax must be null or between 0 and 100."];
        if (request.ChallengeMax is < 0 or > 100)
            errors["challengeMax"] = ["challengeMax must be null or between 0 and 100."];

        var allowMax = request.AllowMax ?? deploymentBands.Value.AllowMax;
        var challengeMax = request.ChallengeMax ?? deploymentBands.Value.ChallengeMax;
        if (allowMax >= challengeMax)
            errors["challengeMax"] = ["The effective challengeMax must be greater than effective allowMax."];

        var mode = request.EnforcementMode switch
        {
            "AutoEnforce" => (byte)0,
            "ApprovalQueue" => (byte)1,
            _ => byte.MaxValue,
        };
        if (mode == byte.MaxValue)
            errors["enforcementMode"] = ["enforcementMode must be AutoEnforce or ApprovalQueue."];
        if (errors.Count > 0)
            return Results.ValidationProblem(errors);

        var updated = await tenants.UpdatePolicyAsync(
            new TenantPolicyUpdate((byte?)request.AllowMax, (byte?)request.ChallengeMax,
                request.ObserveOnly, mode, request.ExternalAuthority),
            HashApiKey(http), ct);
        if (updated is null)
            return Results.NotFound();

        provider.Invalidate();
        return Results.Ok(ToResponse(await provider.GetAsync(ct)));
    }

    private static PolicyResponse ToResponse(EffectiveTenantPolicy policy) => new(
        policy.AllowMax,
        policy.ChallengeMax,
        policy.ObserveOnly,
        policy.EnforcementMode == 0 ? "AutoEnforce" : "ApprovalQueue",
        policy.ExternalAuthority,
        policy.UpdatedUtc,
        new PolicySources(policy.AllowMaxSource, policy.ChallengeMaxSource,
            policy.ObserveOnlySource, "tenant", "tenant"));

    private static byte[]? HashApiKey(HttpContext http)
        => http.Request.Headers.TryGetValue(ApiKeyHeader, out var values) && values.Count > 0
            ? SHA256.HashData(Encoding.UTF8.GetBytes(values.ToString()))
            : null;

    private sealed record PolicyPutRequest(
        int? AllowMax,
        int? ChallengeMax,
        bool? ObserveOnly,
        string? EnforcementMode,
        bool ExternalAuthority);

    private sealed record PolicyResponse(
        int AllowMax,
        int ChallengeMax,
        bool ObserveOnly,
        string EnforcementMode,
        bool ExternalAuthority,
        DateTime? UpdatedUtc,
        PolicySources Sources);

    private sealed record PolicySources(
        string AllowMax,
        string ChallengeMax,
        string ObserveOnly,
        string EnforcementMode,
        string ExternalAuthority);
}
