namespace TelemetryGuard.RiskEngine.Contracts;

/// <summary>
/// RSK-08: the flat ML.NET input row for the LightGBM model — one public float field
/// per model input, feature_set_version = 1. FROZEN v1 order (do not reorder/rename/
/// insert without bumping <see cref="FraudFeatureVector.FeatureSetVersion"/> AND
/// retraining — the model's Concatenate("Features", ...) call binds these names).
///
/// Names are lower_snake_case ML feature names (not C# PascalCase) deliberately: they
/// are the literal ML.NET column names and appear verbatim in feature-importance
/// dumps/model introspection. <see cref="MlFeatureMapper"/> is the ONLY place that
/// converts a <see cref="FraudFeatureVector"/> into this shape.
///
/// Encoding (spec §7, "missing ≠ zero"): bool? -&gt; 1/0/NaN; bool -&gt; 1/0 (always
/// known, never NaN); NaN floats are preserved verbatim; AsnType is one-hot (Unknown
/// = all-zero). LightGBM (HandleMissingValue = true) branches on NaN natively.
///
/// <see cref="ChallengeOutcome"/> is deliberately EXCLUDED: it does not exist at
/// first-score time and would leak label-time state into the model.
///
/// Plain class (not a record) with public fields — the shape <c>LoadFromEnumerable</c>
/// needs. Zero package dependencies (POCO only), usable by both TelemetryGuard.Training
/// (offline, builds the training IDataView) and TelemetryGuard.RiskEngine (serving,
/// MlNetScorer/PredictionEnginePool).
/// </summary>
public sealed class MlFeatureRow
{
    // ================= T1 (1-14) =================
    public float honeypot_touched;
    public float click_before_render;
    public float pointer_untrusted;
    public float webdriver_flag;
    public float headless_browser;
    /// <summary>1 when BeaconIntegrityOk == false (fires on the FALSE direction,
    /// mirroring RSK-05 rule 6) — NOT a straight bool? passthrough.</summary>
    public float beacon_integrity_failed;
    public float ip_tor;
    public float tls_ua_mismatch;
    public float ip_datacenter_asn;
    public float ip_clicks_last_min;
    public float ua_os_mismatch;
    public float mouse_path_linearity;
    public float std_inter_event_ms;
    public float click_id_invalid;

    // ================= T2 (15-28) =================
    public float has_js_beacon;
    public float emulator_or_vm;
    public float screen_res_anomalous;
    public float storage_age_zero_repeat;
    public float ip_proxy_or_vpn;
    public float ip_geo_target_mismatch;
    public float time_on_page_sec;
    public float form_fill_time_sec;
    public float first_interaction_delay_ms;
    public float input_modality_mismatch;
    public float mean_inter_event_ms;
    public float device_sessions_last_hour;
    public float ip_distinct_uas_last_hour;
    public float device_ids_this_ip_hour;

    // ================= T3 (29-38) =================
    public float cookies_disabled;
    public float canvas_fp_blocked;
    public float timezone_ip_mismatch;
    public float language_geo_mismatch;
    public float ip_reputation_bad;
    public float paste_in_identity_fields;
    public float referrer_missing;
    public float input_event_count;
    public float scroll_events;
    public float pages_viewed;

    // ================= CTX (39-50) =================
    public float is_mobile;
    public float asn_residential;
    public float asn_mobile;
    public float asn_business;
    public float asn_datacenter;
    public float asn_education;
    public float asn_government;
    public float asn_cdn;
    public float is_private_relay;
    public float form_submitted;
    public float autofill_detected;
    public float is_paid_click;

    // ================= Training-only (never used by the serving scorer) =================
    public bool Label;
    public float Weight = 1f;

    /// <summary>The 50 model-input field names, in the FROZEN v1 order — used both by
    /// the trainer's <c>Concatenate("Features", ...)</c> call and by
    /// <c>MlFeatureMapperTests</c>' reflection-based field-count assertion.</summary>
    public static readonly string[] FeatureFieldNames =
    [
        nameof(honeypot_touched), nameof(click_before_render), nameof(pointer_untrusted),
        nameof(webdriver_flag), nameof(headless_browser), nameof(beacon_integrity_failed),
        nameof(ip_tor), nameof(tls_ua_mismatch), nameof(ip_datacenter_asn),
        nameof(ip_clicks_last_min), nameof(ua_os_mismatch), nameof(mouse_path_linearity),
        nameof(std_inter_event_ms), nameof(click_id_invalid),

        nameof(has_js_beacon), nameof(emulator_or_vm), nameof(screen_res_anomalous),
        nameof(storage_age_zero_repeat), nameof(ip_proxy_or_vpn), nameof(ip_geo_target_mismatch),
        nameof(time_on_page_sec), nameof(form_fill_time_sec), nameof(first_interaction_delay_ms),
        nameof(input_modality_mismatch), nameof(mean_inter_event_ms), nameof(device_sessions_last_hour),
        nameof(ip_distinct_uas_last_hour), nameof(device_ids_this_ip_hour),

        nameof(cookies_disabled), nameof(canvas_fp_blocked), nameof(timezone_ip_mismatch),
        nameof(language_geo_mismatch), nameof(ip_reputation_bad), nameof(paste_in_identity_fields),
        nameof(referrer_missing), nameof(input_event_count), nameof(scroll_events), nameof(pages_viewed),

        nameof(is_mobile), nameof(asn_residential), nameof(asn_mobile), nameof(asn_business),
        nameof(asn_datacenter), nameof(asn_education), nameof(asn_government), nameof(asn_cdn),
        nameof(is_private_relay), nameof(form_submitted), nameof(autofill_detected), nameof(is_paid_click),
    ];
}
