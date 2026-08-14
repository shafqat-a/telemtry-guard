using Dapper;
using Microsoft.Data.SqlClient;

namespace TelemetryGuard.Training;

/// <summary>Mirrors dbo.ModelRegistry.Status / TelemetryGuard.Data.Repositories.ModelStatuses
/// — duplicated here, not referenced, because TelemetryGuard.Training must not take a
/// dependency on TelemetryGuard.Data (RSK-08's minimal dependency-surface rule; see this
/// file's class doc). The five string values are the shared wire contract migration
/// 0009 defines; RegistryRoundTripTests (Tests.Integration) prove the two independent
/// copies never drift apart.</summary>
public static class ModelStatuses
{
    public const string Candidate = "candidate";
    public const string Shadow = "shadow";
    public const string Active = "active";
    public const string Rejected = "rejected";
    public const string Retired = "retired";
}

/// <summary>One dbo.ModelRegistry row, as read back by the CLI (promote/rollback/models
/// commands and the promotion gates).</summary>
public sealed record ModelRegistryRow(
    Guid ModelId, string? ScorerVersion, int FeatureSetVersion, string Status,
    DateTime TrainedUtc, DateTime WindowFromUtc, DateTime WindowToUtc,
    int Positives, int Negatives, double Auc, double Auprc, double F1, double? ScoreP99Ms,
    string? ArtifactPath, string? RejectReason, string? Notes,
    DateTime? ShadowSinceUtc, DateTime? ActiveSinceUtc);

/// <summary>Everything `retrain` knows about one training run, ready to INSERT.</summary>
public sealed record NewModelRun(
    string? ScorerVersion, int FeatureSetVersion, string Status,
    DateTime TrainedUtc, DateOnly WindowFrom, DateOnly WindowTo,
    int TrainRows, int ValidationRows, int Positives, int Negatives, int DroppedConflicts,
    double Auc, double Auprc, double F1, double MinAucGate, double? ScoreP99Ms,
    string? ArtifactPath, byte[]? ArtifactSha256, string? MetadataJson, string? GateJson,
    string? RejectReason);

/// <summary>
/// P2-02 write side of dbo.ModelRegistry. Hand-rolled Dapper on a SYSTEM-sentinel-
/// stamped SqlConnection, exactly like <c>LabelBuilder.ListActiveTenantIdsAsync</c>
/// (same <c>EXEC sp_set_session_context</c> preamble, same reason: this console must
/// not take a dependency on TelemetryGuard.Data). dbo.ModelRegistry itself carries no
/// TenantId and no RLS predicate (migration 0009) — the session-context stamp is
/// therefore a no-op for this table specifically, kept only for consistency with every
/// other SqlConnection this project opens.
///
/// READ side (serving path) lives in TelemetryGuard.Data.Repositories.
/// ModelRegistryRepository — that class is what TelemetryGuard.Api actually loads at
/// startup. The two share the column contract of migration 0009;
/// RegistryRoundTripTests (Tests.Integration) prove they agree.
///
/// PromoteAsync/RollbackAsync each run as ONE transaction (D18: promotion/rollback are
/// atomic state transitions, never a window where two rows hold the same status or
/// zero rows do during a switch).
/// </summary>
public sealed class ModelRegistryClient(string sqlConnectionString)
{
    /// <summary>SYSTEM sentinel tenant id (DAT-03) — mirrors WellKnownTenants.System.</summary>
    private static readonly Guid SystemSentinel = new("00000000-0000-0000-0000-000000000001");

    private const string SelectColumns =
        """
        ModelId, ScorerVersion, FeatureSetVersion, Status, TrainedUtc, WindowFromUtc, WindowToUtc,
        Positives, Negatives, Auc, Auprc, F1, ScoreP99Ms, ArtifactPath, RejectReason, Notes,
        ShadowSinceUtc, ActiveSinceUtc
        """;

