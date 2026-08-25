using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TelemetryGuard.Api.Options;

namespace TelemetryGuard.Api.Services;

/// <summary>
/// API-04 step 6: pure load-modify-store aggregation over the per-session Redis
/// hash t:{tid}:sess:{sid}. No I/O here — the endpoint loads the hash, calls
/// <see cref="Apply"/>, and stores the mutated dictionary back, which keeps the
/// Welford/path/checksum math unit-testable without Redis.
///
/// CONTRACT NOTES (binding — RSK-07's RedisSessionStateStore reads these field
/// names verbatim; do not rename without updating that reader):
/// - Running aggregates ONLY — never raw event points (spec §7: SDK ships raw
///   events, the server derives; RSK-04 owns the final feature math such as
///   std = sqrt(mm_m2/(mm_n-1)) and linearity = Dist(first, prev)/mm_path_len).
/// - Missing ≠ zero: absent signals leave hash fields ABSENT. Behavioral fields
///   are never initialized to 0.
/// - Keystroke privacy (spec §3 compliance line): the ky parser reads exactly
///   {e,t,d} — there is deliberately NO code path that touches key values, key
///   codes, or field contents anywhere in this class.
/// - integrity_fails is a STORED counter (never recomputed at read time),
///   incremented at most once per POST per condition: nonce mismatch, seq
///   replay, skew over limit, checksum mismatch.
/// - All numeric fields are invariant-culture strings.
/// </summary>
public static class SessionAggregator
{
    /// <summary>Outcome of one POST's aggregation. EventsAggregated is false for
    /// seq replays (the batch's events are dropped; integrity/lifecycle fields
    /// still update). SawFp/SawFs feed the sink cadence and velocity capture.</summary>
    public sealed record Result(
        Dictionary<string, string> Fields,
        bool EventsAggregated,
        bool SawFp,
        bool SawFs);

