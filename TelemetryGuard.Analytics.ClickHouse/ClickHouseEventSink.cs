using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using ClickHouse.Client.ADO;
using ClickHouse.Client.Copy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TelemetryGuard.Analytics.Abstractions;

namespace TelemetryGuard.Analytics.ClickHouse;

/// <summary>
/// Non-blocking, batched writer to tg_events. Hot path only enqueues (D12);
/// delivery is eventual/batched per D7 — drops are counted, never thrown.
/// Register as singleton AND as IHostedService (ANA-05) for shutdown drain.
/// </summary>
public sealed class ClickHouseEventSink : IEventSink, IHostedService
{
    // MUST match schema/0001_events.sql column order (ANA-02) exactly.
    internal static readonly string[] ColumnNames =
    {
        "tenant_id", "site_key", "session_id", "kind",
        "campaign_id", "gclid", "fbclid", "msclkid", "ttclid", "click_id_invalid",
        "ip", "header_names", "user_agent", "sec_ch_ua", "sec_ch_ua_mobile",
        "sec_ch_ua_platform", "accept_language", "referrer", "tls_ja3", "tls_ja4", "cf_asn",
        "country", "asn", "asn_org", "asn_type",
        "is_datacenter", "is_proxy", "is_vpn", "is_tor", "is_private_relay",
        "has_js_beacon", "beacon_integrity_ok", "fingerprint_visitor_id", "storage_age_sec",
        "webdriver_flag", "headless_browser", "screen_width", "screen_height",
        "timezone", "language",
        "mouse_event_count", "key_event_count", "touch_event_count", "scroll_event_count",
        "mean_inter_event_ms", "std_inter_event_ms", "mouse_path_linearity",
        "first_interaction_delay_ms", "form_fill_time_sec",
        "autofill_detected", "paste_in_identity_fields", "honeypot_touched",
        "pointer_untrusted", "input_modality_mismatch",
        "time_on_page_sec", "pages_viewed",
        "ip_clicks_last_min", "ip_distinct_uas_last_hour",
        "device_sessions_last_hour", "device_ids_this_ip_hour",
        "score", "band", "action", "rule_hits", "scorer_version", "feature_set_version",
        "retention_days", "timestamp",
        // RSK-08 (0002_training_and_shadow.sql ALTERs): appended at the end — bulk-copy
        // ColumnNames is an explicit name/order whitelist, independent of the table's
        // physical column order, so appending here needs no reshuffle of the above.
        "features", "shadow_score", "shadow_scorer_version",
        // ANA-08 (0003_attribution.sql ALTERs): appended at the end, same reasoning.
        "gbraid", "wbraid",
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "utm_id",
        "cookie_fbc", "cookie_fbp", "cookie_gcl_aw", "cookie_ttp",
        "attribution_channel", "landing_path", "landing_query_keys", "headers",
        // 0004_full_request.sql
        "landing_url", "cookies"
    };

    private static readonly Meter Meter = new("TelemetryGuard.Analytics.ClickHouse");
    private static readonly Counter<long> Enqueued        = Meter.CreateCounter<long>("tg.events.enqueued");
    private static readonly Counter<long> DroppedFull     = Meter.CreateCounter<long>("tg.events.dropped_queue_full");
    private static readonly Counter<long> Written         = Meter.CreateCounter<long>("tg.events.written");
    private static readonly Counter<long> BatchesFlushed  = Meter.CreateCounter<long>("tg.events.batches_flushed");
    private static readonly Counter<long> BatchRetries    = Meter.CreateCounter<long>("tg.events.batch_retries");
    private static readonly Counter<long> DroppedFlush    = Meter.CreateCounter<long>("tg.events.rows_dropped_flush_failed");

    private readonly Channel<ClickEvent> _channel;
    private readonly ClickHouseAnalyticsOptions _opts;
    private readonly ILogger<ClickHouseEventSink> _log;
    private Task? _runTask;

    public ClickHouseEventSink(IOptions<ClickHouseAnalyticsOptions> opts, ILogger<ClickHouseEventSink> log)
    {
        _opts = opts.Value;
        _log = log;
        _channel = Channel.CreateBounded<ClickEvent>(
            new BoundedChannelOptions(_opts.EventQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite, // newest dropped when full — hot path NEVER waits
                SingleReader = true,
                SingleWriter = false
            },
            static _ => DroppedFull.Add(1)); // itemDropped callback counts every drop
    }

