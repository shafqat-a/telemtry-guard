using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Api.Options;
using TelemetryGuard.Api.Services;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Core.Time;
using TelemetryGuard.Data.Repositories;
using TelemetryGuard.Data.Tenancy;
using TelemetryGuard.RiskEngine.Velocity;

namespace TelemetryGuard.Api.Endpoints;

/// <summary>
/// API-04: the beacon ingestion pair (spec §4 components 1+3, §7).
///
/// GET /i/init — session bootstrap. navigator.sendBeacon is fire-and-forget
/// (the page can NEVER read its response), so every server-issued value the SDK
/// needs (integrity nonce, signed storage timestamp) comes from this separate
/// readable fetch. No cookies, no credentialed CORS: SDK-02 calls with
/// credentials:'omit', so a Set-Cookie here would never be stored — persistence
/// of the {storageTs, storageSig} pair is ENTIRELY client-side (SDK-04's
/// first-party tg_fp cookie + localStorage on the landing-page domain).
///
/// POST /i — accepts SDK envelopes as text/plain (sendBeacon "simple request",
/// NO CORS preflight may ever be required) or application/json (fetch
/// fallback), validates, and INCREMENTALLY aggregates per-session behavioral
/// statistics into the Redis hash t:{tid}:sess:{sid} (running aggregates only —
/// never raw event points; RSK-04/RSK-07 extract every behavioral feature from
/// the hash alone). Always 204 (413 only over the body cap): validation
/// failures are silent — bots get no oracle.
///
/// Tenant resolution: DAT-04's middleware deliberately passes /i and /i/init
/// through UNRESOLVED (it must not buffer bodies); both handlers resolve the
/// ?k= site key themselves via ITenantResolver and stamp the scoped
/// TenantContext so downstream scoped services (velocity store, repositories)
/// see it. Unknown site keys get a success-shaped 204 drop (anti-probing).
/// </summary>
public static partial class BeaconEndpoints
{
    // Accepts BOTH sid shapes that exist by design: the 32-lowercase-hex ids
    // minted by the /c tracker (API-02) and the crypto.randomUUID() fallback
    // (36 chars incl. dashes) SDK-02 generates for organic sessions (§6.2).
    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex SidShape();

