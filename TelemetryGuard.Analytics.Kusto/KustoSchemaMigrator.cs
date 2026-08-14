using System.Reflection;
using Kusto.Data.Common;

namespace TelemetryGuard.Analytics.Kusto;

/// <summary>
/// Applies embedded schema/*.kql scripts in ordinal filename order, once each,
/// journaled in tg_schema_migrations. Kusto analog of ANA-02's SchemaMigrator
/// (D10: versioned scripts per store). Invoked by the MigrationRunner console
/// (DAT-01) via its --kusto switch.
/// </summary>
public sealed class KustoSchemaMigrator(IKustoQueryExecutor executor)
{
    public async Task<IReadOnlyList<string>> ApplyAsync(
        bool enableStreamingIngestion = false, CancellationToken ct = default)
    {
        await executor.ExecuteControlCommandAsync(
            ".create-merge table tg_schema_migrations (script_name: string, applied_at: datetime)", ct)
            .ConfigureAwait(false);

        var applied = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = await executor.ExecuteQueryAsync(
            "tg_schema_migrations | distinct script_name",
            new ClientRequestProperties(), ct).ConfigureAwait(false))
        {
            while (reader.Read())
                applied.Add(reader.GetString(0));
        }

        var asm = Assembly.GetExecutingAssembly();
        const string marker = ".schema.";
        var resources = asm.GetManifestResourceNames()
            .Where(n => n.Contains(marker, StringComparison.Ordinal)
                     && n.EndsWith(".kql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var newlyApplied = new List<string>();
        foreach (var resource in resources)
        {
            var scriptName = resource[(resource.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
            if (applied.Contains(scriptName)) continue;

            // Script names come only from embedded resources (never user input), but
            // this is still the boundary where a name is composed into KQL text below.
            if (scriptName.Contains('"') || scriptName.Contains('\\'))
                throw new InvalidOperationException($"Unsafe schema script name '{scriptName}'.");

            using var stream = asm.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Missing embedded resource {resource}");
            using var textReader = new StreamReader(stream);
            var kql = await textReader.ReadToEndAsync(ct).ConfigureAwait(false);

            foreach (var command in SplitCommands(kql))
                await executor.ExecuteControlCommandAsync(command, ct).ConfigureAwait(false);

            await executor.ExecuteControlCommandAsync(
                $"""
                .set-or-append tg_schema_migrations <|
                print script_name = "{scriptName}", applied_at = now()
                """, ct).ConfigureAwait(false);
            newlyApplied.Add(scriptName);
        }

        if (enableStreamingIngestion)
        {
            // Idempotent policy commands, NOT journaled, and the only commands allowed
            // to fail with a logged warning — a production cluster may have streaming
            // ingestion disabled at cluster level, and the emulator otherwise needs
            // this enabled for step-0's Q1 primary path.
            await TryEnableStreamingAsync("tg_events", ct).ConfigureAwait(false);
            await TryEnableStreamingAsync("tg_labels", ct).ConfigureAwait(false);
        }

        return newlyApplied;
    }

    private async Task TryEnableStreamingAsync(string table, CancellationToken ct)
    {
        try
        {
            await executor.ExecuteControlCommandAsync(
                $".alter table {table} policy streamingingestion enable", ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Deliberately swallowed: this is the one place in the migrator allowed to
            // fail without aborting the run (see the enableStreamingIngestion doc above).
        }
    }

    /// <summary>
    /// The KQL analogue of SchemaMigrator.SplitStatements. Deliberately dead simple:
    /// normalize CRLF -> LF; a new command starts at any line whose first character
    /// is '.' at column 0; all following lines (blank, or starting with whitespace,
    /// <c>'</c>, <c>{</c> or <c>[</c>) belong to that command until the next column-0
    /// '.' line. Lines before the first command, and any line starting "//", are
    /// dropped. Trailing whitespace is trimmed per command; empty commands skipped.
    /// </summary>
    internal static IEnumerable<string> SplitCommands(string kql)
    {
        var lines = kql.Replace("\r\n", "\n").Split('\n');
        var commands = new List<string>();
        List<string>? current = null;

        foreach (var line in lines)
        {
            if (line.Length > 0 && line[0] == '.')
            {
                if (current is not null) commands.Add(string.Join('\n', current).TrimEnd());
                current = new List<string> { line };
                continue;
            }

            if (line.StartsWith("//", StringComparison.Ordinal))
                continue; // comment-only line — dropped whether before or inside a command

            if (current is not null)
                current.Add(line);
            // lines before the first command are dropped
        }
        if (current is not null) commands.Add(string.Join('\n', current).TrimEnd());

        return commands.Where(c => c.Trim().Length > 0);
    }
}
