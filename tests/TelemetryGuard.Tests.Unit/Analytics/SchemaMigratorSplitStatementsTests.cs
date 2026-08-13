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
    public void Embedded0001Script_IsTheOnlyEmbeddedSchemaScript()
    {
        var asm = typeof(SchemaMigrator).Assembly;
        var scripts = asm.GetManifestResourceNames()
            .Where(n => n.Contains(".schema.", StringComparison.Ordinal)
                     && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var name = Assert.Single(scripts);
        Assert.EndsWith(".schema.0001_events.sql", name, StringComparison.Ordinal);
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

    private static string ReadEmbedded0001Script()
    {
        var asm = typeof(SchemaMigrator).Assembly;
        var resource = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(".schema.0001_events.sql", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded resource {resource}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
