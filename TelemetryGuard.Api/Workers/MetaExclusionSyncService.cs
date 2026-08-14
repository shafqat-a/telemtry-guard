using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using TelemetryGuard.Core.Time;
using TelemetryGuard.Data;
using TelemetryGuard.Data.Repositories;
using TelemetryGuard.Integrations.Meta;

namespace TelemetryGuard.Api.Workers;

/// <summary>
/// INT-04: every <c>Meta:SyncIntervalMinutes</c> minutes, syncs <c>dbo.ExclusionQueue</c>
/// rows with <c>Platform='meta'</c> (D15).
///
/// HONESTY: Meta's Marketing API has NO IP-exclusion capability. The 'ip' sweep
/// below ALWAYS runs — even in dry-run, even with no token configured — because it
/// records a platform FACT ("this cannot be enforced here"), not an enforcement
/// action; it is never gated the way the placement push is. Only 'placement' rows
/// are ever pushed, as entries in a tenant's publisher block list (business-level;
/// attaching the list to an ad set's <c>excluded_publisher_list_ids</c> stays a
/// manual Ads Manager step — D15 "keep this small").
///
/// REALITY CHECK: API-06's VerdictFinalizer hardcodes <c>SourceType="ip"</c> — no
/// producer today writes a 'placement' row, so the block-list push below has zero
/// real input at MVP; only the ip sweep is live traffic (see INT-03's identical
/// note on its own placement branch).
///
/// PLACEMENT NOTE (house precedent — mirrors GoogleAdsExclusionSyncService/INT-03,
/// "do not diverge" from the sibling's seam): lives in Api/Workers, not
/// TelemetryGuard.Integrations, because it needs Dapper + ISystemConnectionFactory;
/// only the Meta-API-touching client lives in Integrations.
///
/// Tenancy (D11): <see cref="ISystemConnectionFactory.OpenSystemAsync"/> enumerates
/// tenants only; every other statement runs on a connection stamped for that one
/// tenant via <see cref="ISystemConnectionFactory.OpenForTenantAsync"/>, with
/// explicit TenantId predicates as a backstop to RLS.
/// </summary>
public sealed class MetaExclusionSyncService(
    ISystemConnectionFactory systemConnections,
    IMetaMarketingClient client,
    IOptions<MetaOptions> options,
    IClock clock,
    ILogger<MetaExclusionSyncService> logger) : BackgroundService
{
    private const string NoIpExclusionMessage =
        "Meta Marketing API provides no IP exclusion capability; entry cannot be enforced on Meta.";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // shut down before the first tick — nothing to do
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.SyncIntervalMinutes));
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw; // shutdown — propagate so the host loop stops cleanly
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MetaExclusionSyncService: run failed; the next tick retries");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>One full sync pass over every tenant with Meta sync configured
    /// (<c>MetaBusinessId IS NOT NULL</c>). Public for tests/manual triggering. A
    /// failed tenant never blocks the rest.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<TenantRow> tenants;
        await using (var sys = await systemConnections.OpenSystemAsync(ct).ConfigureAwait(false))
        {
            tenants = (await sys.QueryAsync<TenantRow>(new CommandDefinition(
                "SELECT TenantId, MetaBusinessId FROM dbo.Tenants WHERE Status = 0 AND MetaBusinessId IS NOT NULL",
                cancellationToken: ct)).ConfigureAwait(false)).AsList();
        }

        foreach (var t in tenants)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var conn = await systemConnections.OpenForTenantAsync(t.TenantId, ct).ConfigureAwait(false);
                await SyncTenantAsync(t.TenantId, t.MetaBusinessId, conn, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "MetaExclusionSyncService: sync failed for tenant {TenantId}; continuing with remaining tenants",
                    t.TenantId);
            }
        }
    }

    /// <summary>One tenant's sync cycle. Internal (not private) so
    /// MetaExclusionSyncServiceTests can drive it directly against a real migrated
    /// SQL Server connection with a fake client (mirrors GoogleAdsExclusionSyncService.SyncTenantAsync).</summary>
    internal async Task SyncTenantAsync(Guid tenantId, string businessId, SqlConnection conn, CancellationToken ct)
    {
        var now = clock.UtcNow.UtcDateTime;

        // ---- ip sweep: a LOCAL TRUTH, not a Meta call — unconditional, even in
        // dry-run and with no token configured (D15 guardrail: never imply an IP
        // was blocked on Meta). ----
        var sweptCount = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.ExclusionQueue
            SET Status = @unsupported, UpdatedUtc = @now, LastError = @msg
            WHERE TenantId = @tid AND Platform = 'meta' AND SourceType = 'ip'
              AND Status IN (@pending, @approved)
            """,
            new
            {
                unsupported = ExclusionStatuses.Unsupported, now, msg = NoIpExclusionMessage,
                tid = tenantId, pending = ExclusionStatuses.Pending, approved = ExclusionStatuses.Approved,
            },
            cancellationToken: ct)).ConfigureAwait(false);

        if (sweptCount > 0)
        {
            logger.LogInformation(
                "MetaExclusionSyncService: marked {Count} ip row(s) Unsupported for tenant {TenantId} — Meta has no IP-exclusion API",
                sweptCount, tenantId);
        }

        // ---- approved placements, oldest first (D21: Pending stays Pending — only
        // INT-02's approval flow may move it). ----
        var rows = (await conn.QueryAsync<QueueRow>(new CommandDefinition(
            """
            SELECT Id, Value
            FROM dbo.ExclusionQueue
            WHERE TenantId = @tid AND Platform = 'meta' AND SourceType = 'placement' AND Status = @approved
            ORDER BY CreatedUtc
            """,
            new { tid = tenantId, approved = ExclusionStatuses.Approved },
            cancellationToken: ct)).ConfigureAwait(false)).AsList();

        if (rows.Count == 0) return; // nothing to push this cycle

        var opts = options.Value;
        var publisherUrls = rows.Select(r => r.Value).ToList();

        // ---- dry-run gate: nothing below this point runs until it's past — no
        // Meta call, no SQL write. A misconfigured fresh deployment stays inert. ----
        if (opts.EffectiveDryRun)
        {
            logger.LogInformation(
                "[DRY-RUN] tenant {TenantId} business {BusinessId}: would push {Count} publisher URL(s) to the Meta block list",
                tenantId, businessId, rows.Count);
            return; // rows stay Approved; zero client calls
        }

        var ids = rows.Select(r => r.Id).ToArray();
        try
        {
            var blockListId = await conn.QuerySingleAsync<string?>(new CommandDefinition(
                "SELECT MetaBlockListId FROM dbo.Tenants WHERE TenantId = @tid",
                new { tid = tenantId }, cancellationToken: ct)).ConfigureAwait(false);

            if (string.IsNullOrEmpty(blockListId))
            {
                var listName = $"{opts.BlockListNamePrefix} — {tenantId:D}";
                var existing = await client.FindBlockListAsync(businessId, listName, ct).ConfigureAwait(false);
                if (existing is not null)
                {
                    blockListId = existing.Id;
                    await client.AddPublisherUrlsAsync(blockListId, publisherUrls, ct).ConfigureAwait(false);
                }
                else
                {
                    blockListId = await client.CreateBlockListAsync(businessId, listName, publisherUrls, ct).ConfigureAwait(false);
                }

                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE dbo.Tenants SET MetaBlockListId = @id WHERE TenantId = @tid",
                    new { id = blockListId, tid = tenantId }, cancellationToken: ct)).ConfigureAwait(false);
            }
            else
            {
                await client.AddPublisherUrlsAsync(blockListId, publisherUrls, ct).ConfigureAwait(false);
            }

            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE dbo.ExclusionQueue
                SET Status = @pushed, PushedUtc = @now, UpdatedUtc = @now, LastError = NULL
                WHERE TenantId = @tid AND Platform = 'meta' AND SourceType = 'placement'
                  AND Status = @approved AND Id IN @ids
                """,
                new { pushed = ExclusionStatuses.Pushed, now, tid = tenantId, approved = ExclusionStatuses.Approved, ids },
                cancellationToken: ct)).ConfigureAwait(false);

            logger.LogInformation(
                "MetaExclusionSyncService: pushed {Count} publisher URL(s) to block list {BlockListId} for tenant {TenantId}",
                rows.Count, blockListId, tenantId);
        }
        catch (MetaApiException ex)
        {
            // List-level granularity is acceptable — Add/CreateBlockList append the
            // whole batch as one call; per-URL result granularity does not exist.
            var message = $"{ex.Message} fbtrace_id={ex.FbTraceId}";
            var truncated = message.Length > 2000 ? message[..2000] : message;

            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE dbo.ExclusionQueue
                SET Status = @failed, UpdatedUtc = @now, LastError = @err
                WHERE TenantId = @tid AND Platform = 'meta' AND SourceType = 'placement'
                  AND Status = @approved AND Id IN @ids
                """,
                new { failed = ExclusionStatuses.Failed, now, err = truncated, tid = tenantId, approved = ExclusionStatuses.Approved, ids },
                cancellationToken: ct)).ConfigureAwait(false);

            logger.LogError(ex, "MetaExclusionSyncService: push failed for tenant {TenantId}", tenantId);
        }
    }

    private sealed record TenantRow(Guid TenantId, string MetaBusinessId);
    private sealed record QueueRow(long Id, string Value);
}
