namespace TelemetryGuard.Api.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Core.Time;

/// <summary>
/// API-06: the grace-period worker. Every second, sweeps every tenant's
/// t:{tid}:grace ZSET (populated by API-02's tracker and API-03's pixel) for
/// sessions whose ~10 s beacon grace period has expired and finalizes them via
/// the shared <see cref="VerdictFinalizer"/> — scoring on HTTP + velocity
/// features alone when no beacon ever arrived (has_js_beacon=0, the non-JS-bot
/// path, by design — spec §6.1).
///
/// SINGLE-INSTANCE MVP: claiming a due session via ZREM (returns true only for
/// the caller that actually removed it) is only safe with exactly one worker
/// process. VerdictFinalizer's own t:{tid}:fin:{sid} SETNX makes
/// double-finalization harmless anyway; multi-instance claiming (an atomic Lua
/// pop) is explicitly deferred — do not build distributed locking here.
/// </summary>
public sealed class VerdictFinalizerService(
    IConnectionMultiplexer redis,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<VerdictFinalizerService> logger) : BackgroundService
{
    /// <summary>Cap on due sessions drained per tenant per tick — bounds one slow/huge
    /// tenant's backlog from starving the sweep of every other tenant in the same tick;
    /// the ZSET keeps whatever is left for the next tick.</summary>
    private const int MaxDuePerTenantPerTick = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "VerdictFinalizerService: grace sweep failed"); // never crash the host
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var db = redis.GetDatabase();
        var now = clock.UtcNow.ToUnixTimeSeconds();

        foreach (var tidVal in await db.SetMembersAsync("grace:tenants").ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var tid = (string)tidVal!;
            var graceKey = $"t:{tid}:grace";
            var due = await db.SortedSetRangeByScoreAsync(
                graceKey, double.NegativeInfinity, now, take: MaxDuePerTenantPerTick).ConfigureAwait(false);

            foreach (var sidVal in due)
            {
                ct.ThrowIfCancellationRequested();
                var sid = (string)sidVal!;

                // Claim: ZREM returning true means THIS call removed it (single-instance
                // MVP — see class doc). A false here means another concurrent claim (or
                // a stale re-read) already took it; skip without double-processing.
                if (!await db.SortedSetRemoveAsync(graceKey, sid).ConfigureAwait(false))
                    continue;

                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var tenantId = new TenantId(Guid.Parse(tid));
                    // Stamp the scoped tenant context (FND-04) so the finalizer's
                    // scoped, tenant-bound repositories resolve against the right tenant.
                    scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(tenantId);
                    await scope.ServiceProvider.GetRequiredService<IVerdictFinalizer>()
                        .FinalizeAsync(tenantId, sid, FinalizeTrigger.GraceExpired, precomputed: null, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One session's failure must not stop the sweep from finalizing the
                    // rest of this tenant's (or the next tenant's) due sessions this tick.
                    logger.LogError(ex,
                        "VerdictFinalizerService: finalize failed for session {SessionId} tenant {TenantId}",
                        sid, tid);
                }
            }

            if (await db.SortedSetLengthAsync(graceKey).ConfigureAwait(false) == 0)
                await db.SetRemoveAsync("grace:tenants", tid).ConfigureAwait(false); // self-heals: next click re-adds
        }
    }
}
