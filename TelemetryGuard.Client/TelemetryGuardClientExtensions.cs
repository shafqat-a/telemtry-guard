using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TelemetryGuard.Client;

public static class TelemetryGuardClientExtensions
{
    private const int MaxBodyBytes = 32 * 1024;
    private static readonly byte[] Pixel = Convert.FromBase64String(
        "R0lGODlhAQABAAD/ACwAAAAAAQABAAACADs=");

    public static IServiceCollection AddTelemetryGuardClient(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TelemetryGuardClientOptions>()
            .Bind(configuration.GetSection(TelemetryGuardClientOptions.SectionName))
            .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.Redis.ConnectionString),
                "TelemetryGuard:Redis:ConnectionString is required.")
            .Validate(o => !o.Enabled || o.IntegrityHmacSecret.Length >= 32,
                "TelemetryGuard:IntegrityHmacSecret must be at least 32 characters.")
            .Validate(o => o.Sites.Values.All(s => !s.Enabled
                || (s.TenantId != Guid.Empty && s.CompanyId > 0 && !string.IsNullOrWhiteSpace(s.Domain))),
                "Every enabled TelemetryGuard site needs TenantId, positive CompanyId, and Domain.")
            .ValidateOnStart();

        services.TryAddSingleton<ITelemetryGuardSessionStore, RedisTelemetryGuardSessionStore>();
        services.TryAddSingleton<ITelemetryGuardVisitQueue>(sp =>
            (ITelemetryGuardVisitQueue)sp.GetRequiredService<ITelemetryGuardSessionStore>());
        services.TryAddSingleton<ITelemetryGuardClientScorer, TelemetryGuardClientScorer>();
        services.TryAddSingleton<ITelemetryGuardSiteResolver, ConfigurationTelemetryGuardSiteResolver>();
        services.TryAddSingleton<ITelemetryGuardRelay, MissingTelemetryGuardRelay>();
        services.AddHostedService<TelemetryGuardVisitRelayWorker>();
        return services;
    }

    /// <summary>
    /// Maps collection only. There is intentionally no /decide endpoint and no middleware that
    /// can block, challenge, redirect, or otherwise alter the customer's request.
    /// </summary>
    public static IEndpointRouteBuilder MapTelemetryGuardClient(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider
            .GetRequiredService<IOptions<TelemetryGuardClientOptions>>().Value;
        if (!options.Enabled) return endpoints;

        var group = endpoints.MapGroup(NormalizeBase(options.PathBase));
        group.MapGet("/i/init", InitAsync);
        group.MapPost("/i", CollectAsync);
        group.MapPost("/i/conversion", CollectConversionAsync);
        group.MapGet("/p.gif", PixelAsync);
        group.MapGet("/c", TrackerAsync);
        group.MapGet("/sdk/tg.js", SdkAsync);
        return endpoints;
    }

    private static async Task<IResult> InitAsync(
        HttpContext context,
        IOptions<TelemetryGuardClientOptions> options,
        ITelemetryGuardSiteResolver sites,
        ITelemetryGuardSessionStore sessions,
        CancellationToken ct)
    {
        PublicHeaders(context);
        var siteKey = context.Request.Query["k"].ToString().Trim();
        var site = siteKey.Length == 0 ? null : await sites.ResolveAsync(siteKey, ct).ConfigureAwait(false);
        if (site is null || !ValidId(context.Request.Query["sid"]))
            return Results.NoContent();

        var sid = context.Request.Query["sid"].ToString();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var storageTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await sessions.StoreNonceAsync(site.TenantId, sid, nonce,
            TimeSpan.FromMinutes(options.Value.SessionTtlMinutes), ct).ConfigureAwait(false);
        return Results.Json(new
        {
            nonce,
            storageTs,
            storageSig = StorageSignature(options.Value.IntegrityHmacSecret, site.TenantId, storageTs),
            decoyPaths = site.DecoyPaths,
            conversionGoals = site.ConversionGoals.Select(g => new
            {
                goalId = g.GoalId,
                name = g.Name,
                triggerType = g.TriggerType,
                pagePaths = g.PagePaths,
                selector = g.Selector,
                minimumSeconds = g.MinimumSeconds,
            }),
        });
    }

    private static async Task<IResult> CollectAsync(
        HttpContext context,
        IOptions<TelemetryGuardClientOptions> options,
        ITelemetryGuardSiteResolver sites,
        ITelemetryGuardSessionStore sessions,
        ITelemetryGuardVisitQueue visits,
        ITelemetryGuardClientScorer scorer,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        PublicHeaders(context);
        if (context.Request.ContentLength > MaxBodyBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        string raw;
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8))
            raw = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        if (Encoding.UTF8.GetByteCount(raw) > MaxBodyBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !String(root, "k", out var siteKey)
                || !String(root, "session_id", out var sessionId)
                || !ValidId(sessionId))
                return Results.NoContent();
            var site = await sites.ResolveAsync(siteKey, ct).ConfigureAwait(false);
            if (site is null) return Results.NoContent();

            var eventId = String(root, "visit_id", out var visitId) && ValidId(visitId)
                ? visitId
                : String(root, "sid", out var sid) && ValidId(sid) ? sid : Guid.NewGuid().ToString("N");
            var occurredMs = Long(root, "sent_at") ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var nonceOk = String(root, "nonce", out var nonce)
                && await sessions.ValidateNonceAsync(site.TenantId, eventId, nonce, ct).ConfigureAwait(false);
            var checksumOk = ChecksumValid(raw);
            var observation = Observe(root, site, occurredMs, integrityFailed: !nonceOk || !checksumOk);
            var state = await sessions.UpdateAsync(site.TenantId, sessionId, eventId, observation,
                TimeSpan.FromMinutes(options.Value.SessionTtlMinutes), ct).ConfigureAwait(false);
            var score = scorer.Score(state.Visit);

            var body = BuildMarketIqBody(root, context, site, eventId, sessionId, state, score, occurredMs);
            await visits.ScheduleAsync(new TelemetryGuardSubmission(
                    body, sessionId, site.CompanyId, site.TenantId, siteKey, eventId),
                DateTimeOffset.UtcNow.AddSeconds(options.Value.FinalizeQuietSeconds),
                TimeSpan.FromMinutes(options.Value.SessionTtlMinutes), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // The public SDK gets no parsing, scoring, storage, or delivery oracle.
            loggerFactory.CreateLogger("TelemetryGuard.Native")
                .LogWarning(ex, "Native TelemetryGuard collection dropped an event.");
        }

        return Results.NoContent();
    }

    private static async Task<IResult> CollectConversionAsync(
        HttpContext context,
        IOptions<TelemetryGuardClientOptions> options,
        ITelemetryGuardSiteResolver sites,
        ITelemetryGuardSessionStore sessions,
        ITelemetryGuardVisitQueue visits,
        ITelemetryGuardClientScorer scorer,
        CancellationToken ct)
    {
        PublicHeaders(context);
        if (context.Request.ContentLength > MaxBodyBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        string raw;
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8))
            raw = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        if (Encoding.UTF8.GetByteCount(raw) > MaxBodyBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !String(root, "k", out var siteKey)
                || !String(root, "sessionId", out var sessionId) || !ValidId(sessionId)
                || !String(root, "visitId", out var visitId) || !ValidId(visitId)
                || !String(root, "goalId", out var goalId))
                return Results.NoContent();

            var site = await sites.ResolveAsync(siteKey, ct).ConfigureAwait(false);
            var goal = site?.ConversionGoals.FirstOrDefault(g =>
                string.Equals(g.GoalId, goalId, StringComparison.Ordinal));
            if (site is null || goal is null) return Results.NoContent();

            var pageUrl = String(root, "pageUrl", out var suppliedPage) ? suppliedPage : "";
            if (!PageMatches(pageUrl, goal.PagePaths)) return Results.NoContent();

            var occurredMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (String(root, "occurredAt", out var occurred)
                && DateTimeOffset.TryParse(occurred, out var parsed))
                occurredMs = parsed.ToUnixTimeMilliseconds();
            var pageToConversionMs = Long(root, "pageToConversionMs");
            var observation = new TelemetryGuardObservation(
                occurredMs, 0, 0, 0, 0, false, false, false, false, false,
                IsDecoyPage(pageUrl, site.DecoyPaths), false, false, true,
                pageToConversionMs, goalId, false);
            var state = await sessions.UpdateAsync(site.TenantId, sessionId, visitId, observation,
                TimeSpan.FromMinutes(options.Value.SessionTtlMinutes), ct).ConfigureAwait(false);
            var score = scorer.Score(state.Visit);

            using var normalized = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                u = pageUrl,
                r = "",
                device_id = (string?)null,
            }));
            var body = BuildMarketIqBody(normalized.RootElement, context, site, visitId,
                sessionId, state, score, occurredMs);
            await visits.ScheduleAsync(new TelemetryGuardSubmission(
                    body, sessionId, site.CompanyId, site.TenantId, siteKey, visitId),
                DateTimeOffset.UtcNow.AddSeconds(options.Value.FinalizeQuietSeconds),
                TimeSpan.FromMinutes(options.Value.SessionTtlMinutes), ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // Public conversion collection has the same success-shaped failure contract as /i.
        }

        return Results.NoContent();
    }

    private static async Task<IResult> PixelAsync(
        HttpContext context,
        IOptions<TelemetryGuardClientOptions> options,
        ITelemetryGuardSiteResolver sites,
        ITelemetryGuardRelay relay,
        CancellationToken ct)
    {
        PublicHeaders(context);
        context.Response.Headers.CacheControl = "no-store";
        var siteKey = context.Request.Query["k"].ToString().Trim();
        var site = siteKey.Length == 0 ? null : await sites.ResolveAsync(siteKey, ct).ConfigureAwait(false);
        if (site is not null)
        {
            var sid = ValidId(context.Request.Query["sid"])
                ? context.Request.Query["sid"].ToString() : Guid.NewGuid().ToString("N");
            var eventId = Guid.NewGuid().ToString("N");
            var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["companyId"] = site.CompanyId,
                ["event_id"] = eventId,
                ["session_id"] = sid,
                ["occurred_at"] = DateTimeOffset.UtcNow,
                ["ip"] = ClientIp(context),
                ["user_agent"] = context.Request.Headers.UserAgent.ToString(),
                ["pages_viewed"] = 1,
                ["tg_score"] = 0,
                ["tg_band"] = "allow",
                ["tg_rule_hits"] = Array.Empty<string>(),
                ["tg_feature_version"] = "tg-native-1",
            });
            await relay.RelayAsync(new(payload, sid, site.CompanyId, site.TenantId,
                siteKey, eventId), ct).ConfigureAwait(false);
        }
        return Results.File(Pixel, "image/gif");
    }

    private static async Task<IResult> TrackerAsync(
        HttpContext context, ITelemetryGuardSiteResolver sites, CancellationToken ct)
    {
        var siteKey = context.Request.Query["k"].ToString().Trim();
        var site = siteKey.Length == 0 ? null : await sites.ResolveAsync(siteKey, ct).ConfigureAwait(false);
        if (site is null || string.IsNullOrWhiteSpace(site.LandingUrl)
            || !Uri.TryCreate(site.LandingUrl, UriKind.Absolute, out var landing)
            || !string.Equals(landing.IdnHost, site.Domain, StringComparison.OrdinalIgnoreCase))
            return Results.NoContent();

        var builder = new UriBuilder(landing);
        var separator = string.IsNullOrEmpty(builder.Query) ? "" : builder.Query.TrimStart('?') + "&";
        builder.Query = separator + "tg_sid=" + Uri.EscapeDataString(Guid.NewGuid().ToString("N"));
        return Results.Redirect(builder.Uri.ToString(), permanent: false, preserveMethod: false);
    }

    private static IResult SdkAsync(HttpContext context)
    {
        PublicHeaders(context);
        var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("TelemetryGuard.Client.sdk.tg.js");
        return stream is null
            ? Results.NotFound()
            : Results.Stream(stream, "application/javascript; charset=utf-8",
                lastModified: null, entityTag: null, enableRangeProcessing: false);
    }

    private static TelemetryGuardObservation Observe(
        JsonElement root, TelemetryGuardSiteOptions site, long occurredMs, bool integrityFailed)
    {
        long mouse = 0, touch = 0, scroll = 0, keys = 0;
        var webdriver = false;
        var headless = false;
        var hpField = false;
        var hpLink = false;
        var submitted = false;
        var pasted = false;
        var honeyIdentifierSeen = false;

        if (root.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in events.EnumerateArray())
            {
                if (!String(e, "e", out var kind)) continue;
                switch (kind)
                {
                    case "pm" when e.TryGetProperty("s", out var samples)
                                   && samples.ValueKind == JsonValueKind.Array:
                        mouse += samples.GetArrayLength();
                        break;
                    case "pd" or "cl":
                        if (String(e, "pt", out var pt) && pt == "t") touch++;
                        else mouse++;
                        break;
                    case "sc": scroll++; break;
                    case "ky": keys++; break;
                    case "fs": submitted = true; break;
                    case "pa" when String(e, "fk", out var fieldKind) && fieldKind == "identity":
                        pasted = true;
                        break;
                    case "hp" when String(e, "kind", out var hp):
                        hpField |= hp is "input" or "submit_filled";
                        hpLink |= hp == "link_clicked";
                        break;
                    case "fp":
                        webdriver |= Bool(e, "wd");
                        if (e.TryGetProperty("botd", out var botd)) headless |= Bool(botd, "bot");
                        break;
                }
            }
        }

        var pageUrl = String(root, "u", out var url) ? url : "";
        if (Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri))
            honeyIdentifierSeen = Microsoft.AspNetCore.WebUtilities.QueryHelpers
                .ParseQuery(uri.Query).ContainsKey("tg_honey");

        return new TelemetryGuardObservation(occurredMs, mouse, touch, scroll, keys,
            webdriver, headless, hpField, hpLink, honeyIdentifierSeen,
            IsDecoyPage(pageUrl, site.DecoyPaths), submitted, pasted, false, null, null,
            integrityFailed);
    }

    private static string BuildMarketIqBody(
        JsonElement root, HttpContext context, TelemetryGuardSiteOptions site,
        string eventId, string sessionId, TelemetryGuardAggregateState state,
        TelemetryGuardScore score, long occurredMs)
    {
        var pageUrl = String(root, "u", out var u) ? u : null;
        var query = Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri)
            ? Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query)
            : null;
        string? Query(params string[] names)
            => names.Select(n => query is not null && query.TryGetValue(n, out var v)
                ? v.FirstOrDefault() : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        var visit = state.Visit;
        var session = state.Session;
        var seconds = Math.Max(0, (visit.LastSeenUnixMs - visit.FirstSeenUnixMs) / 1000d);
        var values = new Dictionary<string, object?>
        {
            ["schema_version"] = 1,
            ["source"] = "telemetry_guard",
            ["companyId"] = site.CompanyId,
            ["tg_tenant_id"] = site.TenantId,
            ["site_domain"] = site.Domain,
            ["event_id"] = eventId,
            ["session_id"] = sessionId,
            ["visit_id"] = eventId,
            ["device_id"] = String(root, "device_id", out var device) ? device : null,
            ["occurred_at"] = DateTimeOffset.FromUnixTimeMilliseconds(
                visit.FirstSeenUnixMs > 0 ? visit.FirstSeenUnixMs : occurredMs),
            ["user_agent"] = context.Request.Headers.UserAgent.ToString(),
            ["ip"] = ClientIp(context),
            ["page_url"] = pageUrl,
            ["referrer"] = String(root, "r", out var documentReferrer) ? documentReferrer : null,
            ["utm_platform"] = Platform(Query("utm_platform", "utm_source")),
            ["utm_publisher_id"] = Query("utm_publisher_id", "utm_content"),
            ["utm_campaign_id"] = Query("utm_campaign_id", "utm_id"),
            ["mouse_events"] = visit.MouseEvents,
            ["touch_events"] = visit.TouchEvents,
            ["scroll_events"] = visit.ScrollEvents,
            ["keystrokes"] = visit.Keystrokes,
            ["time_on_page_sec"] = seconds,
            ["pages_viewed"] = session.PagesViewed,
            ["webdriver_flag"] = visit.WebDriver,
            ["headless_browser"] = visit.Headless,
            ["form_submitted"] = visit.FormSubmitted,
            ["paste_in_identity_fields"] = visit.PasteInIdentityField,
            ["honeypot_touched"] = visit.HoneypotFieldFilled || visit.HoneypotLinkClicked,
            ["honeypot_field_filled"] = visit.HoneypotFieldFilled,
            ["honeypot_link_clicked"] = visit.HoneypotLinkClicked,
            ["honey_identifier_seen"] = visit.HoneyIdentifierSeen,
            ["decoy_page"] = visit.DecoyPage,
            ["beacon_integrity_ok"] = !visit.IntegrityFailed,
            ["verified_conversion"] = visit.VerifiedConversion,
            ["page_to_conversion_ms"] = visit.PageToConversionMs,
            ["conversion_goal_ids"] = visit.ConversionGoalIds.Count == 0
                ? null : visit.ConversionGoalIds,
            ["referrer_missing"] = !String(root, "r", out var referrer) || string.IsNullOrEmpty(referrer),
            ["tg_score"] = score.Score,
            ["tg_band"] = score.Band,
            ["tg_rule_hits"] = score.RuleHits,
            ["tg_feature_version"] = score.FeatureVersion,
        };
        foreach (var key in values.Where(x => x.Value is null).Select(x => x.Key).ToArray())
            values.Remove(key);
        return JsonSerializer.Serialize(values);
    }

    private static string? Platform(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "fb" or "facebook.com" or "meta" => "facebook",
        "tt" or "tiktok.com" => "tiktok",
        "google.com" or "googleads" or "google_ads" => "google",
        { Length: > 0 } v => v,
        _ => null,
    };

    private static string StorageSignature(string secret, Guid tenantId, long timestamp)
        => Convert.ToHexString(HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(secret),
                Encoding.UTF8.GetBytes($"{tenantId:D}:{timestamp}")))
            .ToLowerInvariant();

    private static bool ChecksumValid(string raw)
    {
        var marker = raw.LastIndexOf(",\"c\":\"", StringComparison.Ordinal);
        if (marker < 0) return true; // legacy bundle: integrity is unknown, not failed
        var start = marker + 6;
        var end = raw.IndexOf('"', start);
        if (end <= start) return false;
        return string.Equals(raw[start..end], Fnv1aHex(raw[..marker] + "}"),
            StringComparison.Ordinal);
    }

    private static string Fnv1aHex(string value)
    {
        var hash = 0x811c9dc5u;
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= 0x01000193u;
        }
        return hash.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool PageMatches(string pageUrl, IReadOnlyList<string> paths)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri)) return false;
        if (paths.Count == 0) return true;
        var actual = NormalizePath(uri.AbsolutePath);
        return paths.Any(path => string.Equals(actual, NormalizePath(path),
            StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDecoyPage(string pageUrl, IReadOnlyList<string> paths)
        => paths.Count > 0 && PageMatches(pageUrl, paths);

    private static string NormalizePath(string path)
    {
        path = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
        if (!path.StartsWith('/')) path = "/" + path;
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    private static string ClientIp(HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
        var first = forwarded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (!string.IsNullOrEmpty(first))
            return first.Contains(':') && first.Count(c => c == ':') == 1
                ? first[..first.IndexOf(':')] : first;
        return context.Connection.RemoteIpAddress?.ToString() ?? "";
    }


    private static bool ValidId(string? value)
        => value is { Length: >= 8 and <= 64 }
           && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static bool String(JsonElement root, string name, out string value)
    {
        value = "";
        if (!root.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.String) return false;
        value = e.GetString() ?? "";
        return value.Length > 0;
    }
    private static long? Long(JsonElement root, string name)
        => root.TryGetProperty(name, out var e) && e.TryGetInt64(out var value) ? value : null;
    private static bool Bool(JsonElement root, string name)
        => root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.True;
    private static string NormalizeBase(string value)
    {
        value = string.IsNullOrWhiteSpace(value) ? "/tg" : value.Trim();
        return "/" + value.Trim('/');
    }
    private static void PublicHeaders(HttpContext context)
    {
        context.Response.Headers.AccessControlAllowOrigin = "*";
        context.Response.Headers.CacheControl = "no-store";
    }

    private sealed class MissingTelemetryGuardRelay : ITelemetryGuardRelay
    {
        public Task RelayAsync(TelemetryGuardSubmission submission, CancellationToken ct = default)
            => throw new InvalidOperationException(
                "No ITelemetryGuardRelay is registered. The host must supply durable delivery.");
    }
}