    /// <summary>The row this project should compare a new candidate against: the
    /// 'active' row if one exists, otherwise the 'shadow' row, otherwise null.</summary>
    public async Task<ModelRegistryRow?> GetIncumbentAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<ModelRegistryRow>(new CommandDefinition(
            $"""
            SELECT TOP (1) {SelectColumns}
            FROM dbo.ModelRegistry
            WHERE Status IN (@Active, @Shadow)
            ORDER BY CASE Status WHEN @Active THEN 0 ELSE 1 END;
            """,
            new { Active = ModelStatuses.Active, Shadow = ModelStatuses.Shadow },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<ModelRegistryRow?> GetByScorerVersionAsync(string scorerVersion, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<ModelRegistryRow>(new CommandDefinition(
            $"""
            SELECT {SelectColumns}
            FROM dbo.ModelRegistry
            WHERE ScorerVersion = @ScorerVersion;
            """,
            new { ScorerVersion = scorerVersion },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <summary>Used by the `retrain` cadence guard (Training:Retrain:MinIntervalHours).</summary>
    public async Task<ModelRegistryRow?> GetNewestRunAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<ModelRegistryRow>(new CommandDefinition(
            $"""
            SELECT TOP (1) {SelectColumns}
            FROM dbo.ModelRegistry
            ORDER BY TrainedUtc DESC;
            """,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ModelRegistryRow>> ListRecentAsync(int limit, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<ModelRegistryRow>(new CommandDefinition(
            $"""
            SELECT TOP (@Limit) {SelectColumns}
            FROM dbo.ModelRegistry
            ORDER BY TrainedUtc DESC;
            """,
            new { Limit = limit },
            cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<Guid> InsertRunAsync(NewModelRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var modelId = Guid.NewGuid();
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO dbo.ModelRegistry
                (ModelId, ScorerVersion, FeatureSetVersion, Status, TrainedUtc, WindowFromUtc, WindowToUtc,
                 TrainRows, ValidationRows, Positives, Negatives, DroppedConflicts,
                 Auc, Auprc, F1, MinAucGate, ScoreP99Ms,
                 ArtifactPath, ArtifactSha256, MetadataJson, GateJson, RejectReason)
            VALUES
                (@ModelId, @ScorerVersion, @FeatureSetVersion, @Status, @TrainedUtc, @WindowFromUtc, @WindowToUtc,
                 @TrainRows, @ValidationRows, @Positives, @Negatives, @DroppedConflicts,
                 @Auc, @Auprc, @F1, @MinAucGate, @ScoreP99Ms,
                 @ArtifactPath, @ArtifactSha256, @MetadataJson, @GateJson, @RejectReason);
            """,
            new
            {
                ModelId = modelId,
                run.ScorerVersion,
                run.FeatureSetVersion,
                run.Status,
                run.TrainedUtc,
                WindowFromUtc = run.WindowFrom.ToDateTime(TimeOnly.MinValue),
                WindowToUtc = run.WindowTo.ToDateTime(TimeOnly.MinValue),
                run.TrainRows,
                run.ValidationRows,
                run.Positives,
                run.Negatives,
                run.DroppedConflicts,
                run.Auc,
                run.Auprc,
                run.F1,
                run.MinAucGate,
                run.ScoreP99Ms,
                run.ArtifactPath,
                run.ArtifactSha256,
                run.MetadataJson,
                run.GateJson,
                run.RejectReason,
            },
            cancellationToken: ct)).ConfigureAwait(false);
        return modelId;
    }

    /// <summary>ONE transaction: retire whatever currently holds <paramref name="toStatus"/>
    /// (keeps UX_ModelRegistry_Single* valid), then promote the target row. 'rejected'
    /// rows can never match (excluded from the WHERE) and a zero-row result throws —
    /// the CLI maps that to exit code 4.</summary>
    public async Task PromoteAsync(string scorerVersion, string toStatus, string? note, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scorerVersion);
        if (toStatus is not (ModelStatuses.Shadow or ModelStatuses.Active))
        {
            throw new ArgumentException("toStatus must be 'shadow' or 'active'.", nameof(toStatus));
        }

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.ModelRegistry
            SET Status = @Retired, RetiredUtc = SYSUTCDATETIME(), UpdatedUtc = SYSUTCDATETIME()
            WHERE Status = @ToStatus;
            """,
            new { Retired = ModelStatuses.Retired, ToStatus = toStatus },
            transaction: tx, cancellationToken: ct)).ConfigureAwait(false);

        var promoted = (await conn.QueryAsync<Guid>(new CommandDefinition(
            """
            UPDATE dbo.ModelRegistry
            SET Status         = @ToStatus,
                ShadowSinceUtc = CASE WHEN @ToStatus = @Shadow THEN SYSUTCDATETIME() ELSE ShadowSinceUtc END,
                ActiveSinceUtc = CASE WHEN @ToStatus = @Active THEN SYSUTCDATETIME() ELSE ActiveSinceUtc END,
                RetiredUtc     = NULL,
                Notes          = @Note,
                UpdatedUtc     = SYSUTCDATETIME()
            OUTPUT inserted.ModelId
            WHERE ScorerVersion = @ScorerVersion
              AND Status IN (@Candidate, @Shadow, @Retired);
            """,
            new
            {
                ToStatus = toStatus,
                Shadow = ModelStatuses.Shadow,
                Active = ModelStatuses.Active,
                Note = note,
                ScorerVersion = scorerVersion,
                Candidate = ModelStatuses.Candidate,
                Retired = ModelStatuses.Retired,
            },
            transaction: tx, cancellationToken: ct)).ConfigureAwait(false)).AsList();

        if (promoted.Count == 0)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"No promotable row for scorer_version '{scorerVersion}' (it may not exist, or is 'active'/'rejected').");
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>ONE transaction: retires the current 'active' row, then promotes the
    /// rollback target to 'active'. <paramref name="toScorerVersion"/> == "none" just
    /// retires the active row (host falls back to the heuristic at restart); null/empty
    /// picks the most recently retired row that once carried ActiveSinceUtc, EXCLUDING
    /// the row this call just retired. Returns the resulting active scorer_version, or
    /// "none". Throws (CLI maps to exit code 4) when there is nothing to roll back to.</summary>
    public async Task<string> RollbackAsync(string? toScorerVersion, string? note, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var retiredActiveIds = (await conn.QueryAsync<Guid>(new CommandDefinition(
            """
            UPDATE dbo.ModelRegistry
            SET Status = @Retired, RetiredUtc = SYSUTCDATETIME(), UpdatedUtc = SYSUTCDATETIME()
            OUTPUT inserted.ModelId
            WHERE Status = @Active;
            """,
            new { Retired = ModelStatuses.Retired, Active = ModelStatuses.Active },
            transaction: tx, cancellationToken: ct)).ConfigureAwait(false)).AsList();
        var justRetiredId = retiredActiveIds.Count > 0 ? retiredActiveIds[0] : (Guid?)null;

        if (string.Equals(toScorerVersion, "none", StringComparison.OrdinalIgnoreCase))
        {
            if (justRetiredId is null)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw new InvalidOperationException("No active model to roll back — nothing to do.");
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return "none";
        }

        string target;
        if (toScorerVersion is { Length: > 0 })
        {
            target = toScorerVersion;
        }
        else
        {
            var fallback = await conn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
                """
                SELECT TOP (1) ScorerVersion
                FROM dbo.ModelRegistry
                WHERE Status = @Retired AND ActiveSinceUtc IS NOT NULL AND ScorerVersion IS NOT NULL
                  AND (@JustRetiredId IS NULL OR ModelId <> @JustRetiredId)
                ORDER BY ActiveSinceUtc DESC;
                """,
                new { Retired = ModelStatuses.Retired, JustRetiredId = justRetiredId },
                transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            if (fallback is null)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw new InvalidOperationException(
                    "nothing to roll back to — use `promote --status active` explicitly");
            }
            target = fallback;
        }

        var promoted = (await conn.QueryAsync<Guid>(new CommandDefinition(
            """
            UPDATE dbo.ModelRegistry
            SET Status         = @Active,
                ActiveSinceUtc = SYSUTCDATETIME(),
                RetiredUtc     = NULL,
                Notes          = @Note,
                UpdatedUtc     = SYSUTCDATETIME()
            OUTPUT inserted.ModelId
            WHERE ScorerVersion = @ScorerVersion
              AND Status IN (@Candidate, @Shadow, @Retired);
            """,
            new
            {
                Active = ModelStatuses.Active,
                Note = note,
                ScorerVersion = target,
                Candidate = ModelStatuses.Candidate,
                Shadow = ModelStatuses.Shadow,
                Retired = ModelStatuses.Retired,
            },
            transaction: tx, cancellationToken: ct)).ConfigureAwait(false)).AsList();

        if (promoted.Count == 0)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"Rollback target scorer_version '{target}' is not a promotable row.");
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return target;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(sqlConnectionString);
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await conn.ExecuteAsync(new CommandDefinition(
                "EXEC sp_set_session_context @key = N'TenantId', @value = @tid, @read_only = 1",
                new { tid = SystemSentinel }, cancellationToken: ct)).ConfigureAwait(false);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
