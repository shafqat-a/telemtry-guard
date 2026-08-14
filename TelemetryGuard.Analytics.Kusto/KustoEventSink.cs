using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TelemetryGuard.Analytics.Abstractions;

namespace TelemetryGuard.Analytics.Kusto;

/// <summary>
/// Non-blocking, batched writer to tg_events. Hot path only enqueues (D12);
/// delivery is eventual/batched per D7 — drops are counted, never thrown.
/// Register as singleton AND as IHostedService (step 9) for shutdown drain.
/// Copies ClickHouseEventSink's channel/batch/drain/metrics machinery verbatim
/// in shape; deliberately its own concrete class — no shared generic base with
/// the ClickHouse sink and no reference to that project (D7).
/// </summary>
public sealed class KustoEventSink : IEventSink, IHostedService
{
    // MUST match schema/0001_events.kql column order AND the ingestion mapping.
    // Same 71 names, same order, as ClickHouseEventSink.ColumnNames (ANA-03) —
    // duplicated on purpose: providers never reference each other (D7).
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
        "features", "shadow_score", "shadow_scorer_version"
    };

    internal const string TableName = "tg_events";
    internal const string MappingName = "tg_events_json_mapping";

    private static readonly Meter Meter = new("TelemetryGuard.Analytics.Kusto");
    private static readonly Counter<long> Enqueued        = Meter.CreateCounter<long>("tg.events.enqueued");
    private static readonly Counter<long> DroppedFull     = Meter.CreateCounter<long>("tg.events.dropped_queue_full");
    private static readonly Counter<long> Written         = Meter.CreateCounter<long>("tg.events.written");
    private static readonly Counter<long> BatchesFlushed  = Meter.CreateCounter<long>("tg.events.batches_flushed");
    private static readonly Counter<long> BatchRetries    = Meter.CreateCounter<long>("tg.events.batch_retries");
    private static readonly Counter<long> DroppedFlush    = Meter.CreateCounter<long>("tg.events.rows_dropped_flush_failed");

    private readonly Channel<ClickEvent> _channel;
    private readonly KustoAnalyticsOptions _opts;
    private readonly IKustoIngestTransport _transport;
    private readonly ILogger<KustoEventSink> _log;
    private Task? _runTask;

    public KustoEventSink(IOptions<KustoAnalyticsOptions> opts, IKustoIngestTransport transport, ILogger<KustoEventSink> log)
    {
        _opts = opts.Value;
        _transport = transport;
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
            _log.LogWarning("KustoEventSink shutdown drain exceeded {Seconds}s; remaining events abandoned",
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
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var buffer = new MemoryStream();
                await using (var w = new Utf8JsonWriter(buffer))
                {
                    WriteBatchJson(w, batch);
                    await w.FlushAsync().ConfigureAwait(false);
                }
                buffer.Position = 0;
                await _transport.IngestAsync(buffer, TableName, MappingName, CancellationToken.None)
                    .ConfigureAwait(false);
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

    /// <summary>Writes a JSON ARRAY of objects — a valid multijson payload, and it
    /// avoids Utf8JsonWriter's multiple-root-value restriction.</summary>
    internal static void WriteBatchJson(Utf8JsonWriter w, IReadOnlyList<ClickEvent> batch)
    {
        w.WriteStartArray();
        for (var i = 0; i < batch.Count; i++)
            WriteRow(w, MapRow(batch[i]));
        w.WriteEndArray();
    }

    private static void WriteRow(Utf8JsonWriter w, object?[] row)
    {
        w.WriteStartObject();
        for (var i = 0; i < ColumnNames.Length; i++)
        {
            var name = ColumnNames[i];
            switch (row[i])
            {
                case null:
                    w.WriteNull(name);
                    break;
                case string s:
                    w.WriteString(name, s);
                    break;
                case bool b:
                    w.WriteBoolean(name, b);
                    break;
                case Guid g:
                    w.WriteString(name, g);
                    break;
                case DateTime d:
                    w.WriteString(name, d.ToString("O", CultureInfo.InvariantCulture));
                    break;
                case float f:
                    // §7 missing != zero. Q2 primary (see README.md step 0): the emulator
                    // round-trips a JSON string "NaN" into a Kusto `real` as double.NaN.
                    if (float.IsFinite(f)) w.WriteNumber(name, f);
                    else w.WriteString(name, "NaN");
                    break;
                case int n:
                    w.WriteNumber(name, n);
                    break;
                case long l:
                    w.WriteNumber(name, l);
                    break;
                case string[] arr:
                    w.WriteStartArray(name);
                    foreach (var item in arr) w.WriteStringValue(item);
                    w.WriteEndArray();
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unmapped value type {row[i]!.GetType()} for column '{name}'.");
            }
        }
        w.WriteEndObject();
    }

    internal static object?[] MapRow(ClickEvent e) =>
    [
        e.TenantId.Value,
        e.SiteKey, e.SessionId, e.Kind.ToWire(),
        e.CampaignId, e.Gclid, e.Fbclid, e.Msclkid, e.Ttclid, e.ClickIdInvalid,
        KustoValueMapping.NormalizeIp(e.Ip),
        e.HeaderNames as string[] ?? e.HeaderNames.ToArray(),
        e.UserAgent, e.SecChUa, e.SecChUaMobile, e.SecChUaPlatform,
        e.AcceptLanguage, e.Referrer, e.TlsJa3, e.TlsJa4, (long?)e.CfAsn,
        e.Country, (long?)e.Asn, e.AsnOrg, e.AsnType,
        e.IsDatacenter, e.IsProxy, e.IsVpn, e.IsTor, e.IsPrivateRelay,
        e.HasJsBeacon, e.BeaconIntegrityOk, e.FingerprintVisitorId, e.StorageAgeSec,
        e.WebdriverFlag, e.HeadlessBrowser, e.ScreenWidth, e.ScreenHeight,
        e.Timezone, e.Language,
        e.MouseEventCount, e.KeyEventCount, e.TouchEventCount, e.ScrollEventCount,
        e.MeanInterEventMs, e.StdInterEventMs, e.MousePathLinearity,
        e.FirstInteractionDelayMs, e.FormFillTimeSec,
        e.AutofillDetected, e.PasteInIdentityFields, e.HoneypotTouched,
        e.PointerUntrusted, e.InputModalityMismatch,
        e.TimeOnPageSec, e.PagesViewed,
        e.IpClicksLastMin, e.IpDistinctUasLastHour,
        e.DeviceSessionsLastHour, e.DeviceIdsThisIpHour,
        e.Score, e.Band, e.Action,
        e.RuleHits as string[] ?? e.RuleHits.ToArray(),
        e.ScorerVersion, e.FeatureSetVersion,
        (int)e.RetentionDays,
        DateTime.SpecifyKind(e.TimestampUtc, DateTimeKind.Utc),
        e.Features, e.ShadowScore, e.ShadowScorerVersion ?? ""
    ];
}
