using TelemetryGuard.MigrationRunner;

if (args.Length > 0 && args[0].Equals("provision", StringComparison.OrdinalIgnoreCase))
{
    var provisionCs = Environment.GetEnvironmentVariable("MIGRATIONS_CONNECTIONSTRING");
    if (string.IsNullOrWhiteSpace(provisionCs))
    {
        Console.Error.WriteLine("MIGRATIONS_CONNECTIONSTRING environment variable is not set.");
        return 2;
    }
    return await TelemetryGuard.MigrationRunner.Provisioning.ProvisionCommand.RunAsync(args[1..], provisionCs);
}

if (args.Contains("--clickhouse", StringComparer.OrdinalIgnoreCase))
{
    var chCs = Environment.GetEnvironmentVariable("CLICKHOUSE_CONNECTIONSTRING");
    if (string.IsNullOrWhiteSpace(chCs))
    {
        Console.Error.WriteLine("CLICKHOUSE_CONNECTIONSTRING environment variable is not set.");
        return 2;
    }
    return await Migrations.RunClickHouseAsync(chCs);   // delegates to ANA-02's SchemaMigrator
}

if (args.Contains("--kusto", StringComparer.OrdinalIgnoreCase))
{
    var kustoCs = Environment.GetEnvironmentVariable("KUSTO_CONNECTIONSTRING");
    if (string.IsNullOrWhiteSpace(kustoCs))
    {
        Console.Error.WriteLine("KUSTO_CONNECTIONSTRING environment variable is not set.");
        return 2;
    }
    var kustoDb = Environment.GetEnvironmentVariable("KUSTO_DATABASE") ?? "telemetry_guard";
    var streaming = Environment.GetEnvironmentVariable("KUSTO_ENABLE_STREAMING") == "1";
    return await Migrations.RunKustoAsync(kustoCs, kustoDb, streaming);   // delegates to P2-05's KustoSchemaMigrator
}

var cs = Environment.GetEnvironmentVariable("MIGRATIONS_CONNECTIONSTRING");
if (string.IsNullOrWhiteSpace(cs))
{
    Console.Error.WriteLine("MIGRATIONS_CONNECTIONSTRING environment variable is not set.");
    return 2;
}
return Migrations.RunSqlServer(cs);
