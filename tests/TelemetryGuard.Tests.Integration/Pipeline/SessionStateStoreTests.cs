using StackExchange.Redis;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.RiskEngine.Pipeline;
using Testcontainers.Redis;

namespace TelemetryGuard.Tests.Integration.Pipeline;

/// <summary>Shared Redis container + multiplexer (same fixture pattern as RSK-03's
/// velocity-store tests).</summary>
public sealed class SessionStateRedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .Build();

    public IConnectionMultiplexer Mux { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var options = ConfigurationOptions.Parse(_container.GetConnectionString());
        options.AllowAdmin = true; // for the read-only keyspace snapshot assertions
        Mux = await ConnectionMultiplexer.ConnectAsync(options);
    }

    public async Task DisposeAsync()
    {
        Mux?.Dispose();
        await _container.DisposeAsync();
    }
}

/// <summary>
/// RSK-07 reader-contract tests: seed t:{tid}:click:{sid} exactly as API-02 step 3.7
/// writes it and t:{tid}:sess:{sid} exactly as API-04 step 6 writes it (flat fields,
/// invariant-culture strings) and assert the typed mapping, the null cases, the
/// read-only behavior, and tenant-prefix usage.
/// </summary>
public sealed class SessionStateStoreTests : IClassFixture<SessionStateRedisFixture>
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb");

    private readonly SessionStateRedisFixture _fixture;

    public SessionStateStoreTests(SessionStateRedisFixture fixture) => _fixture = fixture;

    private sealed class FakeTenantContext(Guid tenant) : ITenantContext
    {
        public TenantId TenantId { get; } = new(tenant);
        public string? SiteKey => null;
        public bool IsResolved => true;
        public IReadOnlyList<string> Scopes => Array.Empty<string>();
    }

    private RedisSessionStateStore Store(Guid? tenant = null)
        => new(_fixture.Mux, new FakeTenantContext(tenant ?? TenantA));

    private IDatabase Db => _fixture.Mux.GetDatabase();

    private static string Sid() => $"s{Guid.NewGuid():N}";

    private static string TenantPrefix(Guid tenant) => $"t:{new TenantId(tenant)}";

    /// <summary>Click-context hash exactly as API-02 step 3.7 / TrackerEndpoints writes it
    /// (empty string = absent).</summary>
    private static HashEntry[] ClickHash(
        string clickIdInvalid = "0", string clickId = "EAIaIQobChMIabc123", string clickIdType = "gclid")
        =>
        [
            new("kind", "tracker"),
            new("ts", "1754000000000"),
            new("ip", "203.0.113.9"),
            new("ua", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36"),
            new("ch_ua", "\"Chromium\";v=\"125\", \"Google Chrome\";v=\"125\""),
            new("ch_mobile", "?0"),
            new("ch_platform", "\"Windows\""),
            new("accept_language", "de-DE,de;q=0.9"),
            new("referrer", ""), // absent — encoded as empty string by the producer
            new("header_order", "Host,Connection,User-Agent,Accept,Accept-Language"),
            new("site_key", "sk_live_test"),
            new("campaign_id", "11111111-2222-4333-8444-555555555555"),
            new("click_id_type", clickIdType),
            new("click_id", clickId),
            new("click_id_invalid", clickIdInvalid),
        ];

    /// <summary>Aggregate session hash exactly as API-04 step 6 writes it: running
    /// aggregates only, invariant-culture numeric strings, absent signals leave fields
    /// absent (missing ≠ zero).</summary>
    private static HashEntry[] SessHash() =>
    [
        new("n_beacons", "3"),
        new("n_pv", "2"),
        new("mm_n", "2"),
        new("mm_prev_ts", "4300"),
        new("mm_mean_ms", "150.5"),
        new("mm_m2", "5000.25"),
        new("mm_path_len", "412.75"),
        new("mm_first_x", "10.5"),
        new("mm_first_y", "20.25"),
        new("mm_prev_x", "410.5"),
        new("mm_prev_y", "220.25"),
        new("n_click", "3"),
        new("n_key", "5"),
        new("n_scroll", "2"),
        new("n_events_total", "42"),
        new("pt_mouse", "12"),
        new("pt_touch", "1"),
        new("hp_touched", "0"),
        new("pointer_untrusted", "0"),
        new("first_interaction_delay_ms", "450"),
        new("visitor_id", "v-fpjs-abc"),
        new("webdriver", "0"),
        new("botd_bot", "0"),
        new("screen_w", "1920"),
        new("screen_h", "1080"),
        new("dpr", "1.25"),
        new("canvas_blocked", "0"),
        new("tz", "Europe/Berlin"),
        new("langs", "de-DE,en-US"),
        new("cookies_disabled", "0"),
        new("storage_age_sec", "86400.5"),
        new("form_started", "1"),
        new("form_submitted", "1"),
        new("form_fill_ms", "2500"),
        new("autofill", "0"),
        new("paste_identity", "1"),
        new("last_seq", "2"),
        new("has_beacon", "1"),
        new("nav_ts", "1754000000000"),
        new("last_beacon_ts", "1754000045000"),
    ];

    [Fact]
    public async Task BothHashes_MapToTypedClickStateAndAggregateBeaconData()
    {
        var sid = Sid();
        var prefix = TenantPrefix(TenantA);
        await Db.HashSetAsync($"{prefix}:click:{sid}", ClickHash());
        await Db.HashSetAsync($"{prefix}:sess:{sid}", SessHash());

        var state = await Store().GetAsync(sid, CancellationToken.None);

        Assert.NotNull(state);
        Assert.Equal("11111111-2222-4333-8444-555555555555", state.CampaignId);

        // --- ClickState (API-02 step 3.7 contract) ---
        var click = state.Click;
        Assert.NotNull(click);
        Assert.Equal("203.0.113.9", click.Ip);
        Assert.StartsWith("Mozilla/5.0 (Windows NT 10.0", click.UserAgent);
        Assert.True(click.IsPaidClick); // kind == "tracker"
        Assert.Equal("gclid", click.ClickIdType);
        Assert.Equal("EAIaIQobChMIabc123", click.ClickId);
        Assert.True(click.ClickIdFresh); // click_id present + click_id_invalid=0
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_754_000_000_000), click.Timestamp);
        Assert.Null(click.TlsFingerprint);
        Assert.Equal("de-DE,de;q=0.9", click.Headers["Accept-Language"]);
        Assert.Equal("\"Windows\"", click.Headers["Sec-CH-UA-Platform"]);
        Assert.Equal("?0", click.Headers["Sec-CH-UA-Mobile"]);
        Assert.False(click.Headers.ContainsKey("Referer")); // empty string = absent

        // --- BeaconData (API-04 step 6 aggregates, spot asserts) ---
        var beacon = state.Beacon;
        Assert.NotNull(beacon);
        Assert.Equal(2, beacon.MmN);
        Assert.Equal(150.5, beacon.MmMeanMs);
        Assert.Equal(5000.25, beacon.MmM2);
        Assert.Equal(412.75, beacon.MmPathLen);
        Assert.Equal(10.5f, beacon.FirstX);
        Assert.Equal(20.25f, beacon.FirstY);
        Assert.Equal(410.5f, beacon.PrevX);
        Assert.Equal(220.25f, beacon.PrevY);
        Assert.Equal(3, beacon.ClickCount);
        Assert.Equal(5, beacon.KeyCount);
        Assert.Equal(2, beacon.ScrollEventCount);
        Assert.Equal(1, beacon.TouchCount); // pt_touch (API-04's shipped spelling)
        Assert.Equal(450, beacon.FirstInteractionDelayMs);
        Assert.Equal("v-fpjs-abc", beacon.VisitorId);
        Assert.False(beacon.WebdriverFlag);
        Assert.False(beacon.HeadlessBrowser);
        Assert.False(beacon.HoneypotTouched);
        Assert.False(beacon.PointerUntrusted);
        Assert.True(beacon.IntegrityOk); // integrity_fails absent → intact
        Assert.Equal(1920, beacon.ScreenWidth);
        Assert.Equal(1080, beacon.ScreenHeight);
        Assert.Equal(1.25, beacon.DevicePixelRatio);
        Assert.True(beacon.CookiesEnabled); // cookies_disabled=0
        Assert.False(beacon.CanvasFpBlocked);
        Assert.Equal("Europe/Berlin", beacon.Timezone);
        Assert.Equal("de-DE", beacon.Language); // first entry of langs
        Assert.Equal(86_400.5, beacon.StorageAgeSec);
        Assert.Equal(45_000, beacon.SessionDurationMs); // last_beacon_ts − nav_ts
        Assert.Equal(2, beacon.PagesViewed); // n_pv
        Assert.True(beacon.FormSubmitted);
        Assert.True(beacon.PasteInIdentityFields);
        Assert.False(beacon.AutofillDetected);
        // form_fill_ms reconstructed as (FirstFocus=0, Submit=fill_ms):
        Assert.Equal(0, beacon.FormFirstFocusTMs);
        Assert.Equal(2500, beacon.FormSubmitTMs);
        // No producer for viewport dims:
        Assert.Null(beacon.ViewportWidth);
        Assert.Null(beacon.ViewportHeight);
    }

    [Fact]
    public async Task ReplayedClickId_MapsToClickIdFreshFalse()
    {
        var sid = Sid();
        await Db.HashSetAsync($"{TenantPrefix(TenantA)}:click:{sid}", ClickHash(clickIdInvalid: "1"));

        var state = await Store().GetAsync(sid, CancellationToken.None);

        Assert.False(state!.Click!.ClickIdFresh);
    }

    [Fact]
    public async Task NoClickId_MapsToClickIdFreshNull()
    {
        var sid = Sid();
        await Db.HashSetAsync($"{TenantPrefix(TenantA)}:click:{sid}",
            ClickHash(clickIdInvalid: "1", clickId: "", clickIdType: ""));

        var state = await Store().GetAsync(sid, CancellationToken.None);

        Assert.Null(state!.Click!.ClickId);
        Assert.Null(state.Click.ClickIdType);
        Assert.Null(state.Click.ClickIdFresh); // nothing to judge without a click id
    }

    [Fact]
    public async Task IntegrityFails_NonZero_MapsToIntegrityOkFalse()
    {
        var sid = Sid();
        var entries = SessHash().Append(new HashEntry("integrity_fails", "2")).ToArray();
        await Db.HashSetAsync($"{TenantPrefix(TenantA)}:sess:{sid}", entries);

        var state = await Store().GetAsync(sid, CancellationToken.None);

        Assert.False(state!.Beacon!.IntegrityOk);
    }

    [Fact]
    public async Task SparseSessionHash_AbsentFieldsMapToNaNOrNullDefaults_NeverZero()
    {
        var sid = Sid();
        await Db.HashSetAsync($"{TenantPrefix(TenantA)}:sess:{sid}",
            [new HashEntry("visitor_id", "v-sparse"), new HashEntry("n_beacons", "1")]);

        var beacon = (await Store().GetAsync(sid, CancellationToken.None))!.Beacon!;

        Assert.Equal("v-sparse", beacon.VisitorId);
        Assert.Equal(0, beacon.MmN);
        Assert.True(double.IsNaN(beacon.MmMeanMs));
        Assert.True(double.IsNaN(beacon.MmM2));
        Assert.True(float.IsNaN(beacon.FirstX));
        Assert.Null(beacon.FirstInteractionDelayMs);
        Assert.Null(beacon.StorageAgeSec);       // HMAC did not verify → absent → null
        Assert.Null(beacon.ScreenWidth);
        Assert.Null(beacon.DevicePixelRatio);
        Assert.Null(beacon.Timezone);
        Assert.Null(beacon.Language);
        Assert.True(beacon.IntegrityOk);         // integrity_fails absent
        Assert.True(beacon.CookiesEnabled);      // record default
        Assert.Equal(1, beacon.PagesViewed);     // record default
        Assert.True(double.IsNaN(beacon.SessionDurationMs)); // missing timing is unknown, not zero dwell
        Assert.Null(beacon.FormFirstFocusTMs);
        Assert.Null(beacon.FormSubmitTMs);
    }

    [Fact]
    public async Task ClickHashOnly_BeaconIsNull()
    {
        var sid = Sid();
        await Db.HashSetAsync($"{TenantPrefix(TenantA)}:click:{sid}", ClickHash());

        var state = await Store().GetAsync(sid, CancellationToken.None);

        Assert.NotNull(state);
        Assert.NotNull(state.Click);
        Assert.Null(state.Beacon);
        Assert.Equal("11111111-2222-4333-8444-555555555555", state.CampaignId);
    }

    [Fact]
    public async Task SessHashOnly_ClickIsNull()
    {
        var sid = Sid();
        await Db.HashSetAsync($"{TenantPrefix(TenantA)}:sess:{sid}", SessHash());

        var state = await Store().GetAsync(sid, CancellationToken.None);

        Assert.NotNull(state);
        Assert.Null(state.Click);
        Assert.NotNull(state.Beacon);
        Assert.Null(state.CampaignId);
    }

    [Fact]
    public async Task NeitherHash_ReturnsNull()
    {
        var state = await Store().GetAsync(Sid(), CancellationToken.None);
        Assert.Null(state);
    }

    [Fact]
    public async Task GetAsync_PerformsNoRedisWrites()
    {
        var sid = Sid();
        var prefix = TenantPrefix(TenantA);
        await Db.HashSetAsync($"{prefix}:click:{sid}", ClickHash());
        await Db.HashSetAsync($"{prefix}:sess:{sid}", SessHash());

        var server = _fixture.Mux.GetServer(_fixture.Mux.GetEndPoints()[0]);
        var keysBefore = server.Keys(pattern: "*").Select(k => k.ToString()).OrderBy(k => k).ToArray();
        var clickBefore = (await Db.HashGetAllAsync($"{prefix}:click:{sid}")).OrderBy(e => e.Name.ToString()).ToArray();
        var sessBefore = (await Db.HashGetAllAsync($"{prefix}:sess:{sid}")).OrderBy(e => e.Name.ToString()).ToArray();

        await Store().GetAsync(sid, CancellationToken.None);
        await Store().GetAsync(Sid(), CancellationToken.None); // unknown session read too

        var keysAfter = server.Keys(pattern: "*").Select(k => k.ToString()).OrderBy(k => k).ToArray();
        var clickAfter = (await Db.HashGetAllAsync($"{prefix}:click:{sid}")).OrderBy(e => e.Name.ToString()).ToArray();
        var sessAfter = (await Db.HashGetAllAsync($"{prefix}:sess:{sid}")).OrderBy(e => e.Name.ToString()).ToArray();

        Assert.Equal(keysBefore, keysAfter);
        Assert.Equal(clickBefore, clickAfter);
        Assert.Equal(sessBefore, sessAfter);
    }

    [Fact]
    public async Task TenantPrefix_IsolatesTenants()
    {
        var sid = Sid();
        await Db.HashSetAsync($"{TenantPrefix(TenantA)}:click:{sid}", ClickHash());
        await Db.HashSetAsync($"{TenantPrefix(TenantA)}:sess:{sid}", SessHash());

        // Same session id read under tenant B must see nothing (D11 key prefixing).
        var other = await Store(TenantB).GetAsync(sid, CancellationToken.None);
        Assert.Null(other);

        var own = await Store(TenantA).GetAsync(sid, CancellationToken.None);
        Assert.NotNull(own);
    }
}
