using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using TelemetryGuard.Api.Options;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Data.Models;
using TelemetryGuard.Data.Repositories;

namespace TelemetryGuard.Api.Services;

public sealed record EffectiveTenantPolicy(
    int AllowMax,
    int ChallengeMax,
    bool ObserveOnly,
    byte EnforcementMode,
    bool ExternalAuthority,
    DateTime? UpdatedUtc,
    string AllowMaxSource,
    string ChallengeMaxSource,
    string ObserveOnlySource,
    TenantRecord Tenant);

public interface ITenantPolicyProvider
{
    Task<EffectiveTenantPolicy> GetAsync(CancellationToken ct);
    void Invalidate();
}

public sealed class TenantPolicyProvider(
    ITenantRepository tenants,
    ITenantContext tenant,
    IMemoryCache cache,
    IOptions<ScoringBandOptions> bands,
    IOptions<EnforcementOptions> enforcement) : ITenantPolicyProvider
{
    public async Task<EffectiveTenantPolicy> GetAsync(CancellationToken ct)
    {
        var tid = tenant.TenantId.Value.ToString("D");
        TenantRecord? row;
        try
        {
            row = await cache.GetOrCreateAsync($"tenantcfg:{tid}", entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
                return tenants.GetCurrentAsync(ct);
            });
        }
        catch
        {
            // Preserve the pre-policy request behavior during a transient config-store
            // outage: deployment thresholds remain the safe fallback.
            row = null;
        }
        row ??= new TenantRecord(tenant.TenantId.Value, "", 0, 0, 0, DateTime.UnixEpoch);

        var allowMax = row.AllowMax ?? bands.Value.AllowMax;
        var challengeMax = row.ChallengeMax ?? bands.Value.ChallengeMax;
        if (allowMax is < 0 or > 100 || challengeMax is < 0 or > 100 || allowMax >= challengeMax)
            throw new InvalidOperationException("The effective tenant scoring policy is invalid.");

        return new EffectiveTenantPolicy(
            allowMax,
            challengeMax,
            row.ObserveOnly ?? enforcement.Value.ObserveOnly,
            row.EnforcementMode,
            row.ExternalAuthority,
            row.PolicyUpdatedUtc,
            row.AllowMax.HasValue ? "tenant" : "deployment",
            row.ChallengeMax.HasValue ? "tenant" : "deployment",
            row.ObserveOnly.HasValue ? "tenant" : "deployment",
            row);
    }

    public void Invalidate()
    {
        if (tenant.IsResolved)
            cache.Remove($"tenantcfg:{tenant.TenantId.Value:D}");
    }
}
