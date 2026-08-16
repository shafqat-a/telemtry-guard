using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Analytics.Kusto;
using TelemetryGuard.Core.Tenancy;

namespace TelemetryGuard.Tests.Unit.Analytics;

/// <summary>
/// P2-05 mirror of ClickHouseEventSinkMapRowTests: MapRow must mirror
/// KustoEventSink.ColumnNames order and preserve §7 null semantics —
/// Kusto-native nullability this time (bool? stays bool?, no byte encoding).
/// </summary>
public sealed class KustoEventSinkMapRowTests
{
    private static readonly Guid TenantGuid = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");
    private static readonly DateTime Stamp = new(2026, 8, 12, 10, 30, 0, DateTimeKind.Utc);

    private static ClickEvent SampleEvent() => new()
    {
        TenantId = new TenantId(TenantGuid),
        SiteKey = "sk-1",
        SessionId = "sess-1",
        Kind = EventKind.Tracker,
        Ip = "1.2.3.4",
        HasJsBeacon = true,
        IsDatacenter = true,
        IsProxy = false,
        // IsVpn stays null
        RetentionDays = 90,
        TimestampUtc = Stamp
    };

    [Fact]
    public void ColumnNames_HasAll87Columns_InContractOrder()
    {
        Assert.Equal(87, KustoEventSink.ColumnNames.Length);
        Assert.Equal(0, Array.IndexOf(KustoEventSink.ColumnNames, "tenant_id"));
        Assert.Equal(66, Array.IndexOf(KustoEventSink.ColumnNames, "retention_days"));
        Assert.Equal(67, Array.IndexOf(KustoEventSink.ColumnNames, "timestamp"));
        Assert.Equal(68, Array.IndexOf(KustoEventSink.ColumnNames, "features"));
        Assert.Equal(69, Array.IndexOf(KustoEventSink.ColumnNames, "shadow_score"));
        Assert.Equal(70, Array.IndexOf(KustoEventSink.ColumnNames, "shadow_scorer_version"));
        // Same 87 names, same order, as ClickHouseEventSink.ColumnNames (ANA-03) —
        // duplicated on purpose (D7): no cross-provider reference from this project.
        Assert.DoesNotContain(KustoEventSink.ColumnNames, n => n.Contains("ClickHouse"));
    }

    [Fact]
    public void MapRow_Produces87Values_MatchingColumnOrder()
    {
        var row = KustoEventSink.MapRow(SampleEvent());

        Assert.Equal(87, row.Length);
        Assert.Equal(TenantGuid, row[0]);                 // tenant_id
        Assert.Equal("tracker", row[3]);                  // kind wire string
        Assert.Equal(90, row[66]);                        // retention_days: boxed int (task step 7)
        Assert.Equal(Stamp, row[67]);                      // timestamp
        Assert.Equal(DateTimeKind.Utc, ((DateTime)row[67]!).Kind);
        Assert.Equal("", row[68]);                        // features: default "" when unset
        Assert.Null(row[69]);                              // shadow_score: null when no shadow scorer ran
        Assert.Equal("", row[70]);                         // shadow_scorer_version: default ""
    }

    [Fact]
    public void MapRow_RSK08Fields_FlowVerbatim_WhenPopulated()
    {
        var evt = SampleEvent() with
        {
            Features = """{"HasJsBeacon":true}""",
            ShadowScore = 42,
            ShadowScorerVersion = "lgbm-20260915-a1b2c3d4",
        };

        var row = KustoEventSink.MapRow(evt);

        Assert.Equal("""{"HasJsBeacon":true}""", row[68]);
        Assert.Equal(42, row[69]);
        Assert.Equal("lgbm-20260915-a1b2c3d4", row[70]);
    }

    [Fact]
    public void MapRow_NullableBooleans_StayBoolOrNull_NoByteEncoding()
    {
        var row = KustoEventSink.MapRow(SampleEvent());

        var isDatacenter = Array.IndexOf(KustoEventSink.ColumnNames, "is_datacenter");
        var isProxy = Array.IndexOf(KustoEventSink.ColumnNames, "is_proxy");
        var isVpn = Array.IndexOf(KustoEventSink.ColumnNames, "is_vpn");
        var hasJsBeacon = Array.IndexOf(KustoEventSink.ColumnNames, "has_js_beacon");

        // Kusto scalars are natively nullable — no 0/1 byte encoding, unlike ClickHouse.
        Assert.IsType<bool>(row[isDatacenter]);
        Assert.Equal(true, row[isDatacenter]);
        Assert.IsType<bool>(row[isProxy]);
        Assert.Equal(false, row[isProxy]);
        Assert.Null(row[isVpn]);
        Assert.IsType<bool>(row[hasJsBeacon]);
        Assert.Equal(true, row[hasJsBeacon]);
    }

    [Fact]
    public void MapRow_ScoreBandAction_AreNull_OnNonVerdictRow()
    {
        var row = KustoEventSink.MapRow(SampleEvent()); // Kind = Tracker

        var score = Array.IndexOf(KustoEventSink.ColumnNames, "score");
        var band = Array.IndexOf(KustoEventSink.ColumnNames, "band");
        var action = Array.IndexOf(KustoEventSink.ColumnNames, "action");

        Assert.Null(row[score]);
        Assert.Null(row[band]);
        Assert.Null(row[action]);
    }

    [Fact]
    public void MapRow_AbsentSdkNumerics_StayNaN_NeverZero()
    {
        var row = KustoEventSink.MapRow(SampleEvent());

        var storageAge = Array.IndexOf(KustoEventSink.ColumnNames, "storage_age_sec");
        var linearity = Array.IndexOf(KustoEventSink.ColumnNames, "mouse_path_linearity");

        Assert.True(float.IsNaN((float)row[storageAge]!));
        Assert.True(float.IsNaN((float)row[linearity]!));
    }

    [Fact]
    public void NormalizeIp_MapsIPv4_ToIPv6MappedText()
    {
        Assert.Equal("::ffff:10.1.2.3", KustoValueMapping.NormalizeIp("10.1.2.3"));
    }

    [Fact]
    public void NormalizeIp_PassesThroughIPv6_AndFallsBackOnGarbage()
    {
        Assert.Equal("2001:db8::1", KustoValueMapping.NormalizeIp("2001:db8::1"));
        Assert.Equal(System.Net.IPAddress.IPv6None.ToString(), KustoValueMapping.NormalizeIp("not-an-ip"));
    }

    [Fact]
    public void LabelSink_MapRow_MatchesTgLabelsColumns()
    {
        Assert.Equal(
            new[] { "tenant_id", "session_id", "label", "label_source", "created_at", "weight" },
            KustoLabelSink.ColumnNames);

        var created = new DateTime(2026, 8, 12, 11, 0, 0, DateTimeKind.Utc);
        var row = KustoLabelSink.MapRow(new LabelEvent(
            new TenantId(TenantGuid), "sess-2", LabelValues.Fraud, LabelSources.T1Rule, created));

        Assert.Equal(6, row.Length);
        Assert.Equal(TenantGuid, row[0]);
        Assert.Equal("sess-2", row[1]);
        Assert.Equal("fraud", row[2]);
        Assert.Equal("t1_rule", row[3]);
        Assert.Equal(created, row[4]);
        Assert.Equal(1.0f, row[5]); // Kusto has no column defaults — written explicitly
    }
}
