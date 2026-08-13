using StackExchange.Redis;
using TelemetryGuard.Core.Tenancy;

namespace TelemetryGuard.RiskEngine.Pipeline;

/// <summary>
/// Reads DAT-07's binding whitelist cache contract: key t:{tenantId:D}:wl:{sourceType}
/// (lower-case dashed GUID; sourceType ∈ ip | device_id | fingerprint), a Redis SET of
/// whitelisted values with a 1-hour TTL, populated by DAT-07's repository/mirror — this
/// class only reads. One IBatch issues SISMEMBER (+ EXISTS per consulted key):
/// - t:{tid}:wl:ip {ip}
/// - t:{tid}:wl:fingerprint {visitorId} when a visitorId is present — the FingerprintJS
///   visitorId is checked against the "fingerprint" set (documented decision; the
///   "device_id" set is not consulted at MVP).
/// DAT-07 miss behavior: a consulted key that does not EXIST means "unknown" — treat as
/// NOT whitelisted for this request and fire-and-forget a rebuild via
/// IWhitelistCacheRebuilder, never a SQL call on the hot path (&lt; 50 ms budget).
/// Register scoped — the tenant context is scoped per request (D11).
/// </summary>
public sealed class RedisWhitelistCheck : IWhitelistCheck
{
    private const string SourceTypeIp = "ip";
    private const string SourceTypeFingerprint = "fingerprint";

    private readonly IConnectionMultiplexer _mux;
    private readonly ITenantContext _tenant;
    private readonly IWhitelistCacheRebuilder _rebuilder;

    public RedisWhitelistCheck(IConnectionMultiplexer mux, ITenantContext tenant, IWhitelistCacheRebuilder rebuilder)
    {
        _mux = mux;
        _tenant = tenant;
        _rebuilder = rebuilder;
    }

    public async Task<bool> IsWhitelistedAsync(string ip, string? visitorId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var checkIp = !string.IsNullOrEmpty(ip);
        var checkFingerprint = !string.IsNullOrEmpty(visitorId);
        if (!checkIp && !checkFingerprint)
        {
            return false; // nothing to look up (beacon-less, ip-less edge)
        }

        var batch = _mux.GetDatabase().CreateBatch();
        Task<bool>? ipMember = null, ipExists = null, fpMember = null, fpExists = null;

        if (checkIp)
        {
            var key = Key(SourceTypeIp);
            ipMember = batch.SetContainsAsync(key, ip);
            ipExists = batch.KeyExistsAsync(key);
        }

        if (checkFingerprint)
        {
            var key = Key(SourceTypeFingerprint);
            fpMember = batch.SetContainsAsync(key, visitorId);
            fpExists = batch.KeyExistsAsync(key);
        }

        batch.Execute();
        await Task.WhenAll(
            new[] { ipMember, ipExists, fpMember, fpExists }.Where(t => t is not null).Cast<Task>()
        ).ConfigureAwait(false);

        // Missing key = "unknown": not whitelisted now; rebuild scheduled OFF the awaited path.
        if (ipExists is { Result: false })
        {
            _rebuilder.ScheduleRebuild(SourceTypeIp);
        }
        if (fpExists is { Result: false })
        {
            _rebuilder.ScheduleRebuild(SourceTypeFingerprint);
        }

        return ipMember is { Result: true } || fpMember is { Result: true };
    }

    /// <summary>t:{tenantId:D}:wl:{sourceType} — TenantId.ToString() is the canonical
    /// lowercase "D" GUID form (FND-04); tenant prefix mandatory (D11).</summary>
    private RedisKey Key(string sourceType) => $"t:{_tenant.TenantId}:wl:{sourceType}";
}
