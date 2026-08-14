using System.Text.Json;
using Microsoft.Extensions.Configuration;
using TelemetryGuard.Training;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();

if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
{
    PrintUsage();
    return 0;
}

var command = args[0];
var rest = args[1..];

if (rest.Contains("--help") || rest.Contains("-h"))
{
    PrintUsage();
    return 0;
}

try
{
    return command switch
    {
        "build-labels" => await RunBuildLabelsAsync(rest, config).ConfigureAwait(false),
        "train" => await RunTrainAsync(rest, config).ConfigureAwait(false),
        "retrain" => await RetrainRunner.RunAsync(rest, config, CancellationToken.None).ConfigureAwait(false),
        "divergence" => await RunDivergenceAsync(rest, config).ConfigureAwait(false),
        "promote" => await RunPromoteAsync(rest, config).ConfigureAwait(false),
        "rollback" => await RunRollbackAsync(rest, config).ConfigureAwait(false),
        "models" => await RunModelsAsync(rest, config).ConfigureAwait(false),
        _ => UnknownCommand(command),
    };
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"Argument error: {ex.Message}");
    PrintUsage();
    return 1;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"Configuration error: {ex.Message}");
    return 1;
}

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"Unknown command '{command}'.");
    PrintUsage();
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine(
        """
        TelemetryGuard.Training (RSK-08/P2-02) — label pipeline, LightGBM training, and
        the retraining loop: model registry, promotion gate, rollback.

        Usage:
          dotnet run --project TelemetryGuard.Training -- build-labels --from <yyyy-MM-dd> --to <yyyy-MM-dd>
          dotnet run --project TelemetryGuard.Training -- train        --from <yyyy-MM-dd> --to <yyyy-MM-dd> [--out <dir>]
          dotnet run --project TelemetryGuard.Training -- retrain      [--from <d>] [--to <d>] [--out <dir>] [--skip-label-build] [--force]
          dotnet run --project TelemetryGuard.Training -- divergence   --from <d> --to <d> [--model <scorer-version>] [--out <dir>]
          dotnet run --project TelemetryGuard.Training -- promote      --model <scorer-version> --status shadow|active [--note "<text>"] [--force]
          dotnet run --project TelemetryGuard.Training -- rollback     [--to <scorer-version>|none] [--note "<text>"]
          dotnet run --project TelemetryGuard.Training -- models       [--limit 20]

        Config (appsettings.json / environment variables):
          ConnectionStrings:ClickHouse   ClickHouse connection string
          ConnectionStrings:Main         SQL Server connection string (dbo.Tenants/dbo.WhitelistEntries/dbo.ModelRegistry)
          Training:MinAuc                Validation AUC gate (default 0.85)
          Training:T1PositiveWeight      Effective weight for t1_rule-sourced labels (default 0.6)
          Training:ArtifactRoot          Default --out for retrain/train (default artifacts/models)
          Training:Retrain:*             WindowDays/MinPositives/MinNegatives/MinIntervalHours
          Training:Promotion:*           MaxAucRegression/MaxAuprcRegression/MaxScoreP99Ms/MinShadowHours/
                                          MinShadowSessions/MinLabeledShadowSessions/MinLiveAucAdvantage
          Training:Bands                 AllowMax/ChallengeMax — MUST mirror the API's Scoring:Bands

        The heuristic ("heuristic-1") remains the enforcing scorer until a human runs
        `promote --status active`. This CLI never trains or promotes automatically, and
        never touches a real database from CI.

        Exit codes: 0 = success (or --help/nothing to do), 1 = bad args/config,
                    2 = training/candidate gate failed (run recorded as 'rejected'),
                    3 = not enough labels / mixed feature_set_version (nothing recorded),
                    4 = promotion or rollback refused (illegal transition or unmet gate).
        """);
}

static string? GetOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name) return args[i + 1];
    }
    return null;
}

