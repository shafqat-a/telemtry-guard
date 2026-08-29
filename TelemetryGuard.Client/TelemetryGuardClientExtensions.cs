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
            .Validate(o => !o.Enabled || o.Sites.Count > 0,
                "TelemetryGuard:Sites must contain at least one explicit mapping.")
            .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.Redis.ConnectionString),
                "TelemetryGuard:Redis:ConnectionString is required.")
            .Validate(o => o.Sites.Values.All(s => !s.Enabled
                || (s.TenantId != Guid.Empty && s.CompanyId > 0 && !string.IsNullOrWhiteSpace(s.Domain))),
                "Every enabled TelemetryGuard site needs TenantId, positive CompanyId, and Domain.")
            .ValidateOnStart();

        services.TryAddSingleton<ITelemetryGuardSessionStore, RedisTelemetryGuardSessionStore>();
        services.TryAddSingleton<ITelemetryGuardVisitQueue>(sp =>
            (ITelemetryGuardVisitQueue)sp.GetRequiredService<ITelemetryGuardSessionStore>());
        services.TryAddSingleton<ITelemetryGuardClientScorer, TelemetryGuardClientScorer>();
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
        group.MapGet("/p.gif", PixelAsync);
        group.MapGet("/c", TrackerAsync);
        group.MapGet("/sdk/tg.js", SdkAsync);
        return endpoints;
    }

    private static async Task<IResult> InitAsync(
        HttpContext context,
        IOptions<TelemetryGuardClientOptions> options,
        ITelemetryGuardSessionStore sessions,
        CancellationToken ct)
    {
        PublicHeaders(context);
        if (!TrySite(context.Request.Query["k"], options.Value, out _, out var site)
            || !ValidId(context.Request.Query["sid"]))
            return Results.NoContent();

        var sid = context.Request.Query["sid"].ToString();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        await sessions.StoreNonceAsync(site.TenantId, sid, nonce,
            TimeSpan.FromMinutes(options.Value.SessionTtlMinutes), ct).ConfigureAwait(false);
        return Results.Json(new
        {
            nonce,
            storageTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            storageSig = "host-managed",
            decoyPaths = Array.Empty<string>(),
            conversionGoals = Array.Empty<object>(),
        });
    }

    private static async Task<IResult> CollectAsync(
        HttpContext context,
        IOptions<TelemetryGuardClientOptions> options,
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
                || !TrySite(siteKey, options.Value, out siteKey, out var site)
                || !String(root, "session_id", out var sessionId)
                || !ValidId(sessionId))
                return Results.NoContent();

            var eventId = String(root, "visit_id", out var visitId) && ValidId(visitId)
                ? visitId
                : String(root, "sid", out var sid) && ValidId(sid) ? sid : Guid.NewGuid().ToString("N");
            var occurredMs = Long(root, "sent_at") ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var observation = Observe(root, occurredMs);
            var state = await sessions.UpdateAsync(site.TenantId, sessionId, observation,
                TimeSpan.FromMinutes(options.Value.SessionTtlMinutes), ct).ConfigureAwait(false);
            var score = scorer.Score(state);

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

    private static async Task<IResult> PixelAsync(
        HttpContext context,
        IOptions<TelemetryGuardClientOptions> options,
        ITelemetryGuardRelay relay,
        CancellationToken ct)
    {
        PublicHeaders(context);
        context.Response.Headers.CacheControl = "no-store";
        if (TrySite(context.Request.Query["k"], options.Value, out var siteKey, out var site))
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

    private static IResult TrackerAsync(
        HttpContext context, IOptions<TelemetryGuardClientOptions> options)
    {
        if (!TrySite(context.Request.Query["k"], options.Value, out _, out var site)
            || string.IsNullOrWhiteSpace(site.LandingUrl)
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

    private static TelemetryGuardObservation Observe(JsonElement root, long occurredMs)
    {
        long mouse = 0, touch = 0, scroll = 0, keys = 0;
        var webdriver = false;
        var headless = false;
        var hpField = false;
        var hpLink = false;
        var submitted = false;
        var pasted = false;

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

        return new TelemetryGuardObservation(occurredMs, mouse, touch, scroll, keys,
            webdriver, headless, hpField, hpLink, submitted, pasted);
    }

    private static string BuildMarketIqBody(
        JsonElement root, HttpContext context, TelemetryGuardSiteOptions site,
        string eventId, string sessionId, TelemetryGuardSessionState state,
        TelemetryGuardScore score, long occurredMs)
    {
        var pageUrl = String(root, "u", out var u) ? u : null;
        var query = Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri)
            ? Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query)
            : null;
        string? Query(params string[] names)
            => names.Select(n => query is not null && query.TryGetValue(n, out var v)
                ? v.FirstOrDefault() : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        var seconds = Math.Max(0, (state.LastSeenUnixMs - state.FirstSeenUnixMs) / 1000d);
        var values = new Dictionary<string, object?>
        {
            ["companyId"] = site.CompanyId,
            ["event_id"] = eventId,
            ["session_id"] = sessionId,
            ["device_id"] = String(root, "device_id", out var device) ? device : null,
            ["occurred_at"] = DateTimeOffset.FromUnixTimeMilliseconds(occurredMs),
            ["user_agent"] = context.Request.Headers.UserAgent.ToString(),
            ["ip"] = ClientIp(context),
            ["utm_platform"] = Platform(Query("utm_platform", "utm_source")),
            ["utm_publisher_id"] = Query("utm_publisher_id", "utm_content"),
            ["utm_campaign_id"] = Query("utm_campaign_id", "utm_id"),
            ["mouse_events"] = state.MouseEvents,
            ["touch_events"] = state.TouchEvents,
            ["scroll_events"] = state.ScrollEvents,
            ["keystrokes"] = state.Keystrokes,
            ["time_on_page_sec"] = seconds,
            ["pages_viewed"] = state.PagesViewed,
            ["webdriver_flag"] = state.WebDriver,
            ["headless_browser"] = state.Headless,
            ["form_submitted"] = state.FormSubmitted,
            ["paste_in_identity_fields"] = state.PasteInIdentityField,
            ["honeypot_touched"] = state.HoneypotFieldFilled || state.HoneypotLinkClicked,
            ["honeypot_field_filled"] = state.HoneypotFieldFilled,
            ["honeypot_link_clicked"] = state.HoneypotLinkClicked,
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

    private static bool TrySite(string? key, TelemetryGuardClientOptions options,
        out string siteKey, out TelemetryGuardSiteOptions site)
    {
        siteKey = key?.Trim() ?? "";
        site = null!;
        if (siteKey.Length == 0 || !options.Sites.TryGetValue(siteKey, out var resolved)
            || !resolved.Enabled) return false;
        site = resolved;
        return true;
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
