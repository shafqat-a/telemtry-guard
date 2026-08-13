using System.Reflection;
using ClickHouse.Client.ADO;
using ClickHouse.Client.Utility;

namespace TelemetryGuard.Analytics.ClickHouse;

/// <summary>
/// Applies embedded schema/*.sql scripts in ordinal filename order, once each,
/// journaled in tg_schema_migrations. ClickHouse analog of the DbUp runner (D10).
/// Invoked by the MigrationRunner console (DAT-01) via its --clickhouse switch.
/// </summary>
public sealed class SchemaMigrator(string connectionString)
{
    public async Task<IReadOnlyList<string>> ApplyAsync(CancellationToken ct = default)
    {
        await using var conn = new ClickHouseConnection(connectionString);
        await conn.OpenAsync(ct);

        await ExecAsync(conn,
            """
            CREATE TABLE IF NOT EXISTS tg_schema_migrations
            (
                script_name String,
                applied_at  DateTime('UTC') DEFAULT now()
            )
            ENGINE = MergeTree
            ORDER BY script_name
            """, ct);

        var applied = new HashSet<string>(StringComparer.Ordinal);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT script_name FROM tg_schema_migrations";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                applied.Add(reader.GetString(0));
        }

        var asm = Assembly.GetExecutingAssembly();
        const string marker = ".schema.";
        var resources = asm.GetManifestResourceNames()
            .Where(n => n.Contains(marker, StringComparison.Ordinal)
                     && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var newlyApplied = new List<string>();
        foreach (var resource in resources)
        {
            var scriptName = resource[(resource.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
            if (applied.Contains(scriptName)) continue;

            using var stream = asm.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Missing embedded resource {resource}");
            using var text = new StreamReader(stream);
            var sql = await text.ReadToEndAsync(ct);

            foreach (var statement in SplitStatements(sql))
                await ExecAsync(conn, statement, ct);

            await using var journal = conn.CreateCommand();
            journal.CommandText = "INSERT INTO tg_schema_migrations (script_name) VALUES ({name:String})";
            journal.AddParameter("name", scriptName);
            await journal.ExecuteNonQueryAsync(ct);
            newlyApplied.Add(scriptName);
        }
        return newlyApplied;
    }

    private static async Task ExecAsync(ClickHouseConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Splits on ';' at end of line. One statement per fragment; comment-only fragments skipped.</summary>
    internal static IEnumerable<string> SplitStatements(string sql)
    {
        foreach (var raw in sql.Replace("\r\n", "\n").Split(";\n"))
        {
            var stmt = raw.Trim().TrimEnd(';').Trim();
            if (stmt.Length == 0) continue;
            var commentOnly = stmt.Split('\n')
                .All(l => l.Trim().Length == 0 || l.TrimStart().StartsWith("--", StringComparison.Ordinal));
            if (commentOnly) continue;
            yield return stmt;
        }
    }
}
