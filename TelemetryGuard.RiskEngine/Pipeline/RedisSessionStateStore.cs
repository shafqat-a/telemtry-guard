using System.Globalization;
using StackExchange.Redis;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.RiskEngine.Features;

namespace TelemetryGuard.RiskEngine.Pipeline;

/// <summary>
/// Pure READER over the ingest-written session state (this class never writes; producer
/// TTLs govern expiry). One IBatch issues HGETALL t:{tid}:click:{sid} (API-02/API-03's
/// click-context hash) + HGETALL t:{tid}:sess:{sid} (API-04's aggregate session hash) —
/// a single Redis round trip inside the &lt; 50 ms budget (D3). Field names are the
/// producers' binding contract; numeric fields are invariant-culture strings.
/// The store does NOT finish any statistics: std/linearity math from the aggregates
/// belongs to RSK-04's TrajectoryStats (population std = sqrt(mm_m2 / mm_n),
/// linearity = Dist(first, prev) / mm_path_len). Register scoped — the tenant context
/// is scoped per request (D11).
/// </summary>
public sealed class RedisSessionStateStore : ISessionStateStore
{
    private readonly IConnectionMultiplexer _mux;
    private readonly ITenantContext _tenant;

    public RedisSessionStateStore(IConnectionMultiplexer mux, ITenantContext tenant)
    {
        _mux = mux;
        _tenant = tenant;
    }

    public async Task<SessionState?> GetAsync(string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("Session id must not be null or empty.", nameof(sessionId));
        }

        ct.ThrowIfCancellationRequested();

        var batch = _mux.GetDatabase().CreateBatch();
        var clickTask = batch.HashGetAllAsync(K($"click:{sessionId}"));
        var sessTask = batch.HashGetAllAsync(K($"sess:{sessionId}"));
        batch.Execute();
        await Task.WhenAll(clickTask, sessTask).ConfigureAwait(false);

        var clickHash = ToMap(clickTask.Result);
        var sessHash = ToMap(sessTask.Result);
        if (clickHash.Count == 0 && sessHash.Count == 0)
        {
            return null; // unknown session
        }