    public static IEndpointRouteBuilder MapBeaconEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/i/init", HandleInitAsync);
        app.MapPost("/i", HandleBeaconAsync);
        return app;
    }

    // ------------------------------------------------------------ /i/init --

    private static async Task<IResult> HandleInitAsync(
        HttpContext ctx, ITenantResolver resolver, TenantContext tenantContext,
        IConnectionMultiplexer redis, IClock clock, IOptions<BeaconOptions> beaconOpts,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var opts = beaconOpts.Value;

        // CORS: plain wildcard — no credentials are ever used, so '*' is correct
        // and simplest. Deliberately NO Set-Cookie, NO Origin echo, NO
        // Allow-Credentials anywhere in this handler.
        ctx.Response.Headers.AccessControlAllowOrigin = "*";
        ctx.Response.Headers.CacheControl = "no-store";

        // Tenant resolution, first thing (the middleware passed us through).
        var k = ctx.Request.Query["k"].ToString();
        var resolved = string.IsNullOrEmpty(k)
            ? null
            : await resolver.ResolveSiteKeyAsync(k, ct);
        if (resolved is null)
            return Results.NoContent(); // success-shaped drop; SDK treats non-200 as "disabled"
        tenantContext.Resolve(new TenantId(resolved.TenantId), resolved.SiteKey);
        var tid = resolved.TenantId.ToString("D");

        var sid = ctx.Request.Query["sid"].ToString();
        if (!SidShape().IsMatch(sid))
            return Results.NoContent();

        try
        {
            // Nonce: 16 random bytes, lowercase hex; SETEX t:{tid}:nonce:{sid}.
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            await redis.GetDatabase().StringSetAsync(
                $"t:{tid}:nonce:{sid}", nonce, TimeSpan.FromSeconds(opts.NonceTtlSeconds), When.Always);

            // Fresh storage pair on every call; SDK-04 never overwrites an existing
            // valid client-side pair, so age accumulates without any server echo.
            // NOT sid-bound — storage outlives sessions.
            var storageTs = clock.UtcNow.ToUnixTimeMilliseconds();
            var storageSig = SessionAggregator.ComputeStorageSig(opts.HmacSecret, tid, storageTs);

            return Results.Json(new { nonce, storageTs, storageSig });
        }
        catch (Exception ex)
        {
            Log(loggerFactory, ex, "/i/init bootstrap failed for tenant {TenantId}", tid);
            return Results.NoContent(); // SDK degrades gracefully (empty nonce -> integrity signal)
        }
    }

    // ------------------------------------------------------------------ /i --

    private static async Task<IResult> HandleBeaconAsync(
        HttpContext ctx, ITenantResolver resolver, TenantContext tenantContext,
        IConnectionMultiplexer redis, IClock clock, IEventSink sink, IMemoryCache cache,
        IOptions<BeaconOptions> beaconOpts, IOptions<RetentionOptions> retentionOpts,
        IConfiguration config, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var opts = beaconOpts.Value;

        // Tenant resolution, first thing. Unknown key -> success-shaped 204,
        // indistinguishable in status from an accepted beacon (anti-probing).
        var k = ctx.Request.Query["k"].ToString();
        var resolved = string.IsNullOrEmpty(k)
            ? null
            : await resolver.ResolveSiteKeyAsync(k, ct);
        if (resolved is null)
            return Results.NoContent();
        tenantContext.Resolve(new TenantId(resolved.TenantId), resolved.SiteKey);
        var tid = resolved.TenantId.ToString("D");

        // Body cap: 413 is the ONLY non-204 status this endpoint produces.
        if (ctx.Request.ContentLength > opts.MaxBodyBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        var sizeFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
            sizeFeature.MaxRequestBodySize = opts.MaxBodyBytes;

        // UTF-8 JSON regardless of declared content type (text/plain keeps
        // sendBeacon a CORS "simple request"; application/json is the fetch
        // fallback; absent is tolerated). Never demand custom headers.
        string rawBody;
        try
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            rawBody = await reader.ReadToEndAsync(ct);
        }
        catch (BadHttpRequestException)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); // cap hit mid-read
        }
        if (Encoding.UTF8.GetByteCount(rawBody) > opts.MaxBodyBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        // Everything after body-read: failures are logged and swallowed — 204 always.
        try
        {
            JsonElement body;
            try
            {
                body = JsonSerializer.Deserialize<JsonElement>(rawBody);
            }
            catch (JsonException)
            {
                Log(loggerFactory, null, "Beacon drop: unparsable JSON for tenant {TenantId}", tid);
                return Results.NoContent();
            }

            // Validation (silent drops). seq >= 0: SDK-02 starts at 0 — the first
            // envelope of every session is seq:0 and MUST be accepted.
            if (body.ValueKind != JsonValueKind.Object
                || !body.TryGetProperty("sid", out var sidEl) || sidEl.ValueKind != JsonValueKind.String
                || sidEl.GetString() is not { } sid || !SidShape().IsMatch(sid)
                || (body.TryGetProperty("k", out var bodyK)
                    && bodyK.ValueKind == JsonValueKind.String
                    && !string.Equals(bodyK.GetString(), k, StringComparison.Ordinal))
                || !body.TryGetProperty("seq", out var seqEl) || seqEl.ValueKind != JsonValueKind.Number
                || !seqEl.TryGetInt64(out var seq) || seq < 0
                || !body.TryGetProperty("events", out var eventsEl)
                || eventsEl.ValueKind != JsonValueKind.Array)
            {
                Log(loggerFactory, null, "Beacon drop: envelope validation failed for tenant {TenantId}", tid);
                return Results.NoContent();
            }

            var db = redis.GetDatabase();
            var now = clock.UtcNow;
            var nowMs = now.ToUnixTimeMilliseconds();
            var sessKey = $"t:{tid}:sess:{sid}";

            // Load-modify-store (single-instance MVP): beacons for one session
            // arrive serially from one browser, so read-modify-write is
            // acceptable. A Lua script is the multi-instance upgrade path.
            var stored = await db.HashGetAllAsync(sessKey);
            var hash = new Dictionary<string, string>(stored.Length, StringComparer.Ordinal);
            foreach (var entry in stored)
                hash[entry.Name.ToString()] = entry.Value.ToString();

            var storedNonce = await db.StringGetAsync($"t:{tid}:nonce:{sid}");
            var referer = ctx.Request.Headers.Referer.ToString();

            var result = SessionAggregator.Apply(
                hash, body, rawBody, nowMs, opts, tid,
                storedNonce.IsNullOrEmpty ? null : storedNonce.ToString(),
                string.IsNullOrEmpty(referer) ? null : referer);

            var entries = new HashEntry[hash.Count];
            var i = 0;
            foreach (var (name, value) in hash)
                entries[i++] = new HashEntry(name, value);
            await db.HashSetAsync(sessKey, entries);
            await db.KeyExpireAsync(sessKey, TimeSpan.FromSeconds(opts.SessionTtlSeconds));

            // Site liveness for D22 dashboards (exposed by API-07).
            await db.StringSetAsync(
                $"t:{tid}:site:{k}:lastbeacon",
                now.ToUnixTimeSeconds(),
                TimeSpan.FromSeconds(604800),
                When.Always);

            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
            var ua = NullIfEmpty(ctx.Request.Headers.UserAgent.ToString());

            // Velocity session capture (RSK-03) — once per ACCEPTED batch, resolved
            // AFTER the tenant context is stamped (scoped, tenant-ambient).
            // storageAgeZero only when the storage sig VERIFIED and age is 0 —
            // feeds the fpz zero-storage-age repeat counter (storage_age_zero_repeat).
            if (result.EventsAggregated)
            {
                var velocity = ctx.RequestServices.GetRequiredService<IVelocityStore>();
                var visitorId = hash.TryGetValue("visitor_id", out var vid) && vid.Length > 0 ? vid : null;
                var storageAgeZero =
                    hash.TryGetValue("storage_sig_ok", out var sigOk) && sigOk == "1"
                    && hash.TryGetValue("storage_age_sec", out var age) && age == "0";
                await velocity.RecordSessionAsync(ip, ua, visitorId, sid, storageAgeZero, ct);
            }

            // Periodic sink snapshot: first beacon, fp-bearing, fs-bearing, or
            // every Nth. Sink enqueue only — never blocks on ClickHouse (ANA-03).
            var nBeacons = SessionAggregator.GetLong(hash, "n_beacons") ?? 0;
            if (nBeacons == 1 || result.SawFp || result.SawFs
                || (opts.SinkEveryNthBeacon > 0 && nBeacons % opts.SinkEveryNthBeacon == 0))
            {
                var retentionDays = await ResolveRetentionDaysAsync(
                    ctx, cache, retentionOpts.Value, tid, ct);
                var evt = BuildSnapshot(
                    tenantContext.TenantId, k, sid, hash, ip, ua,
                    ctx.Request.Headers.Select(h => h.Key).ToArray(),
                    retentionDays, now.UtcDateTime);
                await sink.WriteBatchAsync(new[] { evt }, ct);
            }

            // Synthetic-bot labeling (SDK-06 contract, D18 "guaranteed positives"):
            // Development-only flag; in any other config the header is IGNORED.
            if (config.GetValue("Synthetic:Enabled", false)
                && ctx.Request.Headers.ContainsKey("X-TG-Synthetic"))
            {
                var labelSink = ctx.RequestServices.GetService<ILabelSink>();
                if (labelSink is not null)
                {
                    var claimed = await db.StringSetAsync(
                        $"t:{tid}:synth:{sid}", "1", TimeSpan.FromSeconds(3600), When.NotExists);
                    if (claimed)
                    {
                        await labelSink.WriteAsync(new LabelEvent(
                            tenantContext.TenantId, sid, LabelValues.Fraud,
                            LabelSources.SyntheticBot, now.UtcDateTime), ct);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log(loggerFactory, ex, "Beacon aggregation failed for tenant {TenantId}; dropping (204 regardless)", tid);
        }

        return Results.NoContent();
    }

    // ------------------------------------------------------------- helpers --

    /// <summary>RetentionDays denormalized from tenant config at ingest (D20),
    /// cached 60 s — same pattern as API-02 step 3.9. Dapper repo on the
    /// tenant-stamped connection; resolved AFTER the tenant context is set.</summary>
    private static async Task<ushort> ResolveRetentionDaysAsync(
        HttpContext ctx, IMemoryCache cache, RetentionOptions retention, string tid, CancellationToken ct)
    {
        var record = await cache.GetOrCreateAsync($"tenantcfg:{tid}", e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
            return ctx.RequestServices.GetRequiredService<ITenantRepository>().GetCurrentAsync(ct);
        });
        return (ushort)(record?.RetentionDays ?? retention.DefaultDays);
    }

    /// <summary>Maps the updated session aggregates onto ANA-01's ClickEvent
    /// (kind beacon). Only fields with a defined mapping are set; unmapped
    /// extras stay in the Redis hash only. Feature finals (std, linearity,
    /// modality mismatch, headless tiers) are RSK-04's — never computed here.
    /// Missing ≠ zero: absent aggregates stay NaN/null on the event.</summary>
    private static ClickEvent BuildSnapshot(
        TenantId tenantId, string siteKey, string sid, Dictionary<string, string> h,
        string ip, string? ua, string[] headerNames, ushort retentionDays, DateTime nowUtc)
    {
        return new ClickEvent
        {
            TenantId = tenantId,
            SiteKey = siteKey,
            SessionId = sid,
            Kind = EventKind.Beacon,
            Ip = ip,
            HeaderNames = headerNames,
            UserAgent = ua,
            Referrer = Str(h, "page_url"),
            HasJsBeacon = true,
            BeaconIntegrityOk = (SessionAggregator.GetLong(h, "integrity_fails") ?? 0) == 0,
            FingerprintVisitorId = Str(h, "visitor_id"),
            StorageAgeSec = Flt(h, "storage_age_sec"),
            WebdriverFlag = Flag(h, "webdriver"),
            ScreenWidth = Flt(h, "screen_w"),
            ScreenHeight = Flt(h, "screen_h"),
            Timezone = Str(h, "tz"),
            Language = FirstToken(Str(h, "langs")),
            MouseEventCount = Flt(h, "mm_n"),
            KeyEventCount = Flt(h, "n_key"),
            TouchEventCount = Flt(h, "pt_touch"),
            ScrollEventCount = Flt(h, "n_scroll"),
            MeanInterEventMs = Flt(h, "mm_mean_ms"),
            FirstInteractionDelayMs = Flt(h, "first_interaction_delay_ms"),
            FormFillTimeSec = Flt(h, "form_fill_ms") is { } ms && !float.IsNaN(ms) ? ms / 1000f : float.NaN,
            AutofillDetected = Flag(h, "autofill"),
            PasteInIdentityFields = Flag(h, "paste_identity"),
            HoneypotTouched = Flag(h, "hp_touched"),
            PointerUntrusted = Flag(h, "pointer_untrusted"),
            PagesViewed = Flt(h, "n_pv"),
            RetentionDays = retentionDays,
            TimestampUtc = nowUtc,
        };
    }

    private static string? Str(Dictionary<string, string> h, string field)
        => h.TryGetValue(field, out var v) && v.Length > 0 ? v : null;

    private static float Flt(Dictionary<string, string> h, string field)
        => h.TryGetValue(field, out var v)
           && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : float.NaN;

    private static bool? Flag(Dictionary<string, string> h, string field)
        => h.TryGetValue(field, out var v) ? v switch { "1" => true, "0" => false, _ => null } : null;

    private static string? FirstToken(string? csv)
    {
        if (csv is null) return null;
        var comma = csv.IndexOf(',');
        var first = (comma >= 0 ? csv[..comma] : csv).Trim();
        return first.Length > 0 ? first : null;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

    private static void Log(ILoggerFactory factory, Exception? ex, string message, params object?[] args)
        => factory.CreateLogger("TelemetryGuard.Api.Endpoints.BeaconEndpoints")
            .LogWarning(ex, message, args);
}
