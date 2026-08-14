using System.Reflection;
using TelemetryGuard.RiskEngine.Contracts;

namespace TelemetryGuard.Tests.Unit.Training;

/// <summary>RSK-08 acceptance: MlFeatureMapper's NaN/1/0 encoding and the frozen
/// 50-field v1 contract.</summary>
public sealed class MlFeatureMapperTests
{
    [Fact]
    public void DefaultVector_MapsAbsentSignalsToNaN()
    {
        var row = MlFeatureMapper.ToRow(new FraudFeatureVector());

        // T1 SDK signals absent (no beacon) -> NaN, never 0/false.
        Assert.True(float.IsNaN(row.honeypot_touched));
        Assert.True(float.IsNaN(row.click_before_render));
        Assert.True(float.IsNaN(row.pointer_untrusted));
        Assert.True(float.IsNaN(row.webdriver_flag));
        Assert.True(float.IsNaN(row.headless_browser));
        Assert.True(float.IsNaN(row.beacon_integrity_failed));
        Assert.True(float.IsNaN(row.ip_tor));
        Assert.True(float.IsNaN(row.tls_ua_mismatch));
        Assert.True(float.IsNaN(row.ip_datacenter_asn));
        Assert.True(float.IsNaN(row.ua_os_mismatch));
        Assert.True(float.IsNaN(row.mouse_path_linearity));
        Assert.True(float.IsNaN(row.std_inter_event_ms));
        Assert.True(float.IsNaN(row.click_id_invalid));

        // T2/T3 SDK signals absent -> NaN.
        Assert.True(float.IsNaN(row.emulator_or_vm));
        Assert.True(float.IsNaN(row.screen_res_anomalous));
        Assert.True(float.IsNaN(row.storage_age_zero_repeat));
        Assert.True(float.IsNaN(row.ip_proxy_or_vpn));
        Assert.True(float.IsNaN(row.ip_geo_target_mismatch));
        Assert.True(float.IsNaN(row.time_on_page_sec));
        Assert.True(float.IsNaN(row.form_fill_time_sec));
        Assert.True(float.IsNaN(row.first_interaction_delay_ms));
        Assert.True(float.IsNaN(row.input_modality_mismatch));
        Assert.True(float.IsNaN(row.mean_inter_event_ms));
        Assert.True(float.IsNaN(row.cookies_disabled));
        Assert.True(float.IsNaN(row.canvas_fp_blocked));
        Assert.True(float.IsNaN(row.timezone_ip_mismatch));
        Assert.True(float.IsNaN(row.language_geo_mismatch));
        Assert.True(float.IsNaN(row.paste_in_identity_fields));
        Assert.True(float.IsNaN(row.input_event_count));
        Assert.True(float.IsNaN(row.scroll_events));
        Assert.True(float.IsNaN(row.pages_viewed));
        Assert.True(float.IsNaN(row.is_mobile));
        Assert.True(float.IsNaN(row.form_submitted));
        Assert.True(float.IsNaN(row.autofill_detected));
    }

    [Fact]
    public void DefaultVector_MapsKnownSignalsToZeroOrTheirDefault_NeverNaN()
    {
        var row = MlFeatureMapper.ToRow(new FraudFeatureVector());

        // Plain bool/numeric fields are ALWAYS known — never NaN.
        Assert.Equal(0f, row.has_js_beacon);
        Assert.Equal(0f, row.ip_clicks_last_min);
        Assert.Equal(0f, row.device_sessions_last_hour);
        Assert.Equal(0f, row.ip_distinct_uas_last_hour);
        Assert.Equal(0f, row.device_ids_this_ip_hour);
        Assert.Equal(0f, row.ip_reputation_bad); // legitimately 0 when cold, per FraudFeatureVector contract
        Assert.Equal(0f, row.referrer_missing);
        Assert.Equal(0f, row.is_private_relay);
        Assert.Equal(0f, row.is_paid_click);
        // AsnType.Unknown -> every one-hot column is 0.
        Assert.Equal(0f, row.asn_residential);
        Assert.Equal(0f, row.asn_mobile);
        Assert.Equal(0f, row.asn_business);
        Assert.Equal(0f, row.asn_datacenter);
        Assert.Equal(0f, row.asn_education);
        Assert.Equal(0f, row.asn_government);
        Assert.Equal(0f, row.asn_cdn);
    }

