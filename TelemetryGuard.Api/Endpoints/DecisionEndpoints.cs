using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TelemetryGuard.Api.Options;
using TelemetryGuard.Api.Services;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Data.Tenancy;
using TelemetryGuard.Integrations.Turnstile;
using TelemetryGuard.RiskEngine.Contracts;
using TelemetryGuard.RiskEngine.Pipeline;

namespace TelemetryGuard.Api.Endpoints;

/// <summary>
/// API-05: POST /decide — the lead-form gate (spec §6.2). Scores the session via
/// RSK-07's in-process pipeline, maps the score to a band using CONFIGURED
/// thresholds (Scoring:Bands), drives the Cloudflare Turnstile round-trip for the
/// challenge band (verify via INT-01, then re-score with the challenge outcome as
/// a CTX feature — spec §6.3), and finalizes the verdict immediately so the
/// grace-period worker (API-06) doesn't double-process the session.
///
/// Tenant resolution: DAT-04's middleware passes /decide through UNRESOLVED (see
/// the pass-through predicate added to TenantResolutionMiddleware), so this
/// handler resolves the ?k= site key itself, exactly like API-04.
///
/// The response NEVER exposes the numeric score, band name, or rule hits — only
/// the action string and (for challenge) the Turnstile site key.
/// </summary>
public static partial class DecisionEndpoints
{
    // Same shape API-04 accepts: 32-lowercase-hex tracker ids and SDK-02's
    // crypto.randomUUID() fallback (36 chars incl. dashes) for organic sessions.
    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex SidShape();

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private sealed record DecideRequest(string? Sid, string? TurnstileToken, string? K);

