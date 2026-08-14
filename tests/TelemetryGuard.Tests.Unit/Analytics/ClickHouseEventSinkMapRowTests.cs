using System.Net;
using System.Net.Sockets;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Analytics.ClickHouse;
using TelemetryGuard.Core.Tenancy;

namespace TelemetryGuard.Tests.Unit.Analytics;

/// <summary>
/// ANA-03: MapRow must mirror the tg_events column order (68 base columns + 3 RSK-08
/// training/listen-only columns appended by 0002_training_and_shadow.sql) and
/// preserve null semantics — NaN stays NaN, bool? maps to (byte)1/(byte)0/null.
/// </summary>
public sealed class ClickHouseEventSinkMapRowTests
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
        IsDatacenter = true,   // -> (byte)1
        IsProxy = false,       // -> (byte)0
        // IsVpn stays null    // -> null
        RetentionDays = 90,
        TimestampUtc = Stamp
    };

    [Fact]
    public void ColumnNames_HasAll71Columns_InContractOrder()
    {
        Assert.Equal(71, ClickHouseEventSink.ColumnNames.Length);
        Assert.Equal(0, Array.IndexOf(ClickHouseEventSink.ColumnNames, "tenant_id"));
        Assert.Equal(66, Array.IndexOf(ClickHouseEventSink.ColumnNames, "retention_days"));
        Assert.Equal(67, Array.IndexOf(ClickHouseEventSink.ColumnNames, "timestamp"));
        // RSK-08: appended at the end (bulk-copy ColumnNames is an explicit
        // name/order whitelist, independent of the table's physical column order).
        Assert.Equal(68, Array.IndexOf(ClickHouseEventSink.ColumnNames, "features"));
        Assert.Equal(69, Array.IndexOf(ClickHouseEventSink.ColumnNames, "shadow_score"));
        Assert.Equal(70, Array.IndexOf(ClickHouseEventSink.ColumnNames, "shadow_scorer_version"));
    }

    [Fact]
    public void MapRow_Produces71Values_MatchingColumnOrder()
    {
        var row = ClickHouseEventSink.MapRow(SampleEvent());

        Assert.Equal(71, row.Length);
        Assert.Equal(TenantGuid, row[0]);                 // tenant_id
        Assert.Equal("tracker", row[3]);                  // kind wire string
        Assert.Equal((ushort)90, row[66]);                // retention_days
        Assert.Equal(Stamp, row[67]);                     // timestamp
        Assert.Equal(DateTimeKind.Utc, ((DateTime)row[67]!).Kind);
        Assert.Equal("", row[68]);                        // features: default "" when unset
        Assert.Null(row[69]);                             // shadow_score: null when no shadow scorer ran
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

        var row = ClickHouseEventSink.MapRow(evt);

        Assert.Equal("""{"HasJsBeacon":true}""", row[68]);
        Assert.Equal((short)42, row[69]);
        Assert.Equal("lgbm-20260915-a1b2c3d4", row[70]);
    }

    [Fact]
    public void MapRow_NullableBooleans_MapToByteOrNull()
    {
        var row = ClickHouseEventSink.MapRow(SampleEvent());

        var isDatacenter = Array.IndexOf(ClickHouseEventSink.ColumnNames, "is_datacenter");
        var isProxy = Array.IndexOf(ClickHouseEventSink.ColumnNames, "is_proxy");
        var isVpn = Array.IndexOf(ClickHouseEventSink.ColumnNames, "is_vpn");
        var hasJsBeacon = Array.IndexOf(ClickHouseEventSink.ColumnNames, "has_js_beacon");

        Assert.Equal((byte)1, row[isDatacenter]);
        Assert.Equal((byte)0, row[isProxy]);
        Assert.Null(row[isVpn]);
        Assert.Equal((byte)1, row[hasJsBeacon]);
    }

    [Fact]
    public void MapRow_AbsentSdkNumerics_StayNaN_NeverZero()
    {
        var row = ClickHouseEventSink.MapRow(SampleEvent());

        var storageAge = Array.IndexOf(ClickHouseEventSink.ColumnNames, "storage_age_sec");
        var linearity = Array.IndexOf(ClickHouseEventSink.ColumnNames, "mouse_path_linearity");

        Assert.True(float.IsNaN((float)row[storageAge]!));
        Assert.True(float.IsNaN((float)row[linearity]!));
    }

    [Fact]
    public void ToIpV6_MapsIPv4_ToIPv6MappedAddress()
    {
        var mapped = ClickHouseEventSink.ToIpV6("1.2.3.4");

        Assert.Equal(AddressFamily.InterNetworkV6, mapped.AddressFamily);
        Assert.Equal(IPAddress.Parse("::ffff:1.2.3.4"), mapped);
    }

    [Fact]
    public void ToIpV6_PassesThroughIPv6_AndFallsBackOnGarbage()
    {
        Assert.Equal(IPAddress.Parse("2001:db8::1"), ClickHouseEventSink.ToIpV6("2001:db8::1"));
        Assert.Equal(IPAddress.IPv6None, ClickHouseEventSink.ToIpV6("not-an-ip"));
    }

    [Fact]
    public void LabelSink_MapRow_MatchesTgLabelsColumns()
    {
        Assert.Equal(
            new[] { "tenant_id", "session_id", "label", "label_source", "created_at" },
            ClickHouseLabelSink.ColumnNames);

        var created = new DateTime(2026, 8, 12, 11, 0, 0, DateTimeKind.Utc);
        var row = ClickHouseLabelSink.MapRow(new LabelEvent(
            new TenantId(TenantGuid), "sess-2", LabelValues.Fraud, LabelSources.T1Rule, created));

        Assert.Equal(5, row.Length);
        Assert.Equal(TenantGuid, row[0]);
        Assert.Equal("sess-2", row[1]);
        Assert.Equal("fraud", row[2]);
        Assert.Equal("t1_rule", row[3]);
        Assert.Equal(created, row[4]);
    }
}