    /// <summary>
    /// Applies one envelope to the session hash. <paramref name="hash"/> is the
    /// current field map (mutated in place and returned inside the result);
    /// <paramref name="body"/> the parsed envelope; <paramref name="rawBody"/>
    /// the exact wire string (needed for the SDK-05 checksum);
    /// <paramref name="expectedNonce"/> the server nonce stored by /i/init
    /// (null = expired or never issued); <paramref name="referer"/> the request's
    /// Referer header (HTTP-layer page-URL signal; no event carries a URL).
    /// The caller has already validated: sid shape, body k == query k,
    /// seq >= 0, events is an array.
    /// </summary>
    public static Result Apply(
        Dictionary<string, string> hash,
        JsonElement body,
        string rawBody,
        long nowMs,
        BeaconOptions opts,
        string tid,
        string? expectedNonce,
        string? referer)
    {
        // ---- lifecycle fields (updated on every POST, replay or not) ----
        SetLong(hash, "n_beacons", (GetLong(hash, "n_beacons") ?? 0) + 1);
        hash["has_beacon"] = "1";
        SetLong(hash, "last_beacon_ts", nowMs);
        if (!hash.ContainsKey("page_url") && !string.IsNullOrEmpty(referer))
            hash["page_url"] = referer; // first non-empty Referer wins
        if (!hash.ContainsKey("page_url") && GetString(body, "u") is { Length: > 0 } clientPageUrl)
            hash["page_url"] = clientPageUrl;

        var integrityFails = 0;

        // ---- checksum_ok (SDK-05 seal, mirrored exactly): only when the raw body
        // carries a trailing "c" field. Locate the LAST ,"c":" occurrence;
        // prefix = body[0..idx) + "}"; FNV-1a 32-bit over the UTF-8 bytes of the
        // prefix; ordinal-compare to the embedded 8-hex value. When no "c" field
        // exists (pre-SDK-05 bundles) the field stays ABSENT (missing ≠ zero)
        // and no failure is counted.
        var cIdx = rawBody.LastIndexOf(",\"c\":\"", StringComparison.Ordinal);
        if (cIdx >= 0)
        {
            var embeddedStart = cIdx + 6;
            var embeddedEnd = rawBody.IndexOf('"', embeddedStart);
            var embedded = embeddedEnd > embeddedStart ? rawBody[embeddedStart..embeddedEnd] : "";
            var computed = Fnv1aHex(string.Concat(rawBody.AsSpan(0, cIdx), "}"));
            var checksumOk = string.Equals(embedded, computed, StringComparison.Ordinal);
            hash["checksum_ok"] = checksumOk ? "1" : "0";
            if (!checksumOk) integrityFails++;
        }

        // ---- nonce_ok: stored nonce equals the envelope's nonce ----
        var bodyNonce = GetString(body, "nonce");
        var nonceOk = expectedNonce is not null
                      && bodyNonce is not null
                      && string.Equals(expectedNonce, bodyNonce, StringComparison.Ordinal);
        hash["nonce_ok"] = nonceOk ? "1" : "0";
        if (!nonceOk) integrityFails++;

        // ---- sequence: last_seq absent => -1, so the SDK's first envelope
        // (seq:0) is last_seq + 1 — in sequence, not a replay. ----
        var seq = body.GetProperty("seq").GetInt64(); // caller validated seq >= 0
        var lastSeq = GetLong(hash, "last_seq") ?? -1;
        var aggregated = true;
        if (seq <= lastSeq)
        {
            // Replay: drop the batch's events; lifecycle fields above still updated.
            SetLong(hash, "seq_replays", (GetLong(hash, "seq_replays") ?? 0) + 1);
            integrityFails++;
            aggregated = false;
        }
        else
        {
            if (seq > lastSeq + 1)
                SetLong(hash, "seq_gaps", (GetLong(hash, "seq_gaps") ?? 0) + (seq - lastSeq - 1));
            SetLong(hash, "last_seq", seq);
        }

        // ---- clock skew (recorded as its own signal, never rejected, never an
        // integrity failure: a device clock that is minutes off is ordinary — corporate
        // images, old Androids — and must not earn the beacon_integrity_failed T1
        // floor. skew_bad is sticky once tripped; skew_max_ms tracks the worst.) ----
        if (body.TryGetProperty("sent_at", out var sentAtEl) && sentAtEl.ValueKind == JsonValueKind.Number)
        {
            var skew = Math.Abs(nowMs - (long)sentAtEl.GetDouble());
            SetLong(hash, "skew_max_ms", Math.Max(GetLong(hash, "skew_max_ms") ?? 0, skew));
            if (skew > opts.MaxClockSkewMs)
                hash["skew_bad"] = "1";   // absent = never tripped (missing ≠ zero convention)
        }

        if (integrityFails > 0)
            SetLong(hash, "integrity_fails", (GetLong(hash, "integrity_fails") ?? 0) + integrityFails);

        // ---- per-event aggregation (skipped entirely on replay) ----
        var sawFp = false;
        var sawFs = false;
        if (aggregated && GetString(body, "u") is { } pageUrl && HasHoneyIdentifier(pageUrl))
            hash["honey_identifier_seen"] = "1";
        if (aggregated && body.TryGetProperty("events", out var events)
                       && events.ValueKind == JsonValueKind.Array)
        {
            long total = 0, unknown = 0;
            foreach (var ev in events.EnumerateArray())
            {
                total++;
                if (ev.ValueKind != JsonValueKind.Object || GetString(ev, "e") is not { } e)
                {
                    unknown++;
                    continue;
                }

                switch (e)
                {
                    case "pv":
                        Incr(hash, "n_pv");
                        // SDK event timestamps are milliseconds since navigation start.
                        // Anchor the navigation on the server clock so dwell does not
                        // depend on the visitor's wall clock (which may be skewed).
                        // First page-view wins across later beacon batches.
                        if (!hash.ContainsKey("nav_ts") && GetNumber(ev, "t") is { } pvT
                            && pvT >= 0 && pvT <= opts.SessionTtlSeconds * 1000L)
                            SetLong(hash, "nav_ts", nowMs - (long)pvT);
                        break;
                    case "ga":
                        var gaStatus = GetString(ev, "s");
                        if (gaStatus is "loaded" or "blocked" or "unknown"
                            or "page_view_sent" or "page_view_accepted")
                            SetGaStatus(hash, gaStatus);
                        break;
                    case "pm":
                        ApplyPointerMoves(hash, ev);
                        break;
                    case "pd":
                        // pt counters are the raw modality material; RSK-04 derives
                        // input_modality_mismatch (there is no client modality field).
                        switch (GetString(ev, "pt"))
                        {
                            case "m": Incr(hash, "pt_mouse"); break;
                            case "t": Incr(hash, "pt_touch"); break;
                            case "p": Incr(hash, "pt_pen"); break;
                        }
                        if (GetNumber(ev, "tr") == 0) hash["pointer_untrusted"] = "1";
                        break;
                    case "cl":
                        Incr(hash, "n_click");
                        if (GetNumber(ev, "tr") == 0) hash["pointer_untrusted"] = "1";
                        // sn = the click's own timeStamp — the click-before-render
                        // INGREDIENT; RSK-04 owns the threshold against paint timing.
                        if (GetNumber(ev, "sn") is { } sn)
                        {
                            var min = GetDouble(hash, "cl_min_sn");
                            if (min is null || sn < min.Value) SetDouble(hash, "cl_min_sn", sn);
                        }
                        break;
                    case "sc":
                        Incr(hash, "n_scroll");
                        break;
                    case "ky":
                        // TIMING ONLY (spec §3): the wire contract is exactly {e,t,d};
                        // only the keydown flag d is read — never key identity.
                        if (GetNumber(ev, "d") == 1) Incr(hash, "n_key");
                        break;
                    case "ff":
                        hash["form_started"] = "1";
                        if (!hash.ContainsKey("first_ff_ts") && GetNumber(ev, "t") is { } ffT)
                            SetDouble(hash, "first_ff_ts", ffT); // first focus wins
                        break;
                    case "fb":
                        break; // blur carries no aggregate beyond the ff/fs pair
                    case "fs":
                        hash["form_submitted"] = "1";
                        sawFs = true;
                        // Server-derived fill time — there is no client fill_ms field.
                        if (GetDouble(hash, "first_ff_ts") is { } firstFf && GetNumber(ev, "t") is { } fsT)
                            SetDouble(hash, "form_fill_ms", fsT - firstFf);
                        break;
                    case "pa":
                        if (GetString(ev, "fk") == "identity") hash["paste_identity"] = "1";
                        break;
                    case "af":
                        hash["autofill"] = "1";
                        break;
                    case "hp":
                        hash["hp_touched"] = "1";
                        switch (GetString(ev, "kind"))
                        {
                            case "input":
                            case "submit_filled": hash["hp_field_filled"] = "1"; break;
                            case "link_clicked": hash["hp_link_clicked"] = "1"; break;
                        }
                        break;
                    case "fi":
                        // fi.t is ms since performance.timeOrigin — already the delay.
                        if (!hash.ContainsKey("first_interaction_delay_ms") && GetNumber(ev, "t") is { } fiT)
                            SetDouble(hash, "first_interaction_delay_ms", fiT);
                        break;
                    case "fp":
                        sawFp = true;
                        ApplyFingerprint(hash, ev, nowMs, opts, tid);
                        break;
                    default:
                        unknown++; // counted and skipped, never rejected
                        break;
                }
            }

            if (total > 0)
                SetLong(hash, "n_events_total", (GetLong(hash, "n_events_total") ?? 0) + total);
            if (unknown > 0)
                SetLong(hash, "n_unknown", (GetLong(hash, "n_unknown") ?? 0) + unknown);
        }

        return new Result(hash, aggregated, sawFp, sawFs);
    }