    [Theory]
    [InlineData(false, 1f)] // fires on the FALSE direction (tampered payload)
    [InlineData(true, 0f)]
    public void BeaconIntegrityOk_EncodesFiringDirection(bool value, float expected)
    {
        var row = MlFeatureMapper.ToRow(new FraudFeatureVector { BeaconIntegrityOk = value });

        Assert.Equal(expected, row.beacon_integrity_failed);
    }

    [Fact]
    public void BeaconIntegrityOk_Null_MapsToNaN_NotZeroOrOne()
    {
        var row = MlFeatureMapper.ToRow(new FraudFeatureVector { BeaconIntegrityOk = null });

        Assert.True(float.IsNaN(row.beacon_integrity_failed));
    }

    [Theory]
    [InlineData(AsnType.Residential)]
    [InlineData(AsnType.Mobile)]
    [InlineData(AsnType.Business)]
    [InlineData(AsnType.Datacenter)]
    [InlineData(AsnType.Education)]
    [InlineData(AsnType.Government)]
    [InlineData(AsnType.Cdn)]
    public void AsnType_OneHotEncodes_ExactlyOneColumn(AsnType asn)
    {
        var row = MlFeatureMapper.ToRow(new FraudFeatureVector { AsnType = asn });

        var onehot = new (string Name, float Value)[]
        {
            ("asn_residential", row.asn_residential),
            ("asn_mobile", row.asn_mobile),
            ("asn_business", row.asn_business),
            ("asn_datacenter", row.asn_datacenter),
            ("asn_education", row.asn_education),
            ("asn_government", row.asn_government),
            ("asn_cdn", row.asn_cdn),
        };

        var hot = onehot.Where(x => x.Value == 1f).ToList();
        var expectedField = "asn_" + asn.ToString().ToLowerInvariant();
        var single = Assert.Single(hot);
        Assert.Equal(expectedField, single.Name);
        Assert.All(onehot.Where(x => x.Name != expectedField), x => Assert.Equal(0f, x.Value));
    }

    [Fact]
    public void AsnType_Mobile_MapsOnlyAsnMobile()
    {
        var row = MlFeatureMapper.ToRow(new FraudFeatureVector { AsnType = AsnType.Mobile });

        Assert.Equal(1f, row.asn_mobile);
        Assert.Equal(0f, row.asn_residential);
        Assert.Equal(0f, row.asn_business);
        Assert.Equal(0f, row.asn_datacenter);
        Assert.Equal(0f, row.asn_education);
        Assert.Equal(0f, row.asn_government);
        Assert.Equal(0f, row.asn_cdn);
    }

    [Fact]
    public void ChallengeOutcome_IsAbsentFromTheRowsFeatureFields()
    {
        var fields = typeof(MlFeatureRow)
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Select(f => f.Name)
            .ToList();

        Assert.DoesNotContain(fields, n => n.Contains("challenge", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("ChallengeOutcome", fields);
    }

    [Fact]
    public void FeatureFieldCount_Is50_ExcludingLabelAndWeight()
    {
        var fields = typeof(MlFeatureRow)
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => f.Name is not (nameof(MlFeatureRow.Label) or nameof(MlFeatureRow.Weight)))
            .ToList();

        Assert.Equal(50, fields.Count);
        Assert.All(fields, f => Assert.Equal(typeof(float), f.FieldType));
    }

    [Fact]
    public void FeatureFieldNames_MatchTheReflectedFieldSet_InOrder()
    {
        var reflected = typeof(MlFeatureRow)
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => f.Name is not (nameof(MlFeatureRow.Label) or nameof(MlFeatureRow.Weight)))
            .Select(f => f.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(50, MlFeatureRow.FeatureFieldNames.Length);
        Assert.Equal(reflected, MlFeatureRow.FeatureFieldNames.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void KnownFraudSignals_AreDetectedAsPositiveEvidence()
    {
        var v = new FraudFeatureVector
        {
            HasJsBeacon = true,
            WebdriverFlag = true,
            HeadlessBrowser = true,
            IpClicksLastMin = 50,
        };

        var row = MlFeatureMapper.ToRow(v);

        Assert.Equal(1f, row.webdriver_flag);
        Assert.Equal(1f, row.headless_browser);
        Assert.Equal(50f, row.ip_clicks_last_min);
    }
}