    public static IEndpointRouteBuilder MapDecisionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/decide", HandleDecideAsync);
        app.MapMethods("/decide", ["OPTIONS"], HandlePreflightAsync);
        return app;
    }

    // --------------------------------------------------------------- OPTIONS --

    private static IResult HandlePreflightAsync(HttpContext ctx)
    {
        ctx.Response.Headers.AccessControlAllowOrigin = "*";
        ctx.Response.Headers.AccessControlAllowMethods = "POST";
        ctx.Response.Headers.AccessControlAllowHeaders = "Content-Type";
        ctx.Response.Headers.AccessControlMaxAge = "86400";
        return Results.StatusCode(StatusCodes.Status204NoContent);
    }

    // ------------------------------------------------------------------ POST --

    private static async Task<IResult> HandleDecideAsync(
        HttpContext ctx,
        ITenantResolver resolver,
        TenantContext tenantContext,
        IScoringPipeline pipeline,
        ITurnstileVerifier turnstile,
        IVerdictFinalizer finalizer,
        IConnectionMultiplexer redis,
        IOptions<ScoringBandOptions> bandOptions,
        IOptions<TurnstileOptions> turnstileOptions,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("TelemetryGuard.Api.Endpoints.DecisionEndpoints");
        var bands = bandOptions.Value;
        var siteKeyForWidget = turnstileOptions.Value.SiteKey;

        // CORS: unlike /i and /c, the caller MUST be able to READ this response
        // (it drives client-side lead-gating). No credentials are used — sid/k
        // ride the body/query — so a plain wildcard is correct and simplest.
        // Applied to every response below, including validation/resolution errors.
        if (ctx.Request.Headers.ContainsKey("Origin"))
        {
            ctx.Response.Headers.AccessControlAllowOrigin = "*";
            ctx.Response.Headers.Vary = "Origin";
        }

        // Body read: UTF-8 JSON regardless of declared Content-Type (text/plain
        // keeps the SDK's request a CORS-preflight-free "simple request";
        // application/json is the fetch fallback that DOES trigger preflight,
        // which the OPTIONS handler above answers).
        string rawBody;
        using (var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8))
        {
            rawBody = await reader.ReadToEndAsync(ct);
        }

        DecideRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<DecideRequest>(rawBody, JsonOpts);
        }
        catch (JsonException)
        {
            return MalformedBody();
        }
        if (req is null)
            return MalformedBody();

        // k: query wins; body is a fallback (sendBeacon-style simple requests may
        // prefer the body). When both are present they must agree.
        var queryK = ctx.Request.Query["k"].ToString();
        if (queryK.Length > 0 && !string.IsNullOrEmpty(req.K)
            && !string.Equals(queryK, req.K, StringComparison.Ordinal))
        {
            return ValidationProblem("k", "Query k and body k must match when both are present.");
        }
        var effectiveK = queryK.Length > 0 ? queryK : (req.K ?? "");

        if (req.Sid is null || !SidShape().IsMatch(req.Sid))
            return ValidationProblem("sid", "sid must match ^[A-Za-z0-9_-]{8,64}$.");
        var sid = req.Sid;

        // Step 0: resolve tenant (the middleware passed this route through
        // unresolved). Unknown key -> 404: this route is called by first-party
        // SDK code that reads the response, so there is no anti-probing reason
        // for a success-shaped drop the way /c and /p.gif use one.
        var resolved = await resolver.ResolveSiteKeyAsync(effectiveK, ct);
        if (resolved is null)
            return UnknownSiteKey();
        tenantContext.Resolve(new TenantId(resolved.TenantId), resolved.SiteKey);
        var tenantId = new TenantId(resolved.TenantId);

        // Step 2: initial score. Tenant is ambient (just stamped above); the
        // challenge outcome defaults to NotChallenged for this first call.
        var outcome = await pipeline.ScoreSessionAsync(sid, ChallengeOutcome.NotChallenged, ct);

        // Step 3: unknown session (no click, no beacon ever seen). Never finalize
        // — there is nothing to finalize — and never re-score.
        if (outcome is null)
        {
            logger.LogWarning(
                "Decide: unknown session {SessionId} for tenant {TenantId}", sid, tenantId);

            if (string.IsNullOrEmpty(req.TurnstileToken))
            {
                TagActivity("challenge", true);
                return ChallengeResult(siteKeyForWidget);
            }

            var verify = await turnstile.VerifyAsync(req.TurnstileToken, ClientIp(ctx), ct);
            var action = verify.Success ? "allow" : "block";
            TagActivity(action, true);
            return ActionResult(action);
        }

        // Step 4: map band using CONFIGURED thresholds (never the hardcoded
        // BandMapper constants baked into RSK-07's outcome.Band). Under default
        // config the two agree; a mismatch only ever means a test/tenant override
        // moved the thresholds — that's expected, not a bug, so it's logged at
        // Debug rather than asserted/thrown.
        var band = MapBand(outcome.Result.Score, bands);
        if (band != outcome.Band)
        {
            logger.LogDebug(
                "Decide: locally-mapped band {LocalBand} differs from pipeline band {PipelineBand} " +
                "for session {SessionId} (score {Score}) — expected only under a Scoring:Bands override.",
                band, outcome.Band, sid, outcome.Result.Score);
        }

        switch (band)
        {
            case VerdictBand.Allow:
            {
                await FinalizeDecisionAsync(finalizer, redis, tenantId, sid, outcome, logger, ct);
                TagActivity("allow", false);
                return ActionResult("allow");
            }

            case VerdictBand.Block:
            {
                await FinalizeDecisionAsync(finalizer, redis, tenantId, sid, outcome, logger, ct);
                TagActivity("block", false);
                return ActionResult("block");
            }

            default: // VerdictBand.Challenge
            {
                if (string.IsNullOrEmpty(req.TurnstileToken))
                {
                    // Round-trip still in flight: do NOT finalize. The grace
                    // worker's deadline backstops abandonment.
                    TagActivity("challenge", true);
                    return ChallengeResult(siteKeyForWidget);
                }

                var verify = await turnstile.VerifyAsync(req.TurnstileToken, ClientIp(ctx), ct);
                var challengeOutcome = verify.Success ? ChallengeOutcome.Passed : ChallengeOutcome.Failed;

                // Re-score WITH the challenge outcome (spec §6.3): RSK-07 takes it
                // as a parameter (not a separate session-state write) and feeds it
                // to feature extraction as the CTX conditioning feature.
                var rescored = await pipeline.ScoreSessionAsync(sid, challengeOutcome, ct);
                if (rescored is null)
                {
                    // Session state evaporated mid-flight (TTL race). Log; no
                    // finalize (nothing to finalize).
                    logger.LogWarning(
                        "Decide: session {SessionId} evaporated mid-challenge for tenant {TenantId}",
                        sid, tenantId);
                    var fallback = verify.Success ? "allow" : "block";
                    TagActivity(fallback, true);
                    return ActionResult(fallback);
                }

                if (!verify.Success)
                {
                    // A failed/unavailable (fail-closed) Turnstile verification
                    // blocks outright — rules only raise; there is no "wash out"
                    // of a challenge failure.
                    await FinalizeDecisionAsync(finalizer, redis, tenantId, sid, rescored, logger, ct);
                    TagActivity("block", true);
                    return ActionResult("block");
                }

                // Passed challenge: re-scored band decides. A passed challenge
                // that STILL scores block blocks (a solved Turnstile doesn't wash
                // out T1 evidence). Anything else allows — never re-challenge.
                var final = rescored.Result.Score > bands.ChallengeMax ? "block" : "allow";
                await FinalizeDecisionAsync(finalizer, redis, tenantId, sid, rescored, logger, ct);
                TagActivity(final, true);
                return ActionResult(final);
            }
        }
    }

    // ------------------------------------------------------------- helpers --

    private static VerdictBand MapBand(int score, ScoringBandOptions bands)
        => score <= bands.AllowMax ? VerdictBand.Allow
         : score <= bands.ChallengeMax ? VerdictBand.Challenge
         : VerdictBand.Block;

    /// <summary>Finalize + grace-entry cleanup, belt-and-braces per task step 9:
    /// the real/stub finalizer already removes the grace entry itself, but this
    /// explicit ZREM (idempotent) guarantees cleanup even if finalization throws
    /// before reaching its own removal. Neither failure may turn an already-computed
    /// decision into a 500 — both are caught and logged.</summary>
    private static async Task FinalizeDecisionAsync(
        IVerdictFinalizer finalizer, IConnectionMultiplexer redis, TenantId tenantId, string sid,
        ScoringOutcome outcome, ILogger logger, CancellationToken ct)
    {
        try
        {
            await finalizer.FinalizeAsync(tenantId, sid, FinalizeTrigger.Decide, outcome, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Decide: verdict finalization failed for session {SessionId} tenant {TenantId}; " +
                "decision already computed, returning it anyway.", sid, tenantId);
        }

        try
        {
            await redis.GetDatabase().SortedSetRemoveAsync($"t:{tenantId}:grace", sid).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Decide: grace-entry removal failed for session {SessionId} tenant {TenantId}; " +
                "the finalizer's own removal is the primary guarantee.", sid, tenantId);
        }
    }

    private static void TagActivity(string action, bool challenged)
    {
        var activity = Activity.Current;
        if (activity is null) return;
        activity.SetTag("tg.decide.action", action);
        activity.SetTag("tg.decide.challenged", challenged);
        // Never tag the raw score or band — the response contract's "never leak
        // scoring internals" extends to telemetry a tenant could scrape.
    }

    private static string? ClientIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString();

    private static IResult ActionResult(string action) => Results.Json(new { action });

    private static IResult ChallengeResult(string turnstileSiteKey)
        => Results.Json(new { action = "challenge", turnstileSiteKey });

    private static IResult MalformedBody()
        => Results.ValidationProblem(
            new Dictionary<string, string[]> { ["body"] = ["Request body must be valid JSON."] });

    private static IResult ValidationProblem(string field, string message)
        => Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static IResult UnknownSiteKey()
        => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Unknown site key",
            type: "https://httpstatuses.io/404");
}