    private static void SetGaStatus(Dictionary<string, string> hash, string status)
    {
        static int Rank(string value) => value switch
        {
            "page_view_accepted" => 4,
            "page_view_sent" => 3,
            "loaded" => 2,
            "blocked" => 1,
            _ => 0,
        };

        if (!hash.TryGetValue("ga_status", out var current) || Rank(status) > Rank(current))
            hash["ga_status"] = status;
    }

    /// <summary>Welford over inter-SAMPLE gaps of pm batches plus accumulated
    /// path length + endpoints — the ingredients only; RSK-04 finishes
    /// std/linearity. mm_prev_ts / mm_prev_x/y carry across batches AND POSTs.</summary>
    private static void ApplyPointerMoves(Dictionary<string, string> hash, JsonElement ev)
    {
        if (!ev.TryGetProperty("s", out var samples) || samples.ValueKind != JsonValueKind.Array)
            return;

        var n = GetLong(hash, "mm_n") ?? 0;
        var mean = GetDouble(hash, "mm_mean_ms") ?? 0;
        var m2 = GetDouble(hash, "mm_m2") ?? 0;
        var prevTs = GetDouble(hash, "mm_prev_ts");
        var prevX = GetDouble(hash, "mm_prev_x");
        var prevY = GetDouble(hash, "mm_prev_y");
        var pathLen = GetDouble(hash, "mm_path_len");

        foreach (var sample in samples.EnumerateArray())
        {
            if (sample.ValueKind != JsonValueKind.Array || sample.GetArrayLength() < 3)
                continue;
            double t, x, y;
            var i = 0;
            double[] vals = new double[3];
            var ok = true;
            foreach (var el in sample.EnumerateArray())
            {
                if (i >= 3) break;
                if (el.ValueKind != JsonValueKind.Number) { ok = false; break; }
                vals[i++] = el.GetDouble();
            }
            if (!ok || i < 3) continue;
            (t, x, y) = (vals[0], vals[1], vals[2]);

            if (prevTs is { } pt)
            {
                var delta = t - pt;
                n++;
                var d = delta - mean;
                mean += d / n;
                m2 += d * (delta - mean);
            }
            prevTs = t;

            if (prevX is { } px && prevY is { } py)
            {
                var dx = x - px;
                var dy = y - py;
                pathLen = (pathLen ?? 0) + Math.Sqrt(dx * dx + dy * dy);
            }
            else
            {
                // First point of the session, stored once (linearity numerator base).
                SetDouble(hash, "mm_first_x", x);
                SetDouble(hash, "mm_first_y", y);
            }
            prevX = x;
            prevY = y;
        }

        // Missing ≠ zero: only write what was actually observed.
        if (n > 0)
        {
            SetLong(hash, "mm_n", n);
            SetDouble(hash, "mm_mean_ms", mean);
            SetDouble(hash, "mm_m2", m2);
        }
        if (prevTs is { } lastTs) SetDouble(hash, "mm_prev_ts", lastTs);
        if (pathLen is { } pl) SetDouble(hash, "mm_path_len", pl);
        if (prevX is { } lx) SetDouble(hash, "mm_prev_x", lx);
        if (prevY is { } ly) SetDouble(hash, "mm_prev_y", ly);
    }