        var click = clickHash.Count == 0 ? null : MapClick(clickHash);
        var beacon = sessHash.Count == 0 ? null : MapBeacon(sessHash);
        return new SessionState(click, beacon, click is null ? null : Str(clickHash, "campaign_id"));
    }

    /// <summary>Click-hash mapping per API-02 step 3.7 (empty string = absent).</summary>
    private static ClickState MapClick(Dictionary<string, string> h)
    {
        // Header values captured verbatim at click time, re-exposed under the header
        // names RSK-04's extractor consumes (Accept-Language, Referer, Sec-CH-UA-*);
        // header_order is kept verbatim under its own hash-field name.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddHeader(headers, "Sec-CH-UA", Str(h, "ch_ua"));
        AddHeader(headers, "Sec-CH-UA-Mobile", Str(h, "ch_mobile"));
        AddHeader(headers, "Sec-CH-UA-Platform", Str(h, "ch_platform"));
        AddHeader(headers, "Accept-Language", Str(h, "accept_language"));
        AddHeader(headers, "Referer", Str(h, "referrer"));
        AddHeader(headers, "header_order", Str(h, "header_order"));

        // ClickIdFresh derived: no click_id → null (nothing to judge);
        // click_id present + click_id_invalid=0 → true (fresh);
        // click_id present + click_id_invalid=1 → false (replayed/missing dedupe).
        var clickId = Str(h, "click_id");
        bool? clickIdFresh = null;
        if (clickId is not null)
        {
            clickIdFresh = Str(h, "click_id_invalid") switch
            {
                "0" => true,
                "1" => false,
                _ => null, // capture-time dedupe unavailable
            };
        }

        return new ClickState(
            Ip: Str(h, "ip") ?? string.Empty,
            UserAgent: Str(h, "ua"),
            Headers: headers,
            ClickIdType: Str(h, "click_id_type"),
            ClickId: clickId,
            ClickIdFresh: clickIdFresh,
            IsPaidClick: Str(h, "kind") == "tracker",
            // No producer writes a TLS fingerprint yet (Cloudflare fronting is INT-05,
            // Phase 1.5); read the reserved field so the reader is forward-compatible.
            TlsFingerprint: Str(h, "tls_fp"),
            Timestamp: Long(h, "ts") is { } ts
                ? DateTimeOffset.FromUnixTimeMilliseconds(ts)
                : DateTimeOffset.UnixEpoch);
    }

    /// <summary>Session-hash mapping → RSK-04's aggregate BeaconData, field-for-field from
    /// API-04 step 6. Absent numeric fields map to the BeaconData member's NaN/null
    /// default — never 0 (missing ≠ zero). Where API-04's shipped field names differ from
    /// the names this task pins (n_touch/headless/cookies_enabled/lang), the producer's
    /// actual spelling (pt_touch/botd_bot/cookies_disabled/langs) is accepted as fallback.</summary>
    private static BeaconData MapBeacon(Dictionary<string, string> h)
    {
        // SessionDurationMs = last_beacon_ts − nav_ts, only when both are present; else 0.
        double sessionDurationMs = 0;
        if (Long(h, "last_beacon_ts") is { } lastBeaconTs && Long(h, "nav_ts") is { } navTs)
        {
            sessionDurationMs = lastBeaconTs - navTs;
        }

        // cookies_enabled (pinned name) with cookies_disabled (API-04's wire name,
        // negated) as fallback; absent → BeaconData default (true).
        var cookiesEnabled = Flag(h, "cookies_enabled") ?? Negate(Flag(h, "cookies_disabled")) ?? true;

        // lang (pinned name) with langs (API-04 joins navigator.languages with ',') fallback.
        var language = Str(h, "lang") ?? FirstToken(Str(h, "langs"));

        // Form focus/submit timestamps when present; otherwise reconstruct from API-04's
        // server-derived form_fill_ms (fs.t − first_ff_ts) so RSK-04's
        // form_fill_time_sec = (SubmitT − FirstFocusT)/1000 reproduces the fill time.
        var formFirstFocus = Dbl(h, "form_first_focus_t_ms");
        var formSubmit = Dbl(h, "form_submit_t_ms");
        if (formFirstFocus is null && formSubmit is null && Dbl(h, "form_fill_ms") is { } fillMs)
        {
            formFirstFocus = 0;
            formSubmit = fillMs;
        }

        return new BeaconData
        {
            VisitorId = Str(h, "visitor_id"),

            MmN = Int(h, "mm_n") ?? 0,
            MmMeanMs = Dbl(h, "mm_mean_ms") ?? double.NaN,
            MmM2 = Dbl(h, "mm_m2") ?? double.NaN,
            MmPathLen = Dbl(h, "mm_path_len") ?? 0,
            FirstX = Flt(h, "mm_first_x") ?? float.NaN,
            FirstY = Flt(h, "mm_first_y") ?? float.NaN,
            PrevX = Flt(h, "mm_prev_x") ?? float.NaN,
            PrevY = Flt(h, "mm_prev_y") ?? float.NaN,

            FirstInteractionDelayMs = Dbl(h, "first_interaction_delay_ms"), // absent → null

            ClickCount = Int(h, "n_click") ?? 0,
            KeyCount = Int(h, "n_key") ?? 0,
            ScrollEventCount = Int(h, "n_scroll") ?? 0,
            TouchCount = Int(h, "n_touch") ?? Int(h, "pt_touch") ?? 0,

            WebdriverFlag = Flag(h, "webdriver") ?? false,
            HeadlessBrowser = Flag(h, "headless") ?? Flag(h, "botd_bot") ?? false,
            HoneypotTouched = Flag(h, "hp_touched") ?? false,
            PointerUntrusted = Flag(h, "pointer_untrusted") ?? false,
            ClickBeforeRender = Flag(h, "click_before_render") ?? false,
            // Derived from the STORED integrity_fails counter: absent or 0 → intact.
            // (Checksum, nonce and sequence failures only — clock skew is separate.)
            IntegrityOk = (Int(h, "integrity_fails") ?? 0) == 0,
            ClockSkewBad = Flag(h, "skew_bad"),

            ScreenWidth = Int(h, "screen_w"),
            ScreenHeight = Int(h, "screen_h"),
            DevicePixelRatio = Dbl(h, "dpr"),
            CookiesEnabled = cookiesEnabled,
            CanvasFpBlocked = Flag(h, "canvas_blocked") ?? false,
            Timezone = Str(h, "tz"),
            Language = language,
            // Field absent when the storage HMAC did not verify — leave null; missing ≠ zero.
            StorageAgeSec = Dbl(h, "storage_age_sec"),
            SessionDurationMs = sessionDurationMs,
            PagesViewed = Int(h, "n_pv") ?? 1,

            FormSubmitted = Flag(h, "form_submitted") ?? false,
            FormFirstFocusTMs = formFirstFocus,
            FormSubmitTMs = formSubmit,
            AutofillDetected = Flag(h, "autofill") ?? false,
            PasteInIdentityFields = Flag(h, "paste_identity") ?? false,
        };
    }

    /// <summary>The ONLY key constructor — guarantees the t:{tenantId}: prefix (D11).
    /// TenantId.ToString() is the canonical lowercase "D" GUID form (FND-04).</summary>
    private RedisKey K(string suffix) => $"t:{_tenant.TenantId}:{suffix}";

    private static Dictionary<string, string> ToMap(HashEntry[] entries)
    {
        var map = new Dictionary<string, string>(entries.Length, StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            map[entry.Name.ToString()] = entry.Value.ToString();
        }
        return map;
    }

    private static void AddHeader(Dictionary<string, string> headers, string name, string? value)
    {
        if (value is not null)
        {
            headers[name] = value;
        }
    }

    /// <summary>Empty string encodes "absent" in the producer hashes → null.</summary>
    private static string? Str(Dictionary<string, string> h, string field)
        => h.TryGetValue(field, out var v) && v.Length > 0 ? v : null;

    private static int? Int(Dictionary<string, string> h, string field)
        => Str(h, field) is { } v
           && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : null;

    private static long? Long(Dictionary<string, string> h, string field)
        => Str(h, field) is { } v
           && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : null;

    private static double? Dbl(Dictionary<string, string> h, string field)
        => Str(h, field) is { } v
           && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : null;

    private static float? Flt(Dictionary<string, string> h, string field)
        => Str(h, field) is { } v
           && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : null;

    /// <summary>"1" → true, "0" → false, absent → null (missing ≠ zero).</summary>
    private static bool? Flag(Dictionary<string, string> h, string field)
        => Str(h, field) switch { "1" => true, "0" => false, _ => null };

    private static bool? Negate(bool? value) => value is { } v ? !v : null;

    private static string? FirstToken(string? csv)
    {
        if (csv is null)
        {
            return null;
        }
        var comma = csv.IndexOf(',');
        var first = (comma >= 0 ? csv[..comma] : csv).Trim();
        return first.Length > 0 ? first : null;
    }
}
