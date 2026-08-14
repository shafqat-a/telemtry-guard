using TelemetryGuard.Analytics.Kusto;

namespace TelemetryGuard.Tests.Unit.Analytics;

/// <summary>
/// P2-05 mirror of SchemaMigratorSplitStatementsTests: KustoSchemaMigrator's KQL
/// splitter is deliberately dead simple (a new command starts at any column-0
/// '.' line) so it is unit-testable and unambiguous.
/// </summary>
public sealed class KustoSchemaSplitCommandsTests
{
    [Fact]
    public void Embedded0001Script_SplitsIntoExactlySixCommands()
    {
        var kql = ReadEmbedded0001Script();

        var commands = KustoSchemaMigrator.SplitCommands(kql).ToList();

        Assert.Equal(6, commands.Count);
        Assert.StartsWith(".create-merge table tg_events (", commands[0], StringComparison.Ordinal);
        Assert.StartsWith(".create-merge table tg_labels (", commands[1], StringComparison.Ordinal);
        Assert.StartsWith(".alter-merge table tg_events policy retention", commands[2], StringComparison.Ordinal);
        Assert.StartsWith(".alter-merge table tg_labels policy retention", commands[3], StringComparison.Ordinal);
        Assert.StartsWith(".create-or-alter table tg_events ingestion json mapping", commands[4], StringComparison.Ordinal);
        Assert.StartsWith(".create-or-alter table tg_labels ingestion json mapping", commands[5], StringComparison.Ordinal);
    }

    [Fact]
    public void MultiLineMappingBlob_StaysOneCommand()
    {
        var kql = ReadEmbedded0001Script();
        var commands = KustoSchemaMigrator.SplitCommands(kql).ToList();

        // The tg_events mapping command spans many quoted lines (one per column) —
        // all of it must land in a single command string, not be split further.
        var mappingCommand = commands[4];
        Assert.Contains("\"tenant_id\"", mappingCommand, StringComparison.Ordinal);
        Assert.Contains("\"shadow_scorer_version\"", mappingCommand, StringComparison.Ordinal);
        Assert.True(mappingCommand.Split('\n').Length > 10,
            "the mapping command should retain its many quoted continuation lines");
    }

    [Fact]
    public void HeaderComment_BeforeFirstCommand_IsDropped()
    {
        var kql = ReadEmbedded0001Script();
        var commands = KustoSchemaMigrator.SplitCommands(kql).ToList();

        Assert.DoesNotContain(commands, c => c.Contains("P2-05 / 0001_events.kql", StringComparison.Ordinal));
    }

    [Fact]
    public void CommentOnlyLines_AreDroppedEverywhere()
    {
        const string kql =
            "// a leading comment\n" +
            ".create-merge table t (a: string)\n" +
            "// a comment INSIDE the command\n" +
            "    b: string\n";

        var commands = KustoSchemaMigrator.SplitCommands(kql).ToList();

        var only = Assert.Single(commands);
        Assert.DoesNotContain("//", only, StringComparison.Ordinal);
        Assert.Contains(".create-merge table t (a: string)", only, StringComparison.Ordinal);
        Assert.Contains("b: string", only, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoAdjacentCommands_SplitIntoTwo()
    {
        const string kql = ".show version\n.show cluster\n";

        var commands = KustoSchemaMigrator.SplitCommands(kql).ToList();

        Assert.Equal(2, commands.Count);
        Assert.Equal(".show version", commands[0]);
        Assert.Equal(".show cluster", commands[1]);
    }

    [Fact]
    public void TrailingWhitespace_IsTrimmedFromEachCommand()
    {
        const string kql = ".show version   \n   \n";

        var commands = KustoSchemaMigrator.SplitCommands(kql).ToList();

        var only = Assert.Single(commands);
        Assert.Equal(".show version", only);
    }

    [Fact]
    public void EmptyInput_YieldsNoCommands()
    {
        Assert.Empty(KustoSchemaMigrator.SplitCommands(""));
        Assert.Empty(KustoSchemaMigrator.SplitCommands("// only a comment\n"));
    }

    [Fact]
    public void CrlfInput_SplitsSameAsLfInput()
    {
        var lf = ReadEmbedded0001Script().Replace("\r\n", "\n");
        var crlf = lf.Replace("\n", "\r\n");

        var fromLf = KustoSchemaMigrator.SplitCommands(lf).ToList();
        var fromCrlf = KustoSchemaMigrator.SplitCommands(crlf).ToList();

        Assert.Equal(fromLf, fromCrlf);
    }

    private static string ReadEmbedded0001Script()
    {
        var asm = typeof(KustoSchemaMigrator).Assembly;
        var resource = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(".schema.0001_events.kql", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded resource {resource}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
