using System.Text;
using System.Text.Json;
using TelemetryGuard.Api.Options;
using TelemetryGuard.Api.Services;

namespace TelemetryGuard.Tests.Unit.Api;

/// <summary>
/// API-04 pure aggregation tests (no I/O): Welford inter-sample stats incl.
/// cross-batch continuity, path length ingredients, first-interaction delay,
/// integrity counters (FNV-1a checksum valid/tampered/absent, nonce, seq
/// replay/gap, skew), fp field mapping, storage HMAC verification (ck/ls),
/// pv/unknown counting, and the keystroke-privacy guarantee (parser reads
/// exactly {e,t,d} — key identity never lands in the hash).
/// </summary>
public sealed class SessionAggregatorTests
{
    private const string Tid = "6f9619ff-8b86-d011-b42d-00cf4fc964ff";
    private const string Sid = "9f8e7d6c5b4a39281706f5e4d3c2b1a0";
    private const string SiteKey = "site-1";
    private const string Nonce = "a1b2c3d4e5f60718293a4b5c6d7e8f90";
    private const string Secret = "test-secret-0123456789abcdef-0123456789";
    private static readonly long NowMs = new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero)
        .ToUnixTimeMilliseconds();

    private static readonly BeaconOptions Opts = new() { HmacSecret = Secret };

    // ------------------------------------------------------------- helpers --

    private static string Envelope(string eventsJson, long seq = 0, string nonce = Nonce, long? sentAt = null)
        => $"{{\"k\":\"{SiteKey}\",\"sid\":\"{Sid}\",\"seq\":{seq},\"nonce\":\"{nonce}\"," +
           $"\"sent_at\":{sentAt ?? NowMs},\"events\":{eventsJson}}}";

    /// <summary>Independent SDK-05 sealer (FNV-1a re-implemented here on purpose
    /// so the test does not lean on the production hash).</summary>
    private static string Seal(string json)
    {
        var h = 0x811c9dc5u;
        foreach (var b in Encoding.UTF8.GetBytes(json))
        {
            h ^= b;
            h *= 0x01000193u;
        }
        return json[..^1] + ",\"c\":\"" + h.ToString("x8") + "\"}";
    }

    private static SessionAggregator.Result Apply(
        Dictionary<string, string> hash, string rawBody,
        string? expectedNonce = Nonce, string? referer = null, long? nowMs = null)
    {
        var body = JsonSerializer.Deserialize<JsonElement>(rawBody);
        return SessionAggregator.Apply(
            hash, body, rawBody, nowMs ?? NowMs, Opts, Tid, expectedNonce, referer);
    }

    private static Dictionary<string, string> NewHash() => new(StringComparer.Ordinal);

    // --------------------------------------------------------- Welford/path --

    [Fact]
    public void PmBatch_WelfordGaps_AndPathLength_MatchTheSpecNumbers()
    {
        var hash = NewHash();

        // Samples at t 0,100,300 (gaps 100,200) moving (0,0)->(3,4)->(6,8).
        Apply(hash, Envelope("""[{"e":"pm","t":300,"s":[[0,0,0],[100,3,4],[300,6,8]]}]"""));

        Assert.Equal("2", hash["mm_n"]);
        Assert.Equal(150, double.Parse(hash["mm_mean_ms"]));
        Assert.Equal(5000, double.Parse(hash["mm_m2"]));
        Assert.Equal(10, double.Parse(hash["mm_path_len"]));   // 5 + 5
        Assert.Equal(0, double.Parse(hash["mm_first_x"]));
        Assert.Equal(0, double.Parse(hash["mm_first_y"]));
        Assert.Equal(6, double.Parse(hash["mm_prev_x"]));
        Assert.Equal(8, double.Parse(hash["mm_prev_y"]));
        Assert.Equal(300, double.Parse(hash["mm_prev_ts"]));
    }

    [Fact]
    public void PmGapContinuity_CarriesAcrossBatchesAndPosts()
    {
        var hash = NewHash();

        Apply(hash, Envelope("""[{"e":"pm","t":100,"s":[[0,0,0],[100,10,0]]}]""", seq: 0));
        Apply(hash, Envelope("""[{"e":"pm","t":300,"s":[[300,20,0]]}]""", seq: 1));

        // Gaps 100 (in-batch) and 200 (across POSTs via mm_prev_ts) => same
        // Welford numbers as one contiguous stream.
        Assert.Equal("2", hash["mm_n"]);
        Assert.Equal(150, double.Parse(hash["mm_mean_ms"]));
        Assert.Equal(5000, double.Parse(hash["mm_m2"]));
        Assert.Equal(20, double.Parse(hash["mm_path_len"]));   // 10 + 10 along x
        Assert.Equal(300, double.Parse(hash["mm_prev_ts"]));
    }

    // ------------------------------------------------------- event mapping --

    [Fact]
    public void FirstInteractionDelay_FirstFiWins()
    {
        var hash = NewHash();

        Apply(hash, Envelope("""[{"e":"fi","t":450,"it":"pointerdown"}]""", seq: 0));
        Apply(hash, Envelope("""[{"e":"fi","t":900,"it":"keydown"}]""", seq: 1));

        Assert.Equal(450, double.Parse(hash["first_interaction_delay_ms"]));
    }

    [Fact]
    public void Pv_CountsPerEvent_AcrossPosts()
    {
        var hash = NewHash();

        Apply(hash, Envelope("""[{"e":"pv","t":12}]""", seq: 0));
        Apply(hash, Envelope("""[{"e":"pv","t":34}]""", seq: 1));

        Assert.Equal("2", hash["n_pv"]);
    }

    [Fact]
    public void UnknownEventType_IsCountedAndSkipped_NeverRejected()
    {
        var hash = NewHash();

        var result = Apply(hash, Envelope("""[{"e":"zz","t":1,"whatever":true},{"e":"pv","t":2}]"""));

        Assert.True(result.EventsAggregated);
        Assert.Equal("1", hash["n_unknown"]);
        Assert.Equal("1", hash["n_pv"]);
        Assert.Equal("2", hash["n_events_total"]);
    }

    [Fact]
    public void Ky_TimingOnly_CountsKeydowns_AndNeverStoresKeyIdentity()
    {
        var hash = NewHash();

        // Even a hostile envelope smuggling key identity next to the contract's
        // {e,t,d} must leave zero identity traces in the hash.
        Apply(hash, Envelope(
            """[{"e":"ky","t":5,"d":1},{"e":"ky","t":9,"d":0},{"e":"ky","t":12,"d":1,"key":"a","code":"KeyA"}]"""));

        Assert.Equal("2", hash["n_key"]);   // keydowns only (d==1)
        Assert.DoesNotContain("key", hash.Keys);
        Assert.DoesNotContain("code", hash.Keys);
        Assert.DoesNotContain("KeyA", hash.Values);
        Assert.DoesNotContain("a", hash.Values);
    }

    [Fact]
    public void PointerAndClickEvents_FeedCountersFlagsAndClickBeforeRenderIngredient()
    {
        var hash = NewHash();

        Apply(hash, Envelope("""
            [{"e":"pd","t":10,"tr":1,"sn":10,"pt":"m"},
             {"e":"pd","t":20,"tr":0,"sn":20,"pt":"t"},
             {"e":"pd","t":30,"tr":1,"sn":30,"pt":"p"},
             {"e":"cl","t":40,"tr":1,"sn":900,"pt":"m"},
             {"e":"cl","t":50,"tr":1,"sn":35,"pt":"m"},
             {"e":"sc","t":60,"y":250}]
            """));

        Assert.Equal("1", hash["pt_mouse"]);
        Assert.Equal("1", hash["pt_touch"]);
        Assert.Equal("1", hash["pt_pen"]);
        Assert.Equal("1", hash["pointer_untrusted"]);   // the tr:0 pd
        Assert.Equal("2", hash["n_click"]);
        Assert.Equal("1", hash["n_scroll"]);
        Assert.Equal(35, double.Parse(hash["cl_min_sn"])); // min sn = ingredient only
    }

    [Fact]
    public void FormFlow_DerivesFillTimeServerSide_AndSetsFlags()
    {
        var hash = NewHash();

        Apply(hash, Envelope("""
            [{"e":"ff","t":1000,"fh":"ab12cd34","ft":"email"},
             {"e":"pa","t":2000,"fk":"identity"},
             {"e":"af","t":2500,"fh":"ab12cd34"},
             {"e":"hp","t":3000,"kind":"input"},
             {"e":"fs","t":5500,"fh":"ab12cd34"}]
            """));

        Assert.Equal("1", hash["form_started"]);
        Assert.Equal("1", hash["form_submitted"]);
        Assert.Equal(4500, double.Parse(hash["form_fill_ms"])); // fs.t - first_ff_ts
        Assert.Equal("1", hash["paste_identity"]);
        Assert.Equal("1", hash["autofill"]);
        Assert.Equal("1", hash["hp_touched"]);
    }

    [Fact]
    public void MissingSignalsLeaveFieldsAbsent_NeverZero()
    {
        var hash = NewHash();

        Apply(hash, Envelope("[]"));

        string[] mustBeAbsent =
        [
            "mm_n", "mm_mean_ms", "mm_m2", "mm_path_len", "n_pv", "n_click", "n_key",
            "n_scroll", "first_interaction_delay_ms", "storage_age_sec", "storage_sig_ok",
            "visitor_id", "hp_touched", "pointer_untrusted", "form_started", "checksum_ok",
            "integrity_fails", "skew_bad",
        ];
        foreach (var field in mustBeAbsent)
            Assert.DoesNotContain(field, hash.Keys);
        Assert.Equal("1", hash["n_beacons"]);
        Assert.Equal("1", hash["has_beacon"]);
        Assert.Equal(NowMs.ToString(), hash["last_beacon_ts"]);
    }

    [Fact]
    public void PageUrl_FirstNonEmptyRefererWins()
    {
        var hash = NewHash();

        Apply(hash, Envelope("[]", seq: 0), referer: "https://tenant.example/landing");
        Apply(hash, Envelope("[]", seq: 1), referer: "https://tenant.example/other");

        Assert.Equal("https://tenant.example/landing", hash["page_url"]);
    }

    // ----------------------------------------------------------- integrity --

    [Fact]
    public void Checksum_SealedBody_Verifies()
    {
        var hash = NewHash();

        var result = Apply(hash, Seal(Envelope("""[{"e":"pv","t":12}]""")));

        Assert.True(result.EventsAggregated);
        Assert.Equal("1", hash["checksum_ok"]);
        Assert.DoesNotContain("integrity_fails", hash.Keys); // nonce+seq+skew clean too
    }

    [Fact]
    public void Checksum_TamperedBody_FailsAndCountsOneIntegrityFail()
    {
        var hash = NewHash();

        // Flip one byte AFTER sealing (t:12 -> t:13); checksum no longer matches.
        var tampered = Seal(Envelope("""[{"e":"pv","t":12}]""")).Replace("\"t\":12", "\"t\":13");
        Apply(hash, tampered);

        Assert.Equal("0", hash["checksum_ok"]);
        Assert.Equal("1", hash["integrity_fails"]);
    }

    [Fact]
    public void Checksum_AbsentCField_LeavesFieldAbsent_NoFailure()
    {
        var hash = NewHash();

        Apply(hash, Envelope("""[{"e":"pv","t":12}]""")); // pre-SDK-05: no "c"

        Assert.DoesNotContain("checksum_ok", hash.Keys);   // missing != zero
        Assert.DoesNotContain("integrity_fails", hash.Keys);
    }

    [Fact]
    public void Nonce_WrongOrMissingStoredNonce_RecordsMismatch()
    {
        var wrongHash = NewHash();
        Apply(wrongHash, Envelope("[]", nonce: "ffffffffffffffffffffffffffffffff"));
        Assert.Equal("0", wrongHash["nonce_ok"]);
        Assert.Equal("1", wrongHash["integrity_fails"]);

        var expiredHash = NewHash();
        Apply(expiredHash, Envelope("[]"), expectedNonce: null); // nonce TTL expired
        Assert.Equal("0", expiredHash["nonce_ok"]);

        var okHash = NewHash();
        Apply(okHash, Envelope("[]"));
        Assert.Equal("1", okHash["nonce_ok"]);
        Assert.DoesNotContain("integrity_fails", okHash.Keys);
    }

    [Fact]
    public void Seq_FirstEnvelopeSeqZero_IsAccepted_NotAReplay()
    {
        var hash = NewHash();

        var result = Apply(hash, Envelope("""[{"e":"pv","t":1}]""", seq: 0));

        Assert.True(result.EventsAggregated);   // last_seq absent => treated as -1
        Assert.Equal("0", hash["last_seq"]);
        Assert.Equal("1", hash["n_pv"]);
        Assert.DoesNotContain("seq_replays", hash.Keys);
        Assert.DoesNotContain("seq_gaps", hash.Keys);
    }

    [Fact]
    public void Seq_Replay_DropsEvents_CountsReplay_StillUpdatesLastBeaconTs()
    {
        var hash = NewHash();

        Apply(hash, Envelope("""[{"e":"pv","t":1}]""", seq: 0));
        var replay = Apply(hash, Envelope("""[{"e":"pv","t":2}]""", seq: 0), nowMs: NowMs + 5000);

        Assert.False(replay.EventsAggregated);
        Assert.Equal("1", hash["n_pv"]);                       // second batch NOT aggregated
        Assert.Equal("1", hash["seq_replays"]);
        Assert.Equal("0", hash["last_seq"]);
        Assert.Equal((NowMs + 5000).ToString(), hash["last_beacon_ts"]);
    }

    [Fact]
    public void Seq_Gap_ZeroThenThree_RecordsTwoMissing()
    {
        var hash = NewHash();

        Apply(hash, Envelope("[]", seq: 0));
        Apply(hash, Envelope("[]", seq: 3));

        Assert.Equal("2", hash["seq_gaps"]);
        Assert.Equal("3", hash["last_seq"]);
    }

    [Fact]
    public void Skew_Beyond120s_SetsSkewBad_AndMaxTracksTheWorst()
    {
        var hash = NewHash();

        Apply(hash, Envelope("[]", seq: 0, sentAt: NowMs - 200_000));

        Assert.Equal("1", hash["skew_bad"]);
        Assert.Equal("200000", hash["skew_max_ms"]);
        // A wrong device clock is its own T3 signal (ClockSkewBad), NEVER an integrity
        // failure: integrity_fails feeds the beacon_integrity_failed T1 floor of 85.
        Assert.DoesNotContain("integrity_fails", hash.Keys);

        Apply(hash, Envelope("[]", seq: 1, sentAt: NowMs - 50));
        Assert.Equal("200000", hash["skew_max_ms"]);   // max survives a clean POST
        Assert.Equal("1", hash["skew_bad"]);           // sticky once tripped
        Assert.DoesNotContain("integrity_fails", hash.Keys);
    }

    [Fact]
    public void Skew_WithinBounds_LeavesSkewBadAbsent_AndNoIntegrityFailure()
    {
        var hash = NewHash();

        Apply(hash, Envelope("[]", seq: 0, sentAt: NowMs - 50));

        Assert.DoesNotContain("skew_bad", hash.Keys);   // missing ≠ zero: absent = never tripped
        Assert.Equal("50", hash["skew_max_ms"]);
        Assert.DoesNotContain("integrity_fails", hash.Keys);
    }

    [Fact]
    public void IntegrityFails_IsAStoredCounter_TwoConditionsInOnePost_ThenCleanPostLeavesIt()
    {
        var hash = NewHash();

        // Nonce mismatch AND checksum mismatch in the same POST => exactly +2.
        var tampered = Seal(Envelope("""[{"e":"pv","t":12}]""", nonce: "ffffffffffffffffffffffffffffffff"))
            .Replace("\"t\":12", "\"t\":13");
        Apply(hash, tampered);
        Assert.Equal("2", hash["integrity_fails"]);

        // Subsequent clean sealed POST: unchanged.
        Apply(hash, Seal(Envelope("""[{"e":"pv","t":12}]""", seq: 1)));
        Assert.Equal("2", hash["integrity_fails"]);
        Assert.Equal("1", hash["checksum_ok"]);
        Assert.Equal("1", hash["nonce_ok"]);
    }

    // ------------------------------------------------------------------ fp --

    private static string FpEvent(string? storageJson = null, string vid = "v-abc123")
    {
        var storage = storageJson is null ? "" : $",\"storage\":{storageJson}";
        return $$"""
            [{"e":"fp","t":700,"vid":"{{vid}}","conf":0.87,"scr":[1920,1080,24,2],
              "tz":"Europe/Stockholm","langs":["sv-SE","en-US"],"canvasBlocked":false,
              "touch":false,"mob":false,"wd":true,"botd":{"bot":true,"kind":"headless_chrome"}{{storage}}}]
            """;
    }

    [Fact]
    public void Fp_MapsDeviceFields_AndHasNoHeadlessEmulatorModalityFields()
    {
        var hash = NewHash();

        Apply(hash, Envelope(FpEvent(
            """{"ck":{"present":false},"ls":{"present":false},"fresh":true,"cookiesDisabled":true}""")));

        Assert.Equal("v-abc123", hash["visitor_id"]);
        Assert.Equal(0.87, double.Parse(hash["conf"]));
        Assert.Equal("1920", hash["screen_w"]);
        Assert.Equal("1080", hash["screen_h"]);
        Assert.Equal("24", hash["color_depth"]);
        Assert.Equal("2", hash["dpr"]);
        Assert.Equal("Europe/Stockholm", hash["tz"]);
        Assert.Equal("sv-SE,en-US", hash["langs"]);
        Assert.Equal("0", hash["canvas_blocked"]);
        Assert.Equal("0", hash["touch"]);
        Assert.Equal("0", hash["is_mobile"]);
        Assert.Equal("1", hash["webdriver"]);
        Assert.Equal("1", hash["botd_bot"]);
        Assert.Equal("headless_chrome", hash["botd_kind"]);
        Assert.Equal("1", hash["cookies_disabled"]);

        // §7: the engine derives; these are NOT wire fields and must not exist.
        Assert.DoesNotContain("headless", hash.Keys);
        Assert.DoesNotContain("emulator", hash.Keys);
        Assert.DoesNotContain("modality", hash.Keys);
        // No ck/ls pair carried ts+sig => no storage verdict at all.
        Assert.DoesNotContain("storage_sig_ok", hash.Keys);
        Assert.DoesNotContain("storage_age_sec", hash.Keys);
    }

    [Fact]
    public void Storage_ValidCkPair_VerifiesAndComputesServerSideAge()
    {
        var hash = NewHash();
        var ts = NowMs - 5000; // minted 5 s ago
        var sig = SessionAggregator.ComputeStorageSig(Secret, Tid, ts);

        Apply(hash, Envelope(FpEvent(
            $$"""{"ck":{"present":true,"ts":{{ts}},"sig":"{{sig}}","ageMs":999999},"ls":{"present":false},"fresh":false,"cookiesDisabled":false}""")));

        Assert.Equal("1", hash["storage_sig_ok"]);
        Assert.Equal("5", hash["storage_age_sec"]); // (nowMs - ts)/1000 — NEVER the client ageMs
    }

    [Fact]
    public void Storage_TamperedSig_FailsAndLeavesAgeAbsent()
    {
        var hash = NewHash();
        var ts = NowMs - 5000;

        Apply(hash, Envelope(FpEvent(
            $$"""{"ck":{"present":true,"ts":{{ts}},"sig":"deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef"},"ls":{"present":false},"fresh":false,"cookiesDisabled":false}""")));

        Assert.Equal("0", hash["storage_sig_ok"]);
        Assert.DoesNotContain("storage_age_sec", hash.Keys); // missing != zero
    }

    [Fact]
    public void Storage_CkAbsent_FallsBackToValidLsPair()
    {
        var hash = NewHash();
        var ts = NowMs - 120_000;
        var sig = SessionAggregator.ComputeStorageSig(Secret, Tid, ts);

        Apply(hash, Envelope(FpEvent(
            $$"""{"ck":{"present":false},"ls":{"present":true,"ts":{{ts}},"sig":"{{sig}}"},"fresh":false,"cookiesDisabled":false}""")));

        Assert.Equal("1", hash["storage_sig_ok"]);
        Assert.Equal("120", hash["storage_age_sec"]);
    }

    [Fact]
    public void Storage_ZeroAge_VerifiedPair_YieldsAgeZero()
    {
        var hash = NewHash();
        var sig = SessionAggregator.ComputeStorageSig(Secret, Tid, NowMs);

        Apply(hash, Envelope(FpEvent(
            $$"""{"ck":{"present":true,"ts":{{NowMs}},"sig":"{{sig}}"},"ls":{"present":false},"fresh":true,"cookiesDisabled":false}""")));

        Assert.Equal("1", hash["storage_sig_ok"]);
        Assert.Equal("0", hash["storage_age_sec"]); // the fpz / storage_age_zero_repeat feed
    }
}
