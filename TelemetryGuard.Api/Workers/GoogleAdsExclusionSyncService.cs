using System.Diagnostics.Metrics;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using TelemetryGuard.Core.Time;
using TelemetryGuard.Data;
using TelemetryGuard.Data.Repositories;
using TelemetryGuard.Integrations.GoogleAds;

namespace TelemetryGuard.Api.Workers;

/// <summary>
/// INT-03: every <c>GoogleAds:SyncIntervalMinutes</c> minutes, pushes `approved`
/// Google exclusion-queue rows (<c>dbo.ExclusionQueue</c>, <c>Platform='google'</c>)
/// to Google Ads as negative campaign criteria (D15) — 'ip' rows become IP
/// blocks, 'placement' rows become negative placements — honoring
/// <see cref="GoogleAdsOptions.MaxIpExclusionsPerCampaign"/> via
/// <see cref="SyncPlanner"/>'s LRU eviction. Consumes ONLY `approved` rows
/// (§6.1 step 5 / D21): pending rows await INT-02's approval flow, and this
/// worker never touches the 31-70 challenge band — it reads exclusively from
/// the queue API-06 writes for block-band verdicts.
///
/// PLACEMENT NOTE (house precedent, see GoogleAdsServiceCollectionExtensions):
/// the BackgroundService lives here in Api/Workers (like RollupService,
/// VerdictFinalizerService) rather than in TelemetryGuard.Integrations, because
/// it needs Dapper + ISystemConnectionFactory. Only the Google-API-touching
/// gateway lives in Integrations.
///
/// REALITY CHECK: at MVP, API-06's VerdictFinalizer hardcodes
/// <c>SourceType="ip"</c> — no producer ever writes a 'placement' row. The
/// placement branch below is implemented and unit/integration-tested (seeded
/// directly), but it has zero real input today; it activates automatically the
/// day a producer starts writing 'placement' rows.
///
/// Tenancy (D11): <see cref="ISystemConnectionFactory.OpenSystemAsync"/> is used
/// ONLY to enumerate tenants; every other statement runs on a connection stamped
/// for that one tenant via <see cref="ISystemConnectionFactory.OpenForTenantAsync"/>,
/// with explicit TenantId predicates kept in every query as a backstop to RLS.
/// </summary>
public sealed class GoogleAdsExclusionSyncService(
    ISystemConnectionFactory systemConnections,
    IGoogleAdsGateway gateway,
    IOptions<GoogleAdsOptions> options,
    IClock clock,
    ILogger<GoogleAdsExclusionSyncService> logger) : BackgroundService
{
    private static readonly Meter Meter = new("TelemetryGuard.GoogleAds");
    private static readonly Counter<long> PushedCounter =
        Meter.CreateCounter<long>("tg.googleads.pushed");
    private static readonly Counter<long> FailedCounter =
        Meter.CreateCounter<long>("tg.googleads.failed");
    private static readonly Counter<long> EvictedCounter =
        Meter.CreateCounter<long>("tg.googleads.evicted");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Run once at startup, after a short delay so the host finishes wiring up
            // (mirrors the "also run once at startup" instruction; a full interval
            // wait before the very first push would otherwise leave a freshly
            // deployed AutoEnforce tenant unsynced for up to 15 minutes).
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
                logger.LogError(ex, "GoogleAdsExclusionSyncService: run failed; the next tick retries");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>One full sync pass over every tenant with Google Ads sync configured.
    /// Public for tests/manual triggering. A failed tenant never blocks the rest.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<TenantRow> tenants;
        await using (var sys = await systemConnections.OpenSystemAsync(ct).ConfigureAwait(false))
        {
            tenants = (await sys.QueryAsync<TenantRow>(new CommandDefinition(
                "SELECT TenantId, GoogleAdsCustomerId FROM dbo.Tenants WHERE Status = 0 AND GoogleAdsCustomerId IS NOT NULL",
                cancellationToken: ct)).ConfigureAwait(false)).AsList();
        }

        foreach (var t in tenants)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var conn = await systemConnections.OpenForTenantAsync(t.TenantId, ct).ConfigureAwait(false);
                await SyncTenantAsync(t.TenantId, t.GoogleAdsCustomerId, conn, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "GoogleAdsExclusionSyncService: sync failed for tenant {TenantId}; continuing with remaining tenants",
                    t.TenantId);
            }
        }
    }

    /// <summary>
    /// One tenant's sync cycle. Internal (not private) so GoogleAdsSyncServiceTests
    /// can drive it directly against a real migrated SQL Server connection with a
    /// scripted fake gateway, per INT-03's testing contract.
    ///
    /// Every write happens in ONE transaction, and — critically — NOTHING is
    /// written at all when <see cref="GoogleAdsOptions.EffectiveDryRun"/> is true:
    /// the whole plan is computed (read-only) first, dry-run is checked, and only
    /// past that point does any INSERT/UPDATE/DELETE happen. A misconfigured
    /// fresh deployment must be completely inert.
    /// </summary>
    internal async Task SyncTenantAsync(
        Guid tenantId, string customerId, SqlConnection conn, CancellationToken ct)
    {
        var opts = options.Value;

        // ---- step a: approved google rows, oldest first (SyncPlanner needs this
        // order — "newest wins" cap logic reads the tail of each per-campaign list). ----
        var rows = (await conn.QueryAsync<QueueRow>(new CommandDefinition(
            """
            SELECT TOP (@batch) Id, SourceType, Value, CampaignScope
            FROM dbo.ExclusionQueue
            WHERE TenantId = @tid AND Platform = 'google' AND Status = @approved
            ORDER BY CreatedUtc
            """,
            new { batch = opts.QueueBatchSize, tid = tenantId, approved = ExclusionStatuses.Approved },
            cancellationToken: ct)).ConfigureAwait(false)).AsList();

        if (rows.Count == 0) return; // nothing approved this cycle for this tenant

        // ---- step b: resolve each row's target Google campaign id(s). IP/placement
        // exclusions are per-campaign in Google Ads — there is no account-level
        // mutate, so a NULL CampaignScope (tenant-wide) fans out to every one of the
        // tenant's active google campaigns that has an ExternalCampaignId. ----
        var immediateFailures = new List<(long RowId, string Error)>();
        var rowTargets = new List<(long RowId, string SourceType, string Value, IReadOnlyList<string> CampaignIds)>();
        List<string>? fanoutCampaignIds = null;

        foreach (var row in rows)
        {
            if (row.CampaignScope is { } scope)
            {
                var externalId = await conn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
                    "SELECT ExternalCampaignId FROM dbo.Campaigns WHERE TenantId = @tid AND CampaignId = @scope AND Platform = 'google'",
                    new { tid = tenantId, scope },
                    cancellationToken: ct)).ConfigureAwait(false);

                if (string.IsNullOrEmpty(externalId))
                    immediateFailures.Add((row.Id, "campaign has no ExternalCampaignId"));
                else
                    rowTargets.Add((row.Id, row.SourceType, row.Value, new[] { externalId }));
            }
            else
            {
                fanoutCampaignIds ??= (await conn.QueryAsync<string>(new CommandDefinition(
                    """
                    SELECT ExternalCampaignId FROM dbo.Campaigns
                    WHERE TenantId = @tid AND Platform = 'google' AND Status = 0 AND ExternalCampaignId IS NOT NULL
                    """,
                    new { tid = tenantId },
                    cancellationToken: ct)).ConfigureAwait(false)).AsList();

                if (fanoutCampaignIds.Count == 0)
                    immediateFailures.Add((row.Id, "no active google campaigns with ExternalCampaignId for tenant-wide exclusion"));
                else
                    rowTargets.Add((row.Id, row.SourceType, row.Value, fanoutCampaignIds));
            }
        }

        // ---- step c: live state + candidate adds, deduped and mapped back to the
        // originating queue row id(s) (a fan-out row maps to many; two rows could in
        // theory collide on the exact same campaign+value — both ride the same add). ----
        var live = (await conn.QueryAsync<LiveRow>(new CommandDefinition(
            "SELECT GoogleCampaignId, CriterionResourceName, SourceType, SourceValue, PushedUtc FROM dbo.GoogleAdsPushedExclusions WHERE TenantId = @tid",
            new { tid = tenantId },
            cancellationToken: ct)).ConfigureAwait(false)).AsList();
        var liveLookup = live.ToDictionary(l => (l.GoogleCampaignId, l.SourceType, l.SourceValue));

        var addToRows = new Dictionary<CriterionAdd, List<long>>();
        foreach (var (rowId, sourceType, value, campaignIds) in rowTargets)
        {
            foreach (var campaignId in campaignIds)
            {
                var add = new CriterionAdd(campaignId, sourceType, value);
                if (!addToRows.TryGetValue(add, out var list))
                    addToRows[add] = list = new List<long>();
                list.Add(rowId);
            }
        }

        var allCandidateAdds = addToRows.Keys.ToList();
        var plannerLive = live
            .Select(l => new LivePushedCriterion(l.GoogleCampaignId, l.CriterionResourceName, l.SourceType, l.SourceValue, l.PushedUtc))
            .ToList();
        var plans = SyncPlanner.Plan(allCandidateAdds, plannerLive, opts.MaxIpExclusionsPerCampaign);

        var plannedAdds = plans.SelectMany(p => p.Adds).ToList();
        var plannedRemoves = plans.SelectMany(p => p.EvictResourceNames).Distinct(StringComparer.Ordinal).ToList();
        var capExceeded = plans.SelectMany(p => p.CapExceeded).ToList();

        // ---- step e: dry-run gate. Nothing below this point runs until it's past —
        // no Google call, no SQL write of any kind (not even the immediate-failure or
        // already-live "free" transitions computed above). ----
        if (opts.EffectiveDryRun)
        {
            var campaignCount = plans.Select(p => p.GoogleCampaignId).Distinct().Count();
            logger.LogInformation(
                "[DRY-RUN] tenant {TenantId} customer {CustomerId}: +{Adds} ips/placements, -{Evictions} evictions across {Campaigns} campaigns",
                tenantId, customerId, plannedAdds.Count, plannedRemoves.Count, campaignCount);
            return;
        }

        // ---- step f: send to Google in MutateBatchSize-sized chunks. ----
        var created = new List<(CriterionAdd Add, string ResourceName)>();
        var failed = new List<(CriterionAdd Add, string Error)>();
        var executedRemoves = new List<string>();

        foreach (var (addsChunk, removesChunk) in ChunkOperations(plannedAdds, plannedRemoves, opts.MutateBatchSize))
        {
            try
            {
                var outcome = await gateway.MutateAsync(customerId, addsChunk, removesChunk, ct).ConfigureAwait(false);
                created.AddRange(outcome.Created);
                failed.AddRange(outcome.Failures);
                // MutateOutcome reports no per-remove result (see IGoogleAdsGateway) — a
                // chunk that returns at all (no exception) is taken to have executed its
                // removes; a chunk that throws is taken to have executed none of them.
                executedRemoves.AddRange(removesChunk);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "GoogleAdsExclusionSyncService: mutate failed for tenant {TenantId} customer {CustomerId}",
                    tenantId, customerId);
                foreach (var a in addsChunk) failed.Add((a, ex.Message));
            }
        }

        // ---- step g: persist every outcome in one transaction. ----
        var now = clock.UtcNow.UtcDateTime;
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        foreach (var (rowId, error) in immediateFailures)
            await FailRowAsync(conn, tx, tenantId, rowId, error, now, ct).ConfigureAwait(false);

        // Per-row, per-target-campaign tally: a fan-out row is 'pushed' only when
        // EVERY target campaign succeeded (already-live OR newly created); any single
        // campaign failure fails the whole row, with all its campaign errors concatenated.
        var total = new Dictionary<long, int>();
        var succeeded = new Dictionary<long, int>();
        var errors = new Dictionary<long, List<string>>();

        void Record(IEnumerable<long> rowIds, bool ok, string? error)
        {
            foreach (var rid in rowIds)
            {
                total[rid] = total.GetValueOrDefault(rid) + 1;
                if (ok)
                {
                    succeeded[rid] = succeeded.GetValueOrDefault(rid) + 1;
                }
                else
                {
                    if (!errors.TryGetValue(rid, out var list))
                        errors[rid] = list = new List<string>();
                    list.Add(error!);
                }
            }
        }

        foreach (var add in allCandidateAdds)
        {
            if (liveLookup.ContainsKey((add.GoogleCampaignId, add.SourceType, add.SourceValue)))
                Record(addToRows[add], ok: true, error: null); // already live — no Google call needed
        }
        foreach (var add in capExceeded)
            Record(addToRows[add], ok: false, error: "ip exclusion cap exceeded");
        foreach (var (add, resourceName) in created)
        {
            Record(addToRows[add], ok: true, error: null);
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO dbo.GoogleAdsPushedExclusions
                    (TenantId, CriterionResourceName, GoogleCampaignId, SourceType, SourceValue, ExclusionQueueId, PushedUtc)
                VALUES (@tid, @rn, @cid, @st, @val, @qid, @now)
                """,
                new
                {
                    tid = tenantId, rn = resourceName, cid = add.GoogleCampaignId,
                    st = add.SourceType, val = add.SourceValue,
                    qid = addToRows[add][0], // first originating row — see field doc on ExclusionQueueId
                    now,
                },
                transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
        }
        foreach (var (add, error) in failed)
            Record(addToRows[add], ok: false, error: error);

        var pushedCount = 0;
        var failedCount = immediateFailures.Count;
        foreach (var (rowId, rowTotal) in total)
        {
            if (succeeded.GetValueOrDefault(rowId) == rowTotal)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE dbo.ExclusionQueue
                    SET Status = @pushed, PushedUtc = @now, UpdatedUtc = @now, LastError = NULL
                    WHERE TenantId = @tid AND Id = @id AND Status = @approved
                    """,
                    new { pushed = ExclusionStatuses.Pushed, now, tid = tenantId, id = rowId, approved = ExclusionStatuses.Approved },
                    transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
                pushedCount++;
            }
            else
            {
                var message = errors.TryGetValue(rowId, out var list)
                    ? string.Join("; ", list.Distinct())
                    : "unknown error";
                await FailRowAsync(conn, tx, tenantId, rowId, message, now, ct).ConfigureAwait(false);
                failedCount++;
            }
        }

        foreach (var rn in executedRemoves)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.GoogleAdsPushedExclusions WHERE TenantId = @tid AND CriterionResourceName = @rn",
                new { tid = tenantId, rn },
                transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);

        var tag = new KeyValuePair<string, object?>("tenant_id", tenantId.ToString("D"));
        if (pushedCount > 0) PushedCounter.Add(pushedCount, tag);
        if (failedCount > 0) FailedCounter.Add(failedCount, tag);
        if (executedRemoves.Count > 0) EvictedCounter.Add(executedRemoves.Count, tag);
    }

    private static Task FailRowAsync(
        SqlConnection conn, System.Data.Common.DbTransaction tx, Guid tenantId, long rowId, string error,
        DateTime now, CancellationToken ct)
        => conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.ExclusionQueue
            SET Status = @failed, UpdatedUtc = @now, LastError = LEFT(@err, 2000)
            WHERE TenantId = @tid AND Id = @id AND Status = @approved
            """,
            new
            {
                failed = ExclusionStatuses.Failed, now, err = error,
                tid = tenantId, id = rowId, approved = ExclusionStatuses.Approved,
            },
            transaction: tx, cancellationToken: ct));

    /// <summary>Splits adds/removes into calls of at most <paramref name="batchSize"/>
    /// combined operations each. Removes drain first (they always fit within the
    /// first chunk(s) before any adds start) — pure sequencing, no semantic meaning
    /// to which chunk a given operation lands in.</summary>
    private static IEnumerable<(IReadOnlyList<CriterionAdd> Adds, IReadOnlyList<string> Removes)> ChunkOperations(
        IReadOnlyList<CriterionAdd> adds, IReadOnlyList<string> removes, int batchSize)
    {
        if (adds.Count == 0 && removes.Count == 0) yield break;
        batchSize = Math.Max(1, batchSize);

        var removeIdx = 0;
        var addIdx = 0;
        while (removeIdx < removes.Count || addIdx < adds.Count)
        {
            var remaining = batchSize;
            var removeChunk = new List<string>();
            while (remaining > 0 && removeIdx < removes.Count)
            {
                removeChunk.Add(removes[removeIdx++]);
                remaining--;
            }
            var addChunk = new List<CriterionAdd>();
            while (remaining > 0 && addIdx < adds.Count)
            {
                addChunk.Add(adds[addIdx++]);
                remaining--;
            }
            yield return (addChunk, removeChunk);
        }
    }

    private sealed record TenantRow(Guid TenantId, string GoogleAdsCustomerId);
    private sealed record QueueRow(long Id, string SourceType, string Value, Guid? CampaignScope);
    private sealed record LiveRow(string GoogleCampaignId, string CriterionResourceName, string SourceType, string SourceValue, DateTime PushedUtc);
}