static async Task<int> RunDivergenceAsync(string[] args, IConfiguration config)
{
    (DateOnly from, DateOnly to, string? outDirArg) = ParseWindowArgs(args);
    var modelArg = GetOption(args, "--model");
    var clickHouseCs = RequireConnectionString(config, "ClickHouse");
    var sqlCs = RequireConnectionString(config, "Main");
    var t1Weight = config.GetValue<float?>("Training:T1PositiveWeight") ?? 0.6f;
    var allowMax = config.GetValue<int?>("Training:Bands:AllowMax") ?? 30;
    var challengeMax = config.GetValue<int?>("Training:Bands:ChallengeMax") ?? 70;
    var outDir = outDirArg ?? "artifacts/divergence";

    var registry = new ModelRegistryClient(sqlCs);
    string shadowVersion;
    if (!string.IsNullOrWhiteSpace(modelArg))
    {
        shadowVersion = modelArg;
    }
    else
    {
        var shadowRow = (await registry.ListRecentAsync(50, CancellationToken.None).ConfigureAwait(false))
            .FirstOrDefault(m => m.Status == ModelStatuses.Shadow);
        if (shadowRow?.ScorerVersion is null)
        {
            Console.Error.WriteLine(
                "No shadow model registered — pass --model <scorer-version> or `promote --status shadow` a candidate first.");
            return 1;
        }
        shadowVersion = shadowRow.ScorerVersion;
    }

    var tenantIds = await new LabelBuilder(clickHouseCs, sqlCs, _ => { })
        .ListActiveTenantIdsAsync(CancellationToken.None).ConfigureAwait(false);

    var summary = await DivergenceReport.RunAsync(
        clickHouseCs, tenantIds, shadowVersion, from, to, t1Weight, allowMax, challengeMax, CancellationToken.None)
        .ConfigureAwait(false);

    PrintDivergenceSummary(summary);

    Directory.CreateDirectory(outDir);
    var outPath = Path.Combine(outDir, $"divergence-{from:yyyy-MM-dd}_{to:yyyy-MM-dd}.json");
    await File.WriteAllTextAsync(
        outPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }))
        .ConfigureAwait(false);
    Console.WriteLine($"Wrote {outPath}");
    return 0;
}

static void PrintDivergenceSummary(DivergenceSummary s)
{
    Console.WriteLine($"shadow_scorer_version={s.ShadowScorerVersion}");
    Console.WriteLine($"sessions={s.Sessions} rule_floored_sessions={s.RuleFlooredSessions} labeled_sessions={s.LabeledSessions}");
    Console.WriteLine($"band_agreement_rate={s.BandAgreementRate:F4} mean_abs_delta={s.MeanAbsDelta:F3}");
    Console.WriteLine($"shadow_would_block_more={s.ShadowWouldBlockMore} shadow_would_block_fewer={s.ShadowWouldBlockFewer}");
    Console.WriteLine($"enforced_auc={s.EnforcedAuc:F4} shadow_auc={s.ShadowAuc:F4}");
    Console.WriteLine("band matrix (row=enforced, col=shadow; allow/challenge/block):");
    for (var row = 0; row < 3; row++)
    {
        Console.WriteLine("  " + string.Join(" ", Enumerable.Range(0, 3).Select(col => s.BandMatrix[(row * 3) + col])));
    }
}

