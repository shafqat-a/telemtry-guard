using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using TelemetryGuard.Data;
using TelemetryGuard.Data.Repositories;
using TelemetryGuard.Training;
// TelemetryGuard.Data.Repositories and TelemetryGuard.Training each declare their own
// ModelStatuses type (P2-02 step 8's deliberate, test-proven duplication — see
// ModelRegistryClient's class doc). Alias the registry-read-side (Data) one; it is
// what the serving host actually branches on.
using ModelStatuses = TelemetryGuard.Data.Repositories.ModelStatuses;

namespace TelemetryGuard.Tests.Integration.Sql;

/// <summary>
/// P2-02: dbo.ModelRegistry (migration 0009) against the real SQL Server fixture.
/// PLATFORM-scoped (no TenantId, no RLS) — every test uses a fresh scorer_version so
/// the suite stays order-independent even though the table itself is NOT tenant-
/// isolated (DAT-08's usual per-tenant seam does not apply here).
///
/// Exercises BOTH halves of migration 0009's shared contract: writes go through
/// TelemetryGuard.Training.ModelRegistryClient (the real CLI write path) and reads go
/// through TelemetryGuard.Data.Repositories.ModelRegistryRepository (the real serving-
/// host read path) — this is what proves the two independently hand-written column
/// lists never drift apart.
/// </summary>
[Collection("sqlserver")]
[Trait("Category", "Integration")]
public sealed class ModelRegistryRepositoryTests(SqlServerFixture fx)
{
    private IModelRegistryRepository Repo() =>
        new ModelRegistryRepository(SystemConnections.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Main"] = fx.ConnectionString,
            }).Build()));

    private ModelRegistryClient Client() => new(fx.ConnectionString);

    private static string FreshVersion(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..30];

    private static NewModelRun Run(string? scorerVersion, string status, string? rejectReason = null) => new(
        ScorerVersion: scorerVersion,
        FeatureSetVersion: 1,
        Status: status,
        TrainedUtc: DateTime.UtcNow,
        WindowFrom: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)),
        WindowTo: DateOnly.FromDateTime(DateTime.UtcNow),
        TrainRows: 800, ValidationRows: 200, Positives: 500, Negatives: 500, DroppedConflicts: 0,
        Auc: 0.91, Auprc: 0.83, F1: 0.77, MinAucGate: 0.85, ScoreP99Ms: 1.234,
        ArtifactPath: scorerVersion is null ? null : $"/tmp/models/{scorerVersion}",
        ArtifactSha256: scorerVersion is null ? null : SHA256.HashData(Guid.NewGuid().ToByteArray()),
        MetadataJson: scorerVersion is null ? null : """{"scorer_version":"x"}""",
        GateJson: """{"passed":true,"reasons":[]}""",
        RejectReason: rejectReason);

    /// <summary>Best-effort: clear whatever is currently 'active' cluster-wide so this
    /// test's own promotion-to-active step is unambiguous. Swallows "nothing to roll
    /// back" — that just means the table was already clean.</summary>
    private async Task ResetActiveAsync()
    {
        try { await Client().RollbackAsync("none", "test-setup-reset", CancellationToken.None); }
        catch (InvalidOperationException) { /* already clean */ }
    }

    [Fact]
    public async Task Lifecycle_InsertPromoteShadowPromoteActiveRollback_ServingModelReflectsEachStep_RoundTrip()
    {
        await ResetActiveAsync();

        var client = Client();
        var repo = Repo();
        var a = FreshVersion("lgbm-a");
        var b = FreshVersion("lgbm-b");

        await client.InsertRunAsync(Run(a, ModelStatuses.Candidate), CancellationToken.None);
        await client.InsertRunAsync(Run(b, ModelStatuses.Candidate), CancellationToken.None);

        // candidate: not servable yet.
        var afterInsert = await repo.GetServingModelAsync(CancellationToken.None);
        Assert.True(afterInsert is null || (afterInsert.ScorerVersion != a && afterInsert.ScorerVersion != b));

        // promote A to shadow — PromoteAsync retires whatever else held 'shadow'
        // cluster-wide, so A is now the UNIQUE shadow row.
        await client.PromoteAsync(a, ModelStatuses.Shadow, note: "test", CancellationToken.None);
        var afterShadow = await repo.GetServingModelAsync(CancellationToken.None);
        Assert.NotNull(afterShadow);
        Assert.Equal(a, afterShadow!.ScorerVersion);
        Assert.Equal(ModelStatuses.Shadow, afterShadow.Status);

        // promote B to active — active wins over shadow even though A is still shadow.
        await client.PromoteAsync(b, ModelStatuses.Active, note: "test", CancellationToken.None);
        var afterActive = await repo.GetServingModelAsync(CancellationToken.None);
        Assert.NotNull(afterActive);
        Assert.Equal(b, afterActive!.ScorerVersion);
        Assert.Equal(ModelStatuses.Active, afterActive.Status);

        // Round-trip check: everything InsertRunAsync (Training) wrote for B is read
        // back correctly by ListRecentAsync (Data) — same column contract, independent
        // hand-written SQL on each side.
        var recent = await repo.ListRecentAsync(200, CancellationToken.None);
        var bEntry = Assert.Single(recent, r => r.ScorerVersion == b);
        Assert.Equal(1, bEntry.FeatureSetVersion);
        Assert.Equal(800, bEntry.TrainRows);
        Assert.Equal(200, bEntry.ValidationRows);
        Assert.Equal(500, bEntry.Positives);
        Assert.Equal(500, bEntry.Negatives);
        Assert.Equal(0.91, bEntry.Auc, precision: 6);
        Assert.Equal(0.83, bEntry.Auprc, precision: 6);
        Assert.Equal(0.77, bEntry.F1, precision: 6);
        Assert.Equal(0.85, bEntry.MinAucGate, precision: 6);
        Assert.NotNull(bEntry.ArtifactSha256);
        Assert.Equal(32, bEntry.ArtifactSha256!.Length);
        Assert.Equal($"/tmp/models/{b}", bEntry.ArtifactPath);
        Assert.Equal(ModelStatuses.Active, bEntry.Status);
        Assert.NotNull(bEntry.ActiveSinceUtc);

        // rollback --to none: retires B; A (still 'shadow') becomes the serving model.
        var result = await client.RollbackAsync("none", "test", CancellationToken.None);
        Assert.Equal("none", result);
        var afterRollback = await repo.GetServingModelAsync(CancellationToken.None);
        Assert.NotNull(afterRollback);
        Assert.Equal(a, afterRollback!.ScorerVersion);
        Assert.Equal(ModelStatuses.Shadow, afterRollback.Status);
    }

    [Fact]
    public async Task UX_ModelRegistry_SingleActive_RejectsSecondActiveRow()
    {
        await ResetActiveAsync();
        var client = Client();
        var v = FreshVersion("lgbm-single-active");
        await client.InsertRunAsync(Run(v, ModelStatuses.Candidate), CancellationToken.None);
        await client.PromoteAsync(v, ModelStatuses.Active, note: null, CancellationToken.None);

        await using var conn = new SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await Assert.ThrowsAsync<SqlException>(() => conn.ExecuteAsync(
            """
            INSERT INTO dbo.ModelRegistry
                (ScorerVersion, FeatureSetVersion, Status, TrainedUtc, WindowFromUtc, WindowToUtc,
                 TrainRows, ValidationRows, Positives, Negatives, DroppedConflicts, Auc, Auprc, F1, MinAucGate,
                 ArtifactPath, ArtifactSha256)
            VALUES
                (@ScorerVersion, 1, 'active', SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME(),
                 10, 10, 10, 10, 0, 0.9, 0.9, 0.9, 0.85, @ArtifactPath, @Hash);
            """,
            new
            {
                ScorerVersion = FreshVersion("lgbm-dup-active"),
                ArtifactPath = "/tmp/x",
                Hash = SHA256.HashData(Guid.NewGuid().ToByteArray()),
            }));
    }

    [Fact]
    public async Task UX_ModelRegistry_SingleShadow_RejectsSecondShadowRow()
    {
        var client = Client();
        var v = FreshVersion("lgbm-single-shadow");
        await client.InsertRunAsync(Run(v, ModelStatuses.Candidate), CancellationToken.None);
        await client.PromoteAsync(v, ModelStatuses.Shadow, note: null, CancellationToken.None);

        await using var conn = new SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await Assert.ThrowsAsync<SqlException>(() => conn.ExecuteAsync(
            """
            INSERT INTO dbo.ModelRegistry
                (ScorerVersion, FeatureSetVersion, Status, TrainedUtc, WindowFromUtc, WindowToUtc,
                 TrainRows, ValidationRows, Positives, Negatives, DroppedConflicts, Auc, Auprc, F1, MinAucGate,
                 ArtifactPath, ArtifactSha256)
            VALUES
                (@ScorerVersion, 1, 'shadow', SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME(),
                 10, 10, 10, 10, 0, 0.9, 0.9, 0.9, 0.85, @ArtifactPath, @Hash);
            """,
            new
            {
                ScorerVersion = FreshVersion("lgbm-dup-shadow"),
                ArtifactPath = "/tmp/x",
                Hash = SHA256.HashData(Guid.NewGuid().ToByteArray()),
            }));
    }

    [Fact]
    public async Task CK_MR_Artifact_RejectsServableRowWithoutArtifact_ButAllowsRejectedWithout()
    {
        await using var conn = new SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();

        // A 'candidate' row with no ScorerVersion/ArtifactPath/ArtifactSha256 violates
        // CK_MR_Artifact — anything that may ever serve MUST carry a verifiable artifact.
        await Assert.ThrowsAsync<SqlException>(() => conn.ExecuteAsync(
            """
            INSERT INTO dbo.ModelRegistry
                (FeatureSetVersion, Status, TrainedUtc, WindowFromUtc, WindowToUtc,
                 TrainRows, ValidationRows, Positives, Negatives, DroppedConflicts, Auc, Auprc, F1, MinAucGate)
            VALUES
                (1, 'candidate', SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME(), 10, 10, 10, 10, 0, 0.5, 0.5, 0.5, 0.85);
            """));

        // A 'rejected' row with the same NULLs is explicitly allowed (gate-failed runs
        // export nothing).
        var rows = await conn.ExecuteAsync(
            """
            INSERT INTO dbo.ModelRegistry
                (FeatureSetVersion, Status, TrainedUtc, WindowFromUtc, WindowToUtc,
                 TrainRows, ValidationRows, Positives, Negatives, DroppedConflicts, Auc, Auprc, F1, MinAucGate, RejectReason)
            VALUES
                (1, 'rejected', SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME(), 10, 10, 10, 10, 0, 0.5, 0.5, 0.5, 0.85, 'AUC too low');
            """);
        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task RejectedRow_CanNeverBePromoted()
    {
        var client = Client();
        // A rejected run CAN carry a real artifact (the incumbent-comparison gate fails
        // AFTER the AUC gate passed and an artifact was exported) — this is the
        // stricter case: even with a full artifact, 'rejected' must never be promotable.
        var v = FreshVersion("lgbm-rejected");
        await client.InsertRunAsync(Run(v, ModelStatuses.Rejected, rejectReason: "incumbent gate failed"), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PromoteAsync(v, ModelStatuses.Shadow, note: null, CancellationToken.None));
        Assert.Contains(v, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelRegistry_HasNoTenantIdColumn_AndIsAbsentFromRls()
    {
        await using var conn = new SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();

        var tenantIdColumns = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.ModelRegistry') AND name = 'TenantId';");
        Assert.Equal(0, tenantIdColumns);

        var predicatesOnModelRegistry = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM sys.security_predicates sp
            JOIN sys.tables t ON sp.target_object_id = t.object_id
            WHERE t.name = 'ModelRegistry';
            """);
        Assert.Equal(0, predicatesOnModelRegistry);

        // Positive control: a genuinely tenant-scoped table DOES carry security
        // predicates — proves the query above isn't trivially empty (e.g. a typo).
        var predicatesOnCampaigns = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM sys.security_predicates sp
            JOIN sys.tables t ON sp.target_object_id = t.object_id
            WHERE t.name = 'Campaigns';
            """);
        Assert.True(predicatesOnCampaigns > 0);
    }
}
