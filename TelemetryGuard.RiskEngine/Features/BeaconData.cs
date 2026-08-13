namespace TelemetryGuard.RiskEngine.Features;

/// <summary>PRE-AGGREGATED, timing-only behavioral state. This shape is normative for the
/// risk side; the scoring pipeline (RSK-07) maps it field-for-field from the Redis session
/// hash that API-04 maintains at ingest. API-04 stores running aggregates ONLY and never
/// raw event points, so this record carries the aggregates, not an event list. No keystroke
/// CONTENT anywhere — counters and timing statistics only (spec §3 non-goal).</summary>
public sealed record BeaconData
{
    public string? VisitorId { get; init; }              // FingerprintJS visitor id

    // --- Mouse-movement aggregates (API-04 step 6: Welford over inter-`mm` gaps +
    //     accumulated path length and endpoints; hash fields named in comments) ---

    /// <summary>Number of inter-move gaps (`mm_n`). Mouse POINT count = MmN + 1
    /// when any move was seen (FirstX is not NaN), else 0.</summary>
    public int MmN { get; init; }
    public double MmMeanMs { get; init; } = double.NaN;  // mm_mean_ms (Welford mean of gaps)
    public double MmM2 { get; init; } = double.NaN;      // mm_m2 (sum of squared deviations)
    public double MmPathLen { get; init; }               // mm_path_len (Σ segment lengths)
    public float FirstX { get; init; } = float.NaN;      // mm_first_x; NaN = no move seen
    public float FirstY { get; init; } = float.NaN;      // mm_first_y
    public float PrevX { get; init; } = float.NaN;       // mm_prev_x (latest point)
    public float PrevY { get; init; } = float.NaN;       // mm_prev_y

    /// <summary>ms from navigation start to first interaction, computed AT INGEST
    /// (hash `first_interaction_delay_ms`). API-04's definition is authoritative
    /// because API-04 computes it: earliest click/key/scroll/touch event minus
    /// nav_ts (mouse moves excluded). null = not observed. Consumed verbatim —
    /// this task never recomputes it.</summary>
    public double? FirstInteractionDelayMs { get; init; }

    // --- Event counters (hash n_click / n_key / n_scroll / n_touch) ---
    public int ClickCount { get; init; }
    public int KeyCount { get; init; }
    public int ScrollEventCount { get; init; }
    public int TouchCount { get; init; }

    // Flags observed by the SDK (already booleans at capture; absence within a present
    // beacon should be sent as false by the SDK — a present beacon reports all checks):
    public bool WebdriverFlag { get; init; }             // navigator.webdriver
    public bool HeadlessBrowser { get; init; }           // Botd verdict
    public bool HoneypotTouched { get; init; }
    public bool PointerUntrusted { get; init; }          // any pointer event with isTrusted=false
    public bool ClickBeforeRender { get; init; }
    public bool IntegrityOk { get; init; } = true;       // beacon signature check (API-04 verifies)

    public int? ScreenWidth { get; init; }
    public int? ScreenHeight { get; init; }
    public int? ViewportWidth { get; init; }
    public int? ViewportHeight { get; init; }
    public double? DevicePixelRatio { get; init; }
    public string? Timezone { get; init; }               // IANA from Intl API
    public string? Language { get; init; }               // navigator.language
    public bool CookiesEnabled { get; init; } = true;
    public bool CanvasFpBlocked { get; init; }
    public double? StorageAgeSec { get; init; }          // age of our first-party cookie/localStorage stamp
    public double SessionDurationMs { get; init; }       // last_beacon_ts − nav_ts (RSK-07 maps it)
    public int PagesViewed { get; init; } = 1;

    // Form telemetry (timing only):
    public bool FormSubmitted { get; init; }
    public double? FormFirstFocusTMs { get; init; }
    public double? FormSubmitTMs { get; init; }
    public bool AutofillDetected { get; init; }
    public bool PasteInIdentityFields { get; init; }
}