    /// <summary>fp event (SDK-04, once per session): device/Botd/storage mapping.
    /// There are NO headless/emulator/modality wire fields — RSK-04 derives those
    /// tiers from wd/botd_*. Client-computed storage.ageMs is convenience data and
    /// never trusted; the server verifies the HMAC pair itself.</summary>
    private static void ApplyFingerprint(
        Dictionary<string, string> hash, JsonElement ev, long nowMs, BeaconOptions opts, string tid)
    {
        if (GetString(ev, "vid") is { } vid) hash["visitor_id"] = vid;
        if (GetNumber(ev, "conf") is { } conf) SetDouble(hash, "conf", conf);
        if (GetBool(ev, "wd") is { } wd) hash["webdriver"] = wd ? "1" : "0";
        if (GetBool(ev, "canvasBlocked") is { } cb) hash["canvas_blocked"] = cb ? "1" : "0";
        if (GetBool(ev, "touch") is { } touch) hash["touch"] = touch ? "1" : "0";
        if (GetBool(ev, "mob") is { } mob) hash["is_mobile"] = mob ? "1" : "0";
        if (GetString(ev, "tz") is { } tz) hash["tz"] = tz;

        if (ev.TryGetProperty("scr", out var scr) && scr.ValueKind == JsonValueKind.Array
            && scr.GetArrayLength() >= 4)
        {
            var i = 0;
            string[] names = ["screen_w", "screen_h", "color_depth", "dpr"];
            foreach (var el in scr.EnumerateArray())
            {
                if (i >= 4) break;
                if (el.ValueKind == JsonValueKind.Number) SetDouble(hash, names[i], el.GetDouble());
                i++;
            }
        }

        if (ev.TryGetProperty("langs", out var langs) && langs.ValueKind == JsonValueKind.Array)
        {
            var joined = string.Join(",", langs.EnumerateArray()
                .Where(l => l.ValueKind == JsonValueKind.String)
                .Select(l => l.GetString()));
            if (joined.Length > 0) hash["langs"] = joined;
        }

        if (ev.TryGetProperty("botd", out var botd) && botd.ValueKind == JsonValueKind.Object)
        {
            if (GetBool(botd, "bot") is { } bot) hash["botd_bot"] = bot ? "1" : "0";
            if (GetString(botd, "kind") is { } kind) hash["botd_kind"] = kind;
        }

        if (ev.TryGetProperty("storage", out var storage) && storage.ValueKind == JsonValueKind.Object)
        {
            if (GetBool(storage, "cookiesDisabled") is { } cd) hash["cookies_disabled"] = cd ? "1" : "0";

            // Take the ck pair when ck.present with ts+sig, else the ls pair.
            var pair = SelectStoragePair(storage);
            if (pair is { } p)
            {
                var sigOk = VerifyStorageSig(opts.HmacSecret, tid, p.Ts, p.Sig);
                hash["storage_sig_ok"] = sigOk ? "1" : "0";
                if (sigOk)
                {
                    // Server-computed age; a tampered/unverified sig leaves the
                    // field ABSENT (missing ≠ zero — RSK-04's NaN gate needs that).
                    SetLong(hash, "storage_age_sec", Math.Max(0, (nowMs - p.Ts) / 1000));
                }
            }
        }
    }

