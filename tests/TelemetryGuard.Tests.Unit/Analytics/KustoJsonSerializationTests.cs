using System.Text.Json;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Analytics.Kusto;
using TelemetryGuard.Core.Tenancy;

namespace TelemetryGuard.Tests.Unit.Analytics;

/// <summary>
/// Round-trips KustoEventSink/KustoLabelSink's WriteBatchJson output through
/// JsonDocument — the multijson payload actually handed to
/// IKustoIngestTransport.IngestAsync, with no container involved.
/// </summary>
public sealed class KustoJsonSerializationTests
{
    private static readonly Guid TenantGuid = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");
    private static readonly DateTime Stamp = new(2026, 8, 12, 10, 30, 0, DateTimeKind.Utc);

    private static ClickEvent SampleEvent(EventKind kind = EventKind.Pixel, int? score = null) => new()
    {
        TenantId = new TenantId(TenantGuid),
        SiteKey = "sk-1",
        SessionId = "sess-1",
        Kind = kind,
        Ip = "1.2.3.4",
        HasJsBeacon = false,
        Score = score,
        HeaderNames = new[] { "user-agent", "accept" },
        RuleHits = new[] { "T1_HONEYPOT" },
        RetentionDays = 90,
        TimestampUtc = Stamp
    };

    private static JsonDocument WriteEvents(params ClickEvent[] events)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
            KustoEventSink.WriteBatchJson(w, events);
        buffer.Position = 0;
        return JsonDocument.Parse(buffer);
    }

    [Fact]
    public void WriteBatchJson_ProducesJsonArray_OneObjectPerEvent()
    {
        using var doc = WriteEvents(SampleEvent(), SampleEvent());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(2, doc.RootElement.GetArrayLength());
        Assert.Equal(JsonValueKind.Object, doc.RootElement[0].ValueKind);
    }

    [Fact]
    public void WriteBatchJson_PropertyOrder_MatchesColumnNames()
    {
        using var doc = WriteEvents(SampleEvent());
        var props = doc.RootElement[0].EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(KustoEventSink.ColumnNames, props);
    }

    [Fact]
    public void WriteBatchJson_AbsentScore_IsJsonNull_NeverZero()
    {
        using var doc = WriteEvents(SampleEvent(kind: EventKind.Pixel, score: null));
        var score = doc.RootElement[0].GetProperty("score");
        Assert.Equal(JsonValueKind.Null, score.ValueKind);
    }

    [Fact]
    public void WriteBatchJson_PresentScore_IsJsonNumber()
    {
        using var doc = WriteEvents(SampleEvent(kind: EventKind.Verdict, score: 42));
        var score = doc.RootElement[0].GetProperty("score");
        Assert.Equal(JsonValueKind.Number, score.ValueKind);
        Assert.Equal(42, score.GetInt32());
    }

    [Fact]
    public void WriteBatchJson_ArrayColumns_AreJsonArrays()
    {
        using var doc = WriteEvents(SampleEvent());
        var headerNames = doc.RootElement[0].GetProperty("header_names");
        var ruleHits = doc.RootElement[0].GetProperty("rule_hits");

        Assert.Equal(JsonValueKind.Array, headerNames.ValueKind);
        Assert.Equal(new[] { "user-agent", "accept" }, headerNames.EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(JsonValueKind.Array, ruleHits.ValueKind);
        Assert.Equal(new[] { "T1_HONEYPOT" }, ruleHits.EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void WriteBatchJson_Timestamp_IsRoundTrippableIso8601_WithZ()
    {
        using var doc = WriteEvents(SampleEvent());
        var raw = doc.RootElement[0].GetProperty("timestamp").GetString()!;

        Assert.EndsWith("Z", raw, StringComparison.Ordinal);
        var parsed = DateTime.Parse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
        Assert.Equal(Stamp, parsed);
    }

    [Fact]
    public void WriteBatchJson_NonFiniteFloat_EncodesAsJsonStringNaN()
    {
        // Q2 PRIMARY (step 0): the emulator round-trips a JSON string "NaN" into a
        // Kusto `real` as double.NaN — verified against the running emulator.
        // Q2-FALLBACK (not used here): null-encoding, i.e. WriteNull for non-finite
        // floats, with KustoValueMapping.RealToFloat mapping null -> NaN on read.
        using var doc = WriteEvents(SampleEvent()); // StorageAgeSec defaults to NaN
        var storageAge = doc.RootElement[0].GetProperty("storage_age_sec");

        Assert.Equal(JsonValueKind.String, storageAge.ValueKind);
        Assert.Equal("NaN", storageAge.GetString());
    }

    [Fact]
    public void WriteBatchJson_FiniteFloat_EncodesAsJsonNumber()
    {
        var evt = SampleEvent() with { StorageAgeSec = 12.5f };
        using var doc = WriteEvents(evt);
        var storageAge = doc.RootElement[0].GetProperty("storage_age_sec");

        Assert.Equal(JsonValueKind.Number, storageAge.ValueKind);
        Assert.Equal(12.5, storageAge.GetDouble(), 0.0001);
    }

    [Fact]
    public void LabelSink_WriteBatchJson_PropertyOrder_MatchesColumnNames()
    {
        var created = new DateTime(2026, 8, 12, 11, 0, 0, DateTimeKind.Utc);
        var label = new LabelEvent(new TenantId(TenantGuid), "sess-2", LabelValues.Fraud, LabelSources.T1Rule, created);

        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
            KustoLabelSink.WriteBatchJson(w, new[] { label });
        buffer.Position = 0;
        using var doc = JsonDocument.Parse(buffer);

        var props = doc.RootElement[0].EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(KustoLabelSink.ColumnNames, props);
        Assert.Equal(1.0, doc.RootElement[0].GetProperty("weight").GetDouble());
    }
}
