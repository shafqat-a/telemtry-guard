using DbUp;
using DbUp.Engine;
using DbUp.Engine.Output;
using DbUp.SqlServer;

namespace TelemetryGuard.MigrationRunner;

public static class Migrations
{
    /// <summary>Creates the database if missing, then applies pending embedded
    /// scripts from TelemetryGuard.Data/migrations. Returns process exit code.</summary>
    public static int RunSqlServer(string connectionString)
    {
        EnsureDatabase.For.SqlDatabase(connectionString);

        var upgrader = DeployChanges.To
            .SqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                typeof(TelemetryGuard.Data.MigrationsAnchor).Assembly,
                name => name.Contains(".migrations.", StringComparison.OrdinalIgnoreCase))
            .JournalToSqlTable("dbo", "SchemaVersions")
            .WithTransactionPerScript()
            .LogToConsole()
            .Build();

        var result = upgrader.PerformUpgrade();
        if (!result.Successful)
        {
            Console.Error.WriteLine(result.Error);
            return 1;
        }

        // DbUp creates the journal table lazily (on the first applied script). While
        // migrations/ holds no .sql files yet, ensure dbo.SchemaVersions exists anyway
        // so the journal is queryable from day one. This reuses DbUp's own SqlTableJournal
        // (same table, same DDL) — it is NOT a second journal or applier.
        var log = new ConsoleUpgradeLog();
        var connectionManager = new SqlConnectionManager(connectionString);
        var journal = new SqlTableJournal(() => connectionManager, () => log, "dbo", "SchemaVersions");
        using (connectionManager.OperationStarting(log, new List<SqlScript>()))
        {
            connectionManager.ExecuteCommandsWithManagedConnection(
                commandFactory => journal.EnsureTableExistsAndIsLatestVersion(commandFactory));
        }

        Console.WriteLine("SQL Server migrations up to date.");
        return 0;
    }

    /// <summary>Delegates ClickHouse schema application to ANA-02's SchemaMigrator
    /// (single journal: tg_schema_migrations).</summary>
    public static async Task<int> RunClickHouseAsync(string connectionString)
    {
        try
        {
            var applied = await new TelemetryGuard.Analytics.ClickHouse
                .SchemaMigrator(connectionString).ApplyAsync();
            foreach (var name in applied) Console.WriteLine($"Applied ClickHouse script {name}");
            Console.WriteLine("ClickHouse schema up to date.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    /// <summary>Delegates Kusto schema application to P2-05's KustoSchemaMigrator
    /// (single journal: tg_schema_migrations). Constructs the options + executor
    /// directly — this console has no DI container.</summary>
    public static async Task<int> RunKustoAsync(string connectionString, string database, bool enableStreaming)
    {
        try
        {
            var options = Microsoft.Extensions.Options.Options.Create(
                new TelemetryGuard.Analytics.Kusto.KustoAnalyticsOptions
                {
                    ConnectionString = connectionString,
                    Database = database
                });
            var executor = new TelemetryGuard.Analytics.Kusto.KustoQueryExecutor(
                options, Microsoft.Extensions.Logging.Abstractions.NullLogger<TelemetryGuard.Analytics.Kusto.KustoQueryExecutor>.Instance);
            var applied = await new TelemetryGuard.Analytics.Kusto
                .KustoSchemaMigrator(executor).ApplyAsync(enableStreaming);
            foreach (var name in applied) Console.WriteLine($"Applied Kusto script {name}");
            Console.WriteLine("Kusto schema up to date.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