    private static (long Ts, string Sig)? SelectStoragePair(JsonElement storage)
    {
        if (storage.TryGetProperty("ck", out var ck) && ck.ValueKind == JsonValueKind.Object
            && GetBool(ck, "present") == true && ReadPair(ck) is { } ckPair)
            return ckPair;
        if (storage.TryGetProperty("ls", out var ls) && ls.ValueKind == JsonValueKind.Object
            && ReadPair(ls) is { } lsPair)
            return lsPair;
        return null;

        static (long Ts, string Sig)? ReadPair(JsonElement el)
            => GetNumber(el, "ts") is { } ts && GetString(el, "sig") is { } sig
                ? ((long)ts, sig)
                : null;
    }

    // ------------------------------------------------------------------ crypto --

    /// <summary>Lowercase-hex HMACSHA256(key: UTF8(secret), data: UTF8($"{tid}:{ts}")).
    /// Used both to MINT the /i/init pair and to VERIFY the fp event's reported pair.</summary>
    public static string ComputeStorageSig(string secret, string tid, long ts)
        => Convert.ToHexString(HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(secret),
                Encoding.UTF8.GetBytes(FormattableString.Invariant($"{tid}:{ts}"))))
            .ToLowerInvariant();

    /// <summary>Constant-time comparison of the reported sig against the expected one.</summary>
    internal static bool VerifyStorageSig(string secret, string tid, long ts, string reportedSig)
    {
        var expected = Encoding.UTF8.GetBytes(ComputeStorageSig(secret, tid, ts));
        var reported = Encoding.UTF8.GetBytes(reportedSig.ToLowerInvariant());
        return expected.Length == reported.Length
               && CryptographicOperations.FixedTimeEquals(expected, reported);
    }

    /// <summary>FNV-1a 32-bit over the UTF-8 bytes of <paramref name="input"/>,
    /// as 8 lowercase hex chars — the exact SDK-05 algorithm (offset basis
    /// 0x811c9dc5, prime 0x01000193).</summary>
    public static string Fnv1aHex(string input)
    {
        var hash = 0x811c9dc5u;
        foreach (var b in Encoding.UTF8.GetBytes(input))
        {
            hash ^= b;
            hash *= 0x01000193u;
        }
        return hash.ToString("x8", CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------- hash field helpers --

    private static void Incr(Dictionary<string, string> hash, string field)
        => SetLong(hash, field, (GetLong(hash, field) ?? 0) + 1);

    private static void SetLong(Dictionary<string, string> hash, string field, long value)
        => hash[field] = value.ToString(CultureInfo.InvariantCulture);

    private static void SetDouble(Dictionary<string, string> hash, string field, double value)
        => hash[field] = value.ToString("R", CultureInfo.InvariantCulture);

    internal static long? GetLong(Dictionary<string, string> hash, string field)
        => hash.TryGetValue(field, out var v)
           && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    internal static double? GetDouble(Dictionary<string, string> hash, string field)
        => hash.TryGetValue(field, out var v)
           && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    // --------------------------------------------------- JSON read helpers --

    private static string? GetString(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static bool HasHoneyIdentifier(string pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri)) return false;
        return uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)[0])
            .Any(key => string.Equals(Uri.UnescapeDataString(key), "tg_honey", StringComparison.Ordinal));
    }

    private static double? GetNumber(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetDouble()
            : null;

    /// <summary>Reads a JSON bool, tolerating the 0/1 number encoding some
    /// collectors use for compactness.</summary>
    private static bool? GetBool(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var p))
            return null;
        return p.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => p.GetDouble() != 0,
            _ => null,
        };
    }
}