static async Task<int> RunPromoteAsync(string[] args, IConfiguration config)
{
    var model = GetOption(args, "--model")
        ?? throw new ArgumentException("--model <scorer-version> is required.");
    var status = GetOption(args, "--status")
        ?? throw new ArgumentException("--status shadow|active is required.");
    var note = GetOption(args, "--note");
    var force = args.Contains("--force");

    if (status is not (ModelStatuses.Shadow or ModelStatuses.Active))
    {
        throw new ArgumentException("--status must be 'shadow' or 'active'.");
    }

    var sqlCs = RequireConnectionString(config, "Main");
    var registry = new ModelRegistryClient(sqlCs);
    var effectiveNote = note;

    if (status == ModelStatuses.Active)
    {
        var candidate = await registry.GetByScorerVersionAsync(model, CancellationToken.None).ConfigureAwait(false);
        if (candidate is null)
        {
            Console.Error.WriteLine($"No model registered with scorer_version '{model}'.");
            return 4;
        }

        var incumbent = await registry.GetIncumbentAsync(CancellationToken.None).ConfigureAwait(false);
        var hasActiveIncumbent = incumbent is { Status: ModelStatuses.Active };

        DivergenceSummary? divergence = null;
        if (candidate.Status == ModelStatuses.Shadow)
        {
            var clickHouseCs = RequireConnectionString(config, "ClickHouse");
            var t1Weight = config.GetValue<float?>("Training:T1PositiveWeight") ?? 0.6f;
            var allowMax = config.GetValue<int?>("Training:Bands:AllowMax") ?? 30;
            var challengeMax = config.GetValue<int?>("Training:Bands:ChallengeMax") ?? 70;
            var tenantIds = await new LabelBuilder(clickHouseCs, sqlCs, _ => { })
                .ListActiveTenantIdsAsync(CancellationToken.None).ConfigureAwait(false);
            var since = DateOnly.FromDateTime(candidate.ShadowSinceUtc ?? candidate.TrainedUtc);
            var through = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);
            divergence = await DivergenceReport.RunAsync(
                clickHouseCs, tenantIds, model, since, through, t1Weight, allowMax, challengeMax, CancellationToken.None)
                .ConfigureAwait(false);
        }

        var enforceOptions = new EnforceGateOptions(
            MinShadowHours: config.GetValue<int?>("Training:Promotion:MinShadowHours") ?? 336,
            MinShadowSessions: config.GetValue<int?>("Training:Promotion:MinShadowSessions") ?? 1000,
            MinLabeledShadowSessions: config.GetValue<int?>("Training:Promotion:MinLabeledShadowSessions") ?? 200,
            MinLiveAucAdvantage: config.GetValue<double?>("Training:Promotion:MinLiveAucAdvantage") ?? 0.0);

        var gate = PromotionGate.EvaluateEnforcePromotion(
            candidate, DateTime.UtcNow, divergence, hasActiveIncumbent, enforceOptions);

        if (!gate.Passed)
        {
            if (!force)
            {
                Console.Error.WriteLine("Promotion to 'active' REFUSED:");
                foreach (var reason in gate.Reasons) Console.Error.WriteLine($"  - {reason}");
                return 4;
            }

            Console.WriteLine("WARNING: --force bypassing the enforce gate. Unmet reasons:");
            foreach (var reason in gate.Reasons) Console.WriteLine($"  - {reason}");
            var forcedNote = $"FORCED: {string.Join("; ", gate.Reasons)}";
            effectiveNote = note is null ? forcedNote : $"{forcedNote} | {note}";
        }
    }

    try
    {
        await registry.PromoteAsync(model, status, effectiveNote, CancellationToken.None).ConfigureAwait(false);
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"Promotion REFUSED: {ex.Message}");
        return 4;
    }

    Console.WriteLine(
        $"Promoted {model} to '{status}'. RESTART the API host to apply — a running process " +
        "never changes its enforcing scorer (D18 lineage).");
    return 0;
}

static async Task<int> RunRollbackAsync(string[] args, IConfiguration config)
{
    var to = GetOption(args, "--to");
    var note = GetOption(args, "--note");
    var sqlCs = RequireConnectionString(config, "Main");
    var registry = new ModelRegistryClient(sqlCs);

    try
    {
        var result = await registry.RollbackAsync(to, note, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine(
            result == "none"
                ? "Rolled back: no active model remains. RESTART the API host — it falls back to the heuristic (D18 lineage)."
                : $"Rolled back to {result} as 'active'. RESTART the API host to apply (D18 lineage).");
        return 0;
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"Rollback REFUSED: {ex.Message}");
        return 4;
    }
}

