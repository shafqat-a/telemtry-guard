using System.Reflection;
using TelemetryGuard.Analytics.ClickHouse;

namespace TelemetryGuard.Tests.Unit.Analytics;

public sealed class SchemaMigratorSplitStatementsTests
{
    [Fact]
    public void Embedded0001Script_SplitsIntoExactlyTwoStatements()
    {
        var sql = ReadEmbedded0001Script();

        var statements = SchemaMigrator.SplitStatements(sql).ToList();

        Assert.Equal(2, statements.Count);
        Assert.Contains("CREATE TABLE IF NOT EXISTS tg_events", statements[0], StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS tg_labels", statements[1], StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedSchemaScripts_ApplyInOrdinalNameOrder_0001ThenRsk08s0002()
    {
        var asm = typeof(SchemaMigrator).Assembly;
        var scripts = asm.GetManifestResourceNames()
            .Where(n => n.Contains(".schema.", StringComparison.Ordinal)
                     && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(2, scripts.Count);
        Assert.EndsWith(".schema.0001_events.sql", scripts[0], StringComparison.Ordinal);
        Assert.EndsWith(".schema.0002_training_and_shadow.sql", scripts[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Embedded0002Script_RSK08_OnlyAltersExistingTables_NeverRecreates()
    {
        var sql = ReadEmbeddedScript("0002_training_and_shadow.sql");

        var statements = SchemaMigrator.SplitStatements(sql).ToList();

        Assert.Equal(4, statements.Count);
        // Leading comment lines with no semicolon of their own stay bundled with the
        // statement that follows them (same SplitStatements behavior as 0001's
        // header comment + CREATE TABLE) — assert Contains, not StartsWith.
        Assert.All(statements, s => Assert.Contains("ALTER TABLE", s, StringComparison.Ordinal));
        Assert.All(statements, s => Assert.Contains("ADD COLUMN IF NOT EXISTS", s, StringComparison.Ordinal));
        Assert.DoesNotContain(statements, s => s.Contains("CREATE TABLE", StringComparison.Ordinal));
    }

    [Fact]
    public void CommentOnlyFragments_AreDropped()
    {
        const string sql =
            "-- a comment-only fragment\n-- another comment line\n;\n" +
            "CREATE TABLE t (a Int32) ENGINE = Memory;\n" +
            "-- trailing comment only\n";

        var statements = SchemaMigrator.SplitStatements(sql).ToList();

        var only = Assert.Single(statements);
        Assert.StartsWith("CREATE TABLE t", only, StringComparison.Ordinal);
    }

    [Fact]
    public void CrlfInput_SplitsSameAsLfInput()
    {
        var lf = ReadEmbedded0001Script().Replace("\r\n", "\n");
        var crlf = lf.Replace("\n", "\r\n");

        var fromLf = SchemaMigrator.SplitStatements(lf).ToList();
        var fromCrlf = SchemaMigrator.SplitStatements(crlf).ToList();

        Assert.Equal(2, fromCrlf.Count);
        // SplitStatements normalizes CRLF to LF before splitting, so results match exactly.
        Assert.Equal(fromLf, fromCrlf);
    }

    [Fact]
    public void WhitespaceOnlyFragments_AreDropped()
    {
        var statements = SchemaMigrator.SplitStatements("  \n;\n\n;\nSELECT 1;\n").ToList();

        var only = Assert.Single(statements);
        Assert.Equal("SELECT 1", only);
    }

    private static string ReadEmbedded0001Script() => ReadEmbeddedScript("0001_events.sql");

    private static string ReadEmbeddedScript(string fileName)
    {
        var asm = typeof(SchemaMigrator).Assembly;
        var resource = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith($".schema.{fileName}", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded resource {resource}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
