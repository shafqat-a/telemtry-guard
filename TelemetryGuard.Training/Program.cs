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
        TelemetryGuard.Training (RSK-08) — label pipeline + LightGBM training.

        Usage:
          dotnet run --project TelemetryGuard.Training -- build-labels --from <yyyy-MM-dd> --to <yyyy-MM-dd>
          dotnet run --project TelemetryGuard.Training -- train --from <yyyy-MM-dd> --to <yyyy-MM-dd> [--out <dir>]

        Config (appsettings.json / environment variables):
          ConnectionStrings:ClickHouse   ClickHouse connection string
          ConnectionStrings:Main         SQL Server connection string (dbo.Tenants/dbo.WhitelistEntries)
          Training:MinAuc                Validation AUC gate (default 0.85)
          Training:T1PositiveWeight      Effective weight for t1_rule-sourced labels (default 0.6)

        Exit codes: 0 = success (or --help), 1 = bad args/config, 2 = train's AUC gate failed.
        """);
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
