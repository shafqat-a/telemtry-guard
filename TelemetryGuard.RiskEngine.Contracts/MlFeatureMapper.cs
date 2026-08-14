namespace TelemetryGuard.RiskEngine.Contracts;

/// <summary>
/// RSK-08: the ONLY place that converts a <see cref="FraudFeatureVector"/> into an
/// <see cref="MlFeatureRow"/>. Column order is feature_set_version 1; any change to
/// field names/order/count bumps <see cref="FraudFeatureVector.FeatureSetVersion"/>
/// AND requires retraining (the trainer's Concatenate call binds these names).
/// Pure, allocation-light, synchronous — safe to call inside the &lt; 50 ms scoring
/// budget (D3) from <c>MlNetScorer</c>, and reused unchanged by the offline trainer.
/// </summary>
public static class MlFeatureMapper
{
    public static MlFeatureRow ToRow(FraudFeatureVector v) => new()
    {
        // ---- T1 ----
        honeypot_touched = B(v.HoneypotTouched),
        click_before_render = B(v.ClickBeforeRender),
        pointer_untrusted = B(v.PointerUntrusted),
        webdriver_flag = B(v.WebdriverFlag),
        headless_browser = B(v.HeadlessBrowser),
        // Fires on the FALSE direction (tampered payload), mirroring RSK-05 rule 6 —
        // NOT a straight bool? passthrough: null (no beacon) still maps to NaN.
        beacon_integrity_failed = v.BeaconIntegrityOk == false ? 1f
            : v.BeaconIntegrityOk == null ? float.NaN : 0f,
        ip_tor = B(v.IpTor),
        tls_ua_mismatch = B(v.TlsUaMismatch),
        ip_datacenter_asn = B(v.IpDatacenterAsn),
        ip_clicks_last_min = v.IpClicksLastMin,
        ua_os_mismatch = B(v.UaOsMismatch),
        mouse_path_linearity = v.MousePathLinearity,
        std_inter_event_ms = v.StdInterEventMs,
        click_id_invalid = B(v.ClickIdInvalid),

        // ---- T2 ----
        has_js_beacon = v.HasJsBeacon ? 1f : 0f, // always known — never NaN
        emulator_or_vm = B(v.EmulatorOrVm),
        screen_res_anomalous = B(v.ScreenResAnomalous),
        storage_age_zero_repeat = v.StorageAgeZeroRepeat,
        ip_proxy_or_vpn = B(v.IpProxyOrVpn),
        ip_geo_target_mismatch = B(v.IpGeoTargetMismatch),
        time_on_page_sec = v.TimeOnPageSec,
        form_fill_time_sec = v.FormFillTimeSec,
        first_interaction_delay_ms = v.FirstInteractionDelayMs,
        input_modality_mismatch = B(v.InputModalityMismatch),
        mean_inter_event_ms = v.MeanInterEventMs,
        device_sessions_last_hour = v.DeviceSessionsLastHour,
        ip_distinct_uas_last_hour = v.IpDistinctUasLastHour,
        device_ids_this_ip_hour = v.DeviceIdsThisIpHour,

        // ---- T3 ----
        cookies_disabled = B(v.CookiesDisabled),
        canvas_fp_blocked = B(v.CanvasFpBlocked),
        timezone_ip_mismatch = B(v.TimezoneIpMismatch),
        language_geo_mismatch = B(v.LanguageGeoMismatch),
        ip_reputation_bad = v.IpReputationBad, // legitimately 0 when cold — never NaN
        paste_in_identity_fields = B(v.PasteInIdentityFields),
        referrer_missing = v.ReferrerMissing ? 1f : 0f, // always known — never NaN
        input_event_count = v.InputEventCount,
        scroll_events = v.ScrollEvents,
        pages_viewed = v.PagesViewed,

        // ---- CTX ----
        is_mobile = B(v.IsMobile),
        asn_residential = v.AsnType == AsnType.Residential ? 1f : 0f,
        asn_mobile = v.AsnType == AsnType.Mobile ? 1f : 0f,
        asn_business = v.AsnType == AsnType.Business ? 1f : 0f,
        asn_datacenter = v.AsnType == AsnType.Datacenter ? 1f : 0f,
        asn_education = v.AsnType == AsnType.Education ? 1f : 0f,
        asn_government = v.AsnType == AsnType.Government ? 1f : 0f,
        asn_cdn = v.AsnType == AsnType.Cdn ? 1f : 0f, // AsnType.Unknown -> every asn_* stays 0
        is_private_relay = v.IsPrivateRelay ? 1f : 0f, // always known — never NaN
        form_submitted = B(v.FormSubmitted),
        autofill_detected = B(v.AutofillDetected),
        is_paid_click = v.IsPaidClick ? 1f : 0f, // always known — never NaN

        // ChallengeOutcome is deliberately excluded (label-time leakage) — see MlFeatureRow doc.
    };

    /// <summary>null = signal absent/indeterminate -&gt; NaN (never treated as false, spec §7).</summary>
    private static float B(bool? v) => v is null ? float.NaN : v.Value ? 1f : 0f;
}