static async Task<int> RunModelsAsync(string[] args, IConfiguration config)
{
    var limitArg = GetOption(args, "--limit");
    var limit = limitArg is null ? 20 : int.Parse(limitArg);

    var sqlCs = RequireConnectionString(config, "Main");
    var registry = new ModelRegistryClient(sqlCs);
    var rows = await registry.ListRecentAsync(limit, CancellationToken.None).ConfigureAwait(false);

    Console.WriteLine("ScorerVersion                 Status     Auc      TrainedUtc           ShadowSince          ActiveSince");
    foreach (var row in rows)
    {
        Console.WriteLine(
            $"{(row.ScorerVersion ?? "(none)"),-30} {row.Status,-10} {row.Auc,-8:F4} {row.TrainedUtc,-20:yyyy-MM-dd HH:mm} " +
            $"{(row.ShadowSinceUtc?.ToString("yyyy-MM-dd HH:mm") ?? "-"),-20} {(row.ActiveSinceUtc?.ToString("yyyy-MM-dd HH:mm") ?? "-")}");
    }
    return 0;
}

static (DateOnly From, DateOnly To, string? Out) ParseWindowArgs(string[] args)
{
    DateOnly? from = null, to = null;
    string? outDir = null;
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--from" when i + 1 < args.Length:
                from = DateOnly.Parse(args[++i]);
                break;
            case "--to" when i + 1 < args.Length:
                to = DateOnly.Parse(args[++i]);
                break;
            case "--out" when i + 1 < args.Length:
                outDir = args[++i];
                break;
        }
    }
    if (from is null || to is null)
    {
        throw new ArgumentException("--from and --to (yyyy-MM-dd) are required.");
    }
    return (from.Value, to.Value, outDir);
}

static async Task<int> RunBuildLabelsAsync(string[] args, IConfiguration config)
{
    (DateOnly from, DateOnly to, _) = ParseWindowArgs(args);
    var clickHouseCs = RequireConnectionString(config, "ClickHouse");
    var sqlCs = RequireConnectionString(config, "Main");

    var builder = new LabelBuilder(clickHouseCs, sqlCs, msg => Console.WriteLine(msg));
    var result = await builder.RunAsync(from, to, CancellationToken.None).ConfigureAwait(false);

    Console.WriteLine($"Tenants processed: {result.PerTenant.Count}");
    Console.WriteLine($"t1_rule inserted (backfill): {result.TotalT1Positives}");
    Console.WriteLine($"synthetic_bot counted (not inserted — written live at ingest): {result.TotalSyntheticBotCounted}");
    Console.WriteLine($"review_screen backfilled: {result.TotalReviewScreenBackfilled}");
    return 0;
}

static async Task<int> RunTrainAsync(string[] args, IConfiguration config)
{
    (DateOnly from, DateOnly to, string? outDirArg) = ParseWindowArgs(args);
    var clickHouseCs = RequireConnectionString(config, "ClickHouse");
    var sqlCs = RequireConnectionString(config, "Main");
    var minAuc = config.GetValue<double?>("Training:MinAuc") ?? 0.85;
    var t1Weight = config.GetValue<float?>("Training:T1PositiveWeight") ?? 0.6f;
    var outDir = outDirArg ?? "artifacts/models";

    var tenantIds = await new LabelBuilder(clickHouseCs, sqlCs, _ => { })
        .ListActiveTenantIdsAsync(CancellationToken.None).ConfigureAwait(false);

    var trainer = new Trainer();
    return await trainer.RunFromDatabaseAsync(
        clickHouseCs, tenantIds, from, to, minAuc, t1Weight, outDir, CancellationToken.None)
        .ConfigureAwait(false);
}

static string RequireConnectionString(IConfiguration config, string name)
{
    var value = config.GetConnectionString(name);
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"ConnectionStrings:{name} is not configured.");
    }
    return value;
}
