using ClickHouse.Client.Utility;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Analytics.ClickHouse;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Data;
using TelemetryGuard.Data.Models;
using TelemetryGuard.Data.Repositories;
using TelemetryGuard.RiskEngine.Contracts;
using TelemetryGuard.Tests.Integration.Sql;
using TelemetryGuard.Training;

namespace TelemetryGuard.Tests.Integration.Services;

/// <summary>
/// P2-02 mini end-to-end, sized for CI: one <see cref="SqlServerFixture"/> (SQL) plus
/// the <see cref="ClickHouseSchemaFixture"/> (ClickHouse). Every dataset stays in the
/// hundreds of rows — the loop must be provable without a large dataset.
///
/// <see cref="RetrainRunner.RunAsync"/> is called IN-PROCESS (never by shelling out to
/// `dotnet run`) — that is the whole reason it is public (see its class doc).
/// dbo.ModelRegistry is GLOBAL (no TenantId, D11 out of scope by design), so every
/// `retrain` call here passes `--force`: the cadence guard (Training:Retrain:
/// MinIntervalHours) is otherwise keyed off the newest row in the WHOLE table, which
/// other tests in the shared "sqlserver" collection may also have written to. Only the
/// one sub-test that specifically proves the cadence guard omits --force.
/// </summary>
[Collection("sqlserver")]
[Trait("Category", "Integration")]
public sealed class RetrainCycleTests(SqlServerFixture sqlFx, ClickHouseSchemaFixture chFx)
    : IClassFixture<ClickHouseSchemaFixture>
{
    private IConfiguration BuildConfig(string artifactRoot, IDictionary<string, string?>? overrides = null)
    {
        var dict = new Dictionary<string, string?>
        {
            ["ConnectionStrings:ClickHouse"] = chFx.ConnectionString,
            ["ConnectionStrings:Main"] = sqlFx.ConnectionString,
            ["Training:MinAuc"] = "0.6",
            ["Training:T1PositiveWeight"] = "0.6",
            ["Training:ArtifactRoot"] = artifactRoot,
            ["Training:Retrain:MinPositives"] = "20",
            ["Training:Retrain:MinNegatives"] = "20",
            // Large on purpose (see class doc) — every call in this suite passes
            // --force except the one deliberately proving the guard fires.
            ["Training:Retrain:MinIntervalHours"] = "999999",
            ["Training:Promotion:MaxAucRegression"] = "1.0",
            ["Training:Promotion:MaxAuprcRegression"] = "1.0",
            ["Training:Promotion:MaxScoreP99Ms"] = "1000",
            ["Training:Promotion:MinShadowHours"] = "0",
            ["Training:Promotion:MinShadowSessions"] = "1",
            ["Training:Promotion:MinLabeledShadowSessions"] = "1",
            ["Training:Promotion:MinLiveAucAdvantage"] = "-1.0",
            ["Training:Bands:AllowMax"] = "30",
            ["Training:Bands:ChallengeMax"] = "70",
        };
        if (overrides is not null)
        {
            foreach (var kv in overrides) dict[kv.Key] = kv.Value;
        }
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    /// <summary>Positives get webdriver_flag/headless + high velocity + scripted mouse
    /// motion; negatives are clean — mirrors TrainerSmokeTests' separable generator.</summary>
    private static FraudFeatureVector SeparableVector(bool fraud, Random rng) => new()
    {
        HasJsBeacon = true,
        WebdriverFlag = fraud,
        HeadlessBrowser = fraud,
        IpClicksLastMin = fraud ? 40 + rng.Next(20) : rng.Next(3),
        MousePathLinearity = fraud ? 0.98f : 0.4f + ((float)rng.NextDouble() * 0.3f),
        StdInterEventMs = fraud ? 1f + (float)rng.NextDouble() : 50f + ((float)rng.NextDouble() * 50),
        TimeOnPageSec = fraud ? 1f + (float)rng.NextDouble() : 30f + ((float)rng.NextDouble() * 60),
        IsPaidClick = true,
    };

    /// <summary>Every field drawn from the SAME distribution regardless of the ground-
    /// truth label — zero signal, so validation AUC should land near chance (0.5).</summary>
    private static FraudFeatureVector NoSignalVector(Random rng) => new()
    {
        HasJsBeacon = true,
        WebdriverFlag = rng.NextDouble() < 0.5,
        IpClicksLastMin = rng.Next(50),
        MousePathLinearity = (float)rng.NextDouble(),
        StdInterEventMs = (float)rng.NextDouble() * 100,
        TimeOnPageSec = (float)rng.NextDouble() * 90,
        IsPaidClick = true,
    };

    private static ClickEvent VerdictEvent(
        Guid tenantId, string sessionId, DateTime ts, int score, string band, FraudFeatureVector vector,
        int featureSetVersion = 1, int? shadowScore = null, string? shadowScorerVersion = null) =>
        TestEvents.Verdict(new TenantId(tenantId), "203.0.113.10", ts, sessionId, score, band) with
        {
            Features = System.Text.Json.JsonSerializer.Serialize(vector, FraudFeatureVectorJson.Options),
            ScorerVersion = "heuristic-1",
            FeatureSetVersion = featureSetVersion,
            ShadowScore = shadowScore,
            ShadowScorerVersion = shadowScorerVersion,
        };

    private static async Task SeedLabelsRawAsync(string connectionString, IReadOnlyList<LabelEvent> labels)
    {
        var sink = new ClickHouseLabelSink(
            Options.Create(new ClickHouseAnalyticsOptions
            {
                ConnectionString = connectionString,
                LabelMaxBatchSize = 5_000,
                LabelMaxBatchAgeSeconds = 30,
            }),
            NullLogger<ClickHouseLabelSink>.Instance);
        await sink.StartAsync(CancellationToken.None);
        foreach (var l in labels) await sink.WriteAsync(l, CancellationToken.None);
        await sink.StopAsync(CancellationToken.None);
    }

    /// <summary>Counts rows matching the EXACT shape <c>Trainer.ReadEventFeaturesAsync</c>
    /// / <c>ReadLabelsAsync</c> filter on in production — tenant, (kind for events),
    /// and the time-column range — so this check can only pass when the rows are
    /// genuinely visible to the real read path, not just present in a bare table
    /// count.</summary>
    private static async Task<long> CountExactAsync(
        string connectionString, string table, string timeColumn,
        Guid tenantId, string? kind, DateTime from, DateTime to)
    {
        await using var conn = new ClickHouse.Client.ADO.ClickHouseConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT count() FROM {table} WHERE tenant_id = {{t:UUID}} " +
            (kind is null ? "" : "AND kind = {k:String} ") +
            $"AND {timeColumn} >= {{f:DateTime64(3,'UTC')}} AND {timeColumn} < {{to:DateTime64(3,'UTC')}}";
        cmd.AddParameter("t", tenantId);
        if (kind is not null) cmd.AddParameter("k", kind);
        cmd.AddParameter("f", from);
        cmd.AddParameter("to", to);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    /// <summary>
    /// A freshly-created Testcontainers ClickHouse instance occasionally mis-lands a
    /// bulk-copy write under heavy concurrent container load: the table's total row
    /// count increases by exactly the right amount, but the rows are not all findable
    /// by the exact filter shape the production read path uses (observed empirically —
    /// a driver/engine warm-up quirk, not a data-shape problem). ClickHouseEventSink's
    /// own retry loop covers mid-stream flush failures but not this kind of silent
    /// mis-land, and it logs through a caller-supplied ILogger that TestEvents.SeedAsync
    /// deliberately wires to NullLogger — invisible without this check. Retries the
    /// WHOLE seed only when the count comes up short — safe because every session_id in
    /// <paramref name="events"/> is unique per test run (fresh GUIDs), so a retried
    /// write can only ever raise the matching count toward the target, never overshoot
    /// it into a false negative.
    /// </summary>
    private static async Task SeedEventsAsync(
        string connectionString, IReadOnlyList<ClickEvent> events, DateTime from, DateTime to)
    {
        if (events.Count == 0) return;
        var tenantId = events[0].TenantId.Value;
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            var before = await CountExactAsync(connectionString, "tg_events", "timestamp", tenantId, "verdict", from, to);
            await TestEvents.SeedAsync(connectionString, events);
            var after = await CountExactAsync(connectionString, "tg_events", "timestamp", tenantId, "verdict", from, to);
            var landed = after - before;
            Console.WriteLine($"[SeedEventsAsync] attempt={attempt} landed={landed} want={events.Count}");
            if (landed >= events.Count) return;
            await Task.Delay(TimeSpan.FromMilliseconds(750 * attempt));
        }
        throw new InvalidOperationException(
            $"SeedEventsAsync: exact-filtered count for {events.Count} events never reached target after 8 attempts.");
    }

    private static async Task SeedLabelsAsync(
        string connectionString, IReadOnlyList<LabelEvent> labels, DateTime from, DateTime to)
    {
        if (labels.Count == 0) return;
        var tenantId = labels[0].TenantId.Value;
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            var before = await CountExactAsync(connectionString, "tg_labels", "created_at", tenantId, kind: null, from, to);
            await SeedLabelsRawAsync(connectionString, labels);
            var after = await CountExactAsync(connectionString, "tg_labels", "created_at", tenantId, kind: null, from, to);
            var landed = after - before;
            Console.WriteLine($"[SeedLabelsAsync] attempt={attempt} landed={landed} want={labels.Count}");
            if (landed >= labels.Count) return;
            await Task.Delay(TimeSpan.FromMilliseconds(750 * attempt));
        }
        throw new InvalidOperationException(
            $"SeedLabelsAsync: exact-filtered count for {labels.Count} labels never reached target after 8 attempts.");
    }

    /// <summary>Retries a `retrain` call that unexpectedly returns exit 3 ("not enough
    /// labels") when the caller has already verified the underlying ClickHouse rows
    /// landed — safe because exit 3 registers nothing (no InsertRunAsync call at all),
    /// so there is no partial state to clean up between attempts. Window is 20 calls *
    /// 3s = up to 60s: observed empirically to occasionally outlast the previous 10 *
    /// 2s = 20s budget on loaded podman/dev hosts (SeedEventsAsync/SeedLabelsAsync above
    /// already confirm the write itself landed via an exact-filter count — this loop is
    /// purely riding out read-visibility lag on a distinct connection/query shape).</summary>
    private static async Task<int> RunAsyncWithRetryOnTransientEmptyRead(string[] args, IConfiguration config)
    {
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            var exit = await RetrainRunner.RunAsync(args, config, CancellationToken.None);
            if (exit != 3) return exit;
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        return await RetrainRunner.RunAsync(args, config, CancellationToken.None);
    }

    [Fact]
    public async Task FullLifecycle_RetrainCadenceForceDivergePromoteRollback()
    {
        // Future-dated like the other windows in this class (2027/2028): tg_events rows
        // carry TTL timestamp + retention_days, so a past window silently expires the
        // seeds between the insert-verify count and the trainer's read.
        var window1From = new DateTime(2029, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var window1To = window1From.AddDays(5);
        var rng = new Random(123);
        var events = new List<ClickEvent>();
        var labels = new List<LabelEvent>();
        for (var i = 0; i < 300; i++)
        {
            var fraud = i % 2 == 0;
            var sessionId = $"w1-{i}-{Guid.NewGuid():N}";
            var ts = window1From.AddMinutes(i);
            events.Add(VerdictEvent(
                SqlServerFixture.TenantA, sessionId, ts, fraud ? 80 : 10, fraud ? "block" : "allow",
                SeparableVector(fraud, rng)));
            labels.Add(new LabelEvent(
                new TenantId(SqlServerFixture.TenantA), sessionId,
                fraud ? LabelValues.Fraud : LabelValues.Legit,
                fraud ? LabelSources.SyntheticBot : LabelSources.ReviewScreen, ts));
        }
        await SeedEventsAsync(chFx.ConnectionString, events, window1From, window1To);
        await SeedLabelsAsync(chFx.ConnectionString, labels, window1From, window1To);

        var outDir = Path.Combine(Path.GetTempPath(), "tg-retrain-cycle-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = BuildConfig(outDir);
            var registry = new ModelRegistryClient(sqlFx.ConnectionString);

            // dbo.ModelRegistry is GLOBAL (see class doc): other classes in the shared
            // "sqlserver" collection leave rows behind, and a leftover 'active' row
            // outranks this test's 'shadow' promotion in GetServingModelAsync. This
            // test owns the full lifecycle narrative, so it starts from a clean table
            // (safe: the collection runs classes sequentially).
            await using (var wipe = new Microsoft.Data.SqlClient.SqlConnection(sqlFx.ConnectionString))
            {
                await wipe.OpenAsync();
                await using var wipeCmd = wipe.CreateCommand();
                wipeCmd.CommandText = "DELETE FROM dbo.ModelRegistry;";
                await wipeCmd.ExecuteNonQueryAsync();
            }

            // ---- 1. retrain -> one candidate row, artifact on disk, exit 0 ----
            var argsWindow1 = new[]
            {
                "retrain", "--from", window1From.ToString("yyyy-MM-dd"), "--to", window1To.ToString("yyyy-MM-dd"),
                "--skip-label-build", "--force",
            };
            // A ClickHouse read immediately after a verified write can occasionally lag
            // under heavy concurrent container load on shared CI/dev hardware (the write
            // itself is confirmed landed by SeedEventsAsync/SeedLabelsAsync above). exit 3
            // ("not enough labels") registers NOTHING, so retrying the whole call is safe.
            // See RunAsyncWithRetryOnTransientEmptyRead's doc for the retry budget.
            var exit1 = await RunAsyncWithRetryOnTransientEmptyRead(argsWindow1, config);
            Assert.Equal(0, exit1);

            var candidate = (await registry.ListRecentAsync(1, CancellationToken.None)).Single();
            var scorerVersion = candidate.ScorerVersion;
            Assert.NotNull(scorerVersion);
            Assert.Equal("candidate", candidate.Status);
            Assert.NotNull(candidate.ArtifactPath);
            Assert.True(File.Exists(Path.Combine(candidate.ArtifactPath!, "model.zip")));

            // MetadataJson/GateJson aren't surfaced by either read-side record (by
            // design — see ModelRegistryRow/ModelRegistryEntry) — verify with raw SQL.
            var (metadataJson, gateJson) = await ReadJsonColumnsAsync(scorerVersion!);
            Assert.False(string.IsNullOrWhiteSpace(metadataJson));
            Assert.False(string.IsNullOrWhiteSpace(gateJson));

            // ---- 2a. re-run inside MinIntervalHours, NO --force -> exit 0, no new row ----
            var countBeforeGuard = (await registry.ListRecentAsync(5000, CancellationToken.None)).Count;
            var argsNoForce = new[]
            {
                "retrain", "--from", window1From.ToString("yyyy-MM-dd"), "--to", window1To.ToString("yyyy-MM-dd"),
                "--skip-label-build",
            };
            var exitGuard = await RetrainRunner.RunAsync(argsNoForce, config, CancellationToken.None);
            Assert.Equal(0, exitGuard);
            Assert.Equal(countBeforeGuard, (await registry.ListRecentAsync(5000, CancellationToken.None)).Count);

            // ---- 2b. --force with a slightly expanded window -> a NEW row ----
            var window1bTo = window1To.AddDays(1);
            var extraEvents = new List<ClickEvent>();
            var extraLabels = new List<LabelEvent>();
            for (var i = 0; i < 20; i++)
            {
                var fraud = i % 2 == 0;
                var sessionId = $"w1b-{i}-{Guid.NewGuid():N}";
                var ts = window1To.AddMinutes(i);
                extraEvents.Add(VerdictEvent(
                    SqlServerFixture.TenantA, sessionId, ts, fraud ? 80 : 10, fraud ? "block" : "allow",
                    SeparableVector(fraud, rng)));
                extraLabels.Add(new LabelEvent(
                    new TenantId(SqlServerFixture.TenantA), sessionId,
                    fraud ? LabelValues.Fraud : LabelValues.Legit,
                    fraud ? LabelSources.SyntheticBot : LabelSources.ReviewScreen, ts));
            }
            await SeedEventsAsync(chFx.ConnectionString, extraEvents, window1To, window1bTo);
            await SeedLabelsAsync(chFx.ConnectionString, extraLabels, window1To, window1bTo);

            var countBeforeForce = (await registry.ListRecentAsync(5000, CancellationToken.None)).Count;
            var argsForce = new[]
            {
                "retrain", "--from", window1From.ToString("yyyy-MM-dd"), "--to", window1bTo.ToString("yyyy-MM-dd"),
                "--skip-label-build", "--force",
            };
            var exitForce = await RetrainRunner.RunAsync(argsForce, config, CancellationToken.None);
            Assert.Equal(0, exitForce);
            Assert.Equal(countBeforeForce + 1, (await registry.ListRecentAsync(5000, CancellationToken.None)).Count);

            // ---- 5. promote --status shadow, then a divergence window ----
            await registry.PromoteAsync(scorerVersion!, "shadow", "test-shadow", CancellationToken.None);

            var dataRepo = new ModelRegistryRepository(SystemConnections.FromConfiguration(config));
            var servingShadow = await dataRepo.GetServingModelAsync(CancellationToken.None);
            Assert.NotNull(servingShadow);
            Assert.Equal(scorerVersion, servingShadow!.ScorerVersion);
            Assert.Equal("shadow", servingShadow.Status);

            var divFrom = window1bTo.AddDays(1);
            var divTo = divFrom.AddDays(2);
            var divEvents = new List<ClickEvent>();
            var divLabels = new List<LabelEvent>();
            for (var i = 0; i < 60; i++)
            {
                var fraud = i % 3 == 0;
                var sessionId = $"div-{i}-{Guid.NewGuid():N}";
                var ts = divFrom.AddMinutes(i);
                var enforcedScore = fraud ? 80 : 15;
                var shadowScore = fraud ? 75 : 20;
                divEvents.Add(VerdictEvent(
                    SqlServerFixture.TenantA, sessionId, ts, enforcedScore, fraud ? "block" : "allow",
                    SeparableVector(fraud, rng), shadowScore: shadowScore, shadowScorerVersion: scorerVersion));
                if (i % 2 == 0)
                {
                    divLabels.Add(new LabelEvent(
                        new TenantId(SqlServerFixture.TenantA), sessionId,
                        fraud ? LabelValues.Fraud : LabelValues.Legit,
                        fraud ? LabelSources.SyntheticBot : LabelSources.ReviewScreen, ts));
                }
            }
            await SeedEventsAsync(chFx.ConnectionString, divEvents, divFrom, divTo);
            await SeedLabelsAsync(chFx.ConnectionString, divLabels, divFrom, divTo);

            var tenantIds = new List<Guid> { SqlServerFixture.TenantA, SqlServerFixture.TenantB };
            var divergence = await DivergenceReport.RunAsync(
                chFx.ConnectionString, tenantIds, scorerVersion!,
                DateOnly.FromDateTime(divFrom), DateOnly.FromDateTime(divTo),
                t1PositiveWeight: 0.6f, allowMax: 30, challengeMax: 70, CancellationToken.None);

            Assert.Equal(scorerVersion, divergence.ShadowScorerVersion);
            Assert.Equal(60, divergence.Sessions);
            Assert.Equal(0, divergence.RuleFlooredSessions);
            Assert.Equal(30, divergence.LabeledSessions);

            // ---- 6. promote --status active (relaxed thresholds), then rollback ----
            var candidateRow = await registry.GetByScorerVersionAsync(scorerVersion!, CancellationToken.None);
            Assert.NotNull(candidateRow);
            var incumbentBeforeActive = await registry.GetIncumbentAsync(CancellationToken.None);
            var hasActiveIncumbent = incumbentBeforeActive is { Status: "active" };
            var enforceOptions = new EnforceGateOptions(
                MinShadowHours: 0, MinShadowSessions: 1, MinLabeledShadowSessions: 1, MinLiveAucAdvantage: -1.0);
            var gate = PromotionGate.EvaluateEnforcePromotion(
                candidateRow!, DateTime.UtcNow, divergence, hasActiveIncumbent, enforceOptions);
            Assert.True(gate.Passed, string.Join("; ", gate.Reasons));

            await registry.PromoteAsync(scorerVersion!, "active", "test-active", CancellationToken.None);
            var servingActive = await dataRepo.GetServingModelAsync(CancellationToken.None);
            Assert.NotNull(servingActive);
            Assert.Equal(scorerVersion, servingActive!.ScorerVersion);
            Assert.Equal("active", servingActive.Status);

            var rollbackResult = await registry.RollbackAsync("none", "test-rollback", CancellationToken.None);
            Assert.Equal("none", rollbackResult);
            var servingAfterRollback = await dataRepo.GetServingModelAsync(CancellationToken.None);
            Assert.True(servingAfterRollback is null || servingAfterRollback.ScorerVersion != scorerVersion);
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task Retrain_NoSignalLabels_ExitCode2_RejectedRowWithNullScorerVersion()
    {
        var window2From = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var window2To = window2From.AddDays(5);
        var rng = new Random(555);
        var events = new List<ClickEvent>();
        var labels = new List<LabelEvent>();
        for (var i = 0; i < 400; i++)
        {
            var fraud = i % 2 == 0;
            var sessionId = $"w2-{i}-{Guid.NewGuid():N}";
            var ts = window2From.AddMinutes(i);
            events.Add(VerdictEvent(
                SqlServerFixture.TenantA, sessionId, ts, fraud ? 80 : 10, fraud ? "block" : "allow",
                NoSignalVector(rng)));
            labels.Add(new LabelEvent(
                new TenantId(SqlServerFixture.TenantA), sessionId,
                fraud ? LabelValues.Fraud : LabelValues.Legit,
                fraud ? LabelSources.SyntheticBot : LabelSources.ReviewScreen, ts));
        }
        await SeedEventsAsync(chFx.ConnectionString, events, window2From, window2To);
        await SeedLabelsAsync(chFx.ConnectionString, labels, window2From, window2To);

        var outDir = Path.Combine(Path.GetTempPath(), "tg-retrain-shuffled-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = BuildConfig(outDir);
            var args = new[]
            {
                "retrain", "--from", window2From.ToString("yyyy-MM-dd"), "--to", window2To.ToString("yyyy-MM-dd"),
                "--skip-label-build", "--force",
            };
            var exit = await RetrainRunner.RunAsync(args, config, CancellationToken.None);
            Assert.Equal(2, exit);

            var registry = new ModelRegistryClient(sqlFx.ConnectionString);
            var latest = (await registry.ListRecentAsync(1, CancellationToken.None)).Single();
            Assert.Equal("rejected", latest.Status);
            Assert.Null(latest.ScorerVersion);
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task Retrain_FeatureSetBoundaryStraddle_ExitCode3_NothingRegistered()
    {
        var window3From = new DateTime(2028, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var window3To = window3From.AddDays(2);
        var rng = new Random(7);
        var events = new List<ClickEvent>
        {
            VerdictEvent(
                SqlServerFixture.TenantA, $"w3-good-{Guid.NewGuid():N}", window3From.AddMinutes(1), 80, "block",
                SeparableVector(true, rng), featureSetVersion: 1),
            VerdictEvent(
                SqlServerFixture.TenantA, $"w3-poison-{Guid.NewGuid():N}", window3From.AddMinutes(2), 80, "block",
                SeparableVector(true, rng), featureSetVersion: 99),
        };
        await SeedEventsAsync(chFx.ConnectionString, events, window3From, window3To);

        var outDir = Path.Combine(Path.GetTempPath(), "tg-retrain-boundary-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = BuildConfig(outDir);
            var registry = new ModelRegistryClient(sqlFx.ConnectionString);
            var countBefore = (await registry.ListRecentAsync(5000, CancellationToken.None)).Count;

            var args = new[]
            {
                "retrain", "--from", window3From.ToString("yyyy-MM-dd"), "--to", window3To.ToString("yyyy-MM-dd"),
                "--skip-label-build", "--force",
            };
            var exit = await RetrainRunner.RunAsync(args, config, CancellationToken.None);
            Assert.Equal(3, exit);

            Assert.Equal(countBefore, (await registry.ListRecentAsync(5000, CancellationToken.None)).Count);
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }

    private async Task<(string? MetadataJson, string? GateJson)> ReadJsonColumnsAsync(string scorerVersion)
    {
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(sqlFx.ConnectionString);
        await conn.OpenAsync();
        return await Dapper.SqlMapper.QuerySingleAsync<(string?, string?)>(
            conn,
            "SELECT MetadataJson, GateJson FROM dbo.ModelRegistry WHERE ScorerVersion = @ScorerVersion;",
            new { ScorerVersion = scorerVersion });
    }
}
