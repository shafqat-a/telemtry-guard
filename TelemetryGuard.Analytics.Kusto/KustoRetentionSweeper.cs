using Microsoft.Extensions.Logging;

namespace TelemetryGuard.Analytics.Kusto;

/// <summary>
/// D20 per-row retention for Kusto. ClickHouse expresses this as a per-row TTL
/// (`timestamp + toIntervalDay(retention_days)`); Kusto has no per-row TTL, so the
/// same policy is enforced by this scheduled soft delete. D20 explicitly allows —
/// requires — each provider to own its retention mechanism (D7).
/// The 180d table retention policy in schema/0001_events.kql is the backstop; this
/// is the per-tenant half. Tenant-agnostic on purpose: the predicate is per ROW,
/// so a single command covers every tenant (no ITenantContext here).
/// NOTE (drift from the task sketch): takes no <c>IOptions&lt;KustoAnalyticsOptions&gt;</c>
/// — the database is already bound into <see cref="IKustoQueryExecutor"/>, and an
/// unread primary-constructor parameter is a hard error under this repo's
/// TreatWarningsAsErrors=true (CS9113). KustoRetentionService (the scheduling
/// BackgroundService, TelemetryGuard.Api/Workers) is the one that reads
/// RetentionSweepEnabled/RetentionSweepIntervalHours.
/// </summary>
public sealed class KustoRetentionSweeper(
    IKustoQueryExecutor executor,
    ILogger<KustoRetentionSweeper> log)
{
    // Verified against the emulator during step 0 (Q5): `.delete table X records <| ...`
    // runs and the swept rows disappear from subsequent queries.
    internal const string SweepCommand =
        """
        .delete table tg_events records <|
        tg_events
        | where timestamp + (retention_days * 1d) < now()
        """;

    /// <summary>Runs the sweep once. Returns true when the command was accepted by
    /// the engine (does not itself throw — callers, e.g. KustoRetentionService,
    /// decide how to react to a false/failed sweep).</summary>
    public async Task<bool> SweepAsync(CancellationToken ct)
    {
        try
        {
            await executor.ExecuteControlCommandAsync(SweepCommand, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "KustoRetentionSweeper: sweep command failed");
            return false;
        }
    }
}