    public ValueTask WriteBatchAsync(ReadOnlyMemory<ClickEvent> events, CancellationToken ct)
    {
        var span = events.Span;
        for (var i = 0; i < span.Length; i++)
            _channel.Writer.TryWrite(span[i]); // DropWrite: always returns without blocking
        Enqueued.Add(span.Length);
        return ValueTask.CompletedTask;        // storage outcome is never surfaced to the hot path
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _runTask = Task.Run(RunAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete(); // RunAsync drains everything left, then exits
        if (_runTask is null) return;
        try
        {
            await _runTask.WaitAsync(TimeSpan.FromSeconds(_opts.ShutdownDrainTimeoutSeconds), cancellationToken);
        }
        catch (TimeoutException)
        {
            _log.LogWarning("ClickHouseEventSink shutdown drain exceeded {Seconds}s; remaining events abandoned",
                _opts.ShutdownDrainTimeoutSeconds);
        }
    }

    private async Task RunAsync()
    {
        var reader = _channel.Reader;
        var batch = new List<ClickEvent>(_opts.EventMaxBatchSize);
        var maxAge = TimeSpan.FromSeconds(_opts.EventMaxBatchAgeSeconds);

        while (await reader.WaitToReadAsync().ConfigureAwait(false)) // false only when writer completed
        {
            using var ageCts = new CancellationTokenSource(maxAge);
            try
            {
                while (batch.Count < _opts.EventMaxBatchSize &&
                       await reader.WaitToReadAsync(ageCts.Token).ConfigureAwait(false))
                {
                    while (batch.Count < _opts.EventMaxBatchSize && reader.TryRead(out var e))
                        batch.Add(e);
                }
            }
            catch (OperationCanceledException) { /* batch age reached — flush what we have */ }

            if (batch.Count > 0)
            {
                await FlushBatchAsync(batch).ConfigureAwait(false);
                batch.Clear();
            }
        }

        // channel completed (shutdown): drain the remainder
        while (reader.TryRead(out var e))
        {
            batch.Add(e);
            if (batch.Count >= _opts.EventMaxBatchSize)
            {
                await FlushBatchAsync(batch).ConfigureAwait(false);
                batch.Clear();
            }
        }
        if (batch.Count > 0) await FlushBatchAsync(batch).ConfigureAwait(false);
    }

    private async Task FlushBatchAsync(List<ClickEvent> batch)
    {
        var rows = new object?[batch.Count][];
        for (var i = 0; i < batch.Count; i++) rows[i] = MapRow(batch[i]);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = new ClickHouseConnection(_opts.ConnectionString);
                using var bulk = new ClickHouseBulkCopy(conn)
                {
                    DestinationTableName = "tg_events",
                    ColumnNames = ColumnNames,
                    BatchSize = batch.Count
                };
                await bulk.InitAsync().ConfigureAwait(false); // required when ColumnNames is set (ClickHouse.Client 7.x)
                await bulk.WriteToServerAsync(rows).ConfigureAwait(false);
                Written.Add(batch.Count);
                BatchesFlushed.Add(1);
                return;
            }
            catch (Exception ex) when (attempt <= _opts.FlushMaxRetries)
            {
                BatchRetries.Add(1);
                var delay = TimeSpan.FromMilliseconds(_opts.FlushRetryBaseDelayMs * Math.Pow(2, attempt - 1));
                _log.LogWarning(ex, "tg_events flush attempt {Attempt}/{Max} failed; retrying in {Delay}",
                    attempt, _opts.FlushMaxRetries, delay);
                await Task.Delay(delay).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DroppedFlush.Add(batch.Count);
                _log.LogError(ex, "Dropping batch of {Count} events after {Attempts} failed attempts (D7: eventual delivery only)",
                    batch.Count, attempt);
                return;
            }
        }
    }

    internal static object?[] MapRow(ClickEvent e) =>
    [
        e.TenantId.Value,
        e.SiteKey, e.SessionId, e.Kind.ToWire(),
        e.CampaignId, e.Gclid, e.Fbclid, e.Msclkid, e.Ttclid, B(e.ClickIdInvalid),
        ToIpV6(e.Ip),
        e.HeaderNames as string[] ?? e.HeaderNames.ToArray(),
        e.UserAgent, e.SecChUa, e.SecChUaMobile, e.SecChUaPlatform,
        e.AcceptLanguage, e.Referrer, e.TlsJa3, e.TlsJa4, e.CfAsn,
        e.Country, e.Asn, e.AsnOrg, e.AsnType,
        B(e.IsDatacenter), B(e.IsProxy), B(e.IsVpn), B(e.IsTor), B(e.IsPrivateRelay),
        (byte)(e.HasJsBeacon ? 1 : 0), B(e.BeaconIntegrityOk), e.FingerprintVisitorId, e.StorageAgeSec,
        B(e.WebdriverFlag), B(e.HeadlessBrowser), e.ScreenWidth, e.ScreenHeight,
        e.Timezone, e.Language,
        e.MouseEventCount, e.KeyEventCount, e.TouchEventCount, e.ScrollEventCount,
        e.MeanInterEventMs, e.StdInterEventMs, e.MousePathLinearity,
        e.FirstInteractionDelayMs, e.FormFillTimeSec,
        B(e.AutofillDetected), B(e.PasteInIdentityFields), B(e.HoneypotTouched),
        B(e.PointerUntrusted), B(e.InputModalityMismatch),
        e.TimeOnPageSec, e.PagesViewed,
        e.IpClicksLastMin, e.IpDistinctUasLastHour,
        e.DeviceSessionsLastHour, e.DeviceIdsThisIpHour,
        (short?)e.Score, e.Band, e.Action,
        e.RuleHits as string[] ?? e.RuleHits.ToArray(),
        e.ScorerVersion, (ushort?)e.FeatureSetVersion,
        e.RetentionDays,
        DateTime.SpecifyKind(e.TimestampUtc, DateTimeKind.Utc),
        e.Features, (short?)e.ShadowScore, e.ShadowScorerVersion ?? "",
        e.Gbraid, e.Wbraid,
        e.UtmSource, e.UtmMedium, e.UtmCampaign, e.UtmTerm, e.UtmContent, e.UtmId,
        e.CookieFbc, e.CookieFbp, e.CookieGclAw, e.CookieTtp,
        e.AttributionChannel, e.LandingPath ?? "",
        e.LandingQueryKeys as string[] ?? e.LandingQueryKeys.ToArray(),
        e.Headers as Dictionary<string, string> ?? new Dictionary<string, string>(e.Headers),
        e.LandingUrl ?? "",
        e.Cookies as Dictionary<string, string> ?? new Dictionary<string, string>(e.Cookies)
    ];

    private static byte? B(bool? v) => v is null ? null : (byte)(v.Value ? 1 : 0);

    internal static IPAddress ToIpV6(string ip)
    {
        var addr = IPAddress.TryParse(ip, out var parsed) ? parsed : IPAddress.IPv6None;
        return addr.AddressFamily == AddressFamily.InterNetwork ? addr.MapToIPv6() : addr;
    }
}
