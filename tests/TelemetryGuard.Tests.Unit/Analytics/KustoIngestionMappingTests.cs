using System.Text.Json;
using TelemetryGuard.Analytics.Kusto;

namespace TelemetryGuard.Tests.Unit.Analytics;

/// <summary>
/// The drift guard (P2-05 step 11c — the Kusto analogue of ANA-03's "MUST match
/// schema column order" comment). Reads the embedded schema/0001_events.kql and
/// proves, from the TEXT of the committed script, that three independently
/// maintained artifacts can never drift apart:
///   1. the `.create-merge table tg_events (...)` column DECLARATION list,
///   2. the `tg_events_json_mapping` ingestion mapping's Column/Path entries,
///   3. KustoEventSink.ColumnNames (the array that drives both MapRow and the
///      multijson payload keys) — and the same triple for tg_labels.
/// </summary>
public sealed class KustoIngestionMappingTests
{
    private static readonly string Kql = ReadEmbeddedSchema();

    [Fact]
    public void EventsMapping_ColumnsMatch_KustoEventSinkColumnNames_InOrder()
    {
        var mapping = ExtractMapping(Kql, "tg_events_json_mapping");

        Assert.Equal(KustoEventSink.ColumnNames.Length, mapping.Count);
        for (var i = 0; i < KustoEventSink.ColumnNames.Length; i++)
        {
            Assert.Equal(KustoEventSink.ColumnNames[i], mapping[i].Column);
            Assert.Equal($"$.{KustoEventSink.ColumnNames[i]}", mapping[i].Path);
        }
    }

    [Fact]
    public void LabelsMapping_ColumnsMatch_KustoLabelSinkColumnNames_InOrder()
    {
        var mapping = ExtractMapping(Kql, "tg_labels_json_mapping");

        Assert.Equal(KustoLabelSink.ColumnNames.Length, mapping.Count);
        for (var i = 0; i < KustoLabelSink.ColumnNames.Length; i++)
        {
            Assert.Equal(KustoLabelSink.ColumnNames[i], mapping[i].Column);
            Assert.Equal($"$.{KustoLabelSink.ColumnNames[i]}", mapping[i].Path);
        }
    }

    [Fact]
    public void EventsTableDeclaration_ColumnsMatch_KustoEventSinkColumnNames_InOrder()
    {
        var declared = ExtractTableColumns(Kql, "tg_events");
        Assert.Equal(KustoEventSink.ColumnNames, declared);
    }

    [Fact]
    public void LabelsTableDeclaration_ColumnsMatch_KustoLabelSinkColumnNames_InOrder()
    {
        var declared = ExtractTableColumns(Kql, "tg_labels");
        Assert.Equal(KustoLabelSink.ColumnNames, declared);
    }

    // ------------------------------------------------------------- extraction

    private static string ReadEmbeddedSchema()
    {
        var asm = typeof(KustoEventSink).Assembly;
        var resource = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(".schema.0001_events.kql", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded resource {resource}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static List<(string Column, string Path)> ExtractMapping(string kql, string mappingName)
    {
        var lines = kql.Replace("\r\n", "\n").Split('\n');
        var marker = $"mapping \"{mappingName}\"";
        var startIdx = Array.FindIndex(lines, l => l.Contains(marker, StringComparison.Ordinal));
        Assert.True(startIdx >= 0, $"Could not find '{marker}' in the embedded schema.");

        var fragments = new List<string>();
        for (var i = startIdx + 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) break;
            if (!line.StartsWith('\'')) break;
            // strip exactly one leading and one trailing single-quote
            Assert.True(line.Length >= 2 && line.EndsWith('\''),
                $"Expected a single-quoted KQL string literal fragment, got: {line}");
            fragments.Add(line[1..^1]);
        }
        Assert.NotEmpty(fragments);

        var json = string.Concat(fragments);
        using var doc = JsonDocument.Parse(json);
        var result = new List<(string, string)>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var column = el.GetProperty("Column").GetString()!;
            var path = el.GetProperty("Properties").GetProperty("Path").GetString()!;
            result.Add((column, path));
        }
        return result;
    }

    /// <summary>Parses the plain column-name list out of `.create-merge table
    /// &lt;name&gt; ( ... )`, stripping bracket-quoting (KQL reserved words like
    /// `kind` are declared as <c>['kind']: type</c> — see KustoAnalyticsQueries'
    /// namespace-collision/reserved-word note) so the comparison is name-for-name
    /// against ColumnNames, independent of that quoting.</summary>
    private static List<string> ExtractTableColumns(string kql, string tableName)
    {
        var marker = $".create-merge table {tableName} (";
        var start = kql.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{marker}' in the embedded schema.");
        var bodyStart = start + marker.Length;
        var end = kql.IndexOf("\n)", bodyStart, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Could not find the closing ')' for table {tableName}.");

        var body = kql[bodyStart..end];
        var columns = new List<string>();
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd(',');
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            var colonIdx = line.IndexOf(':');
            Assert.True(colonIdx > 0, $"Could not parse column declaration: '{line}'");
            var name = line[..colonIdx].Trim();
            if (name.StartsWith("['", StringComparison.Ordinal) && name.EndsWith("']", StringComparison.Ordinal))
                name = name[2..^2];
            columns.Add(name);
        }
        return columns;
    }
}
