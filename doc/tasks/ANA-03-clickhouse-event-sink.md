---
id: ANA-03
title: ClickHouse event sink (batched, non-blocking)
phase: 1
workstream: analytics
depends_on: [ANA-01, ANA-02]
size: L
spec_refs: [D7, D12, D16, "section 4 (component 6)", "section 2 goal 2"]
detail_level: full
---

# ANA-03: ClickHouse event sink (batched, non-blocking)

## Objective

Implement `ClickHouseEventSink : IEventSink` and `ClickHouseLabelSink : ILabelSink` in `TelemetryGuard.Analytics.ClickHouse`: a bounded in-memory channel that the request hot path writes to without ever blocking, drained by a background flusher that bulk-inserts batches into `tg_events`/`tg_labels` with retry-then-drop semantics, graceful shutdown drain, and OpenTelemetry counters.

## Spec context (self-contained)

- **The real-time path never blocks on the event store** (spec §4 component 6). The scoring/request pipeline has a hard **< 50 ms** budget; `WriteBatchAsync` must complete synchronously in-memory (enqueue only) — no network I/O, no awaiting flushes, no lock convoys.
- **D12 — no broker at MVP:** ingestion writes to ClickHouse via async batched inserts directly from the service. No Kafka, no Redis Streams buffer in this task.
- **D7 — weak guarantee:** `IEventSink` promises only *eventual, batched* delivery. Under sustained backpressure or persistent ClickHouse failure it is CORRECT to drop events — but every drop must be counted in a metric and logged. Never propagate storage failures to the caller.
- **D16 — observability is OpenTelemetry**; use `System.Diagnostics.Metrics.Meter` so the OTel exporter (configured in API-01) picks counters up automatically.
- Batch policy (this task's contract): flush when a batch reaches **5000 events** or the oldest buffered event is **2 s** old, whichever first. Queue capacity default **100 000**; when full, the NEWEST write is dropped (`BoundedChannelFullMode.DropWrite`) and counted. Flush failures retry **3×** with exponential backoff (200 ms, 400 ms, 800 ms), then the batch is dropped and counted.
- Null semantics (§7) must survive storage: `float.NaN` SDK values are written as ClickHouse `Float32` NaN (never coerced to 0); `bool?` null flags become SQL NULL in `Nullable(UInt8)` columns.

## Prerequisites

- ANA-01: `TelemetryGuard.Analytics.Abstractions` with `ClickEvent`, `EventKind`/`EventKindWire`, `IEventSink`, `ILabelSink`, `LabelEvent` (see `doc/tasks/ANA-01-analytics-abstractions.md` for exact members).
- ANA-02: `tg_events` (68 columns, order fixed in `schema/0001_events.sql`) and `tg_labels` exist after `SchemaMigrator.ApplyAsync()`; NuGet `ClickHouse.Client` already referenced by `TelemetryGuard.Analytics.ClickHouse`.
- Add NuGet packages to `TelemetryGuard.Analytics.ClickHouse.csproj`: `Microsoft.Extensions.Hosting.Abstractions`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Logging.Abstractions`. (`System.Threading.Channels` and `System.Diagnostics.Metrics` are in-box for net8.0.)

## Implementation steps

1. **Options** — create `TelemetryGuard.Analytics.ClickHouse/ClickHouseAnalyticsOptions.cs` (bound to config section `Analytics:ClickHouse` by ANA-05; keep property names exactly as below):
   ```csharp
   namespace TelemetryGuard.Analytics.ClickHouse;

   public sealed class ClickHouseAnalyticsOptions
   {
       public string ConnectionString { get; init; } = "";
       // event sink
       public int EventQueueCapacity { get; init; } = 100_000;
       public int EventMaxBatchSize { get; init; } = 5_000;
       public double EventMaxBatchAgeSeconds { get; init; } = 2.0;
       // label sink
       public int LabelQueueCapacity { get; init; } = 10_000;
       public int LabelMaxBatchSize { get; init; } = 500;
       public double LabelMaxBatchAgeSeconds { get; init; } = 5.0;
       // flush retry (both sinks)
       public int FlushMaxRetries { get; init; } = 3;
       public double FlushRetryBaseDelayMs { get; init; } = 200;
       public double ShutdownDrainTimeoutSeconds { get; init; } = 10;
   }
   ```

2. **Event sink** — create `TelemetryGuard.Analytics.ClickHouse/ClickHouseEventSink.cs`:
   ```csharp
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
           "retention_days", "timestamp"
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
           e.TenantId.Value,                       // adjust if FND-04's TenantId property is named differently
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
           DateTime.SpecifyKind(e.TimestampUtc, DateTimeKind.Utc)
       ];

       private static byte? B(bool? v) => v is null ? null : (byte)(v.Value ? 1 : 0);

       internal static IPAddress ToIpV6(string ip)
       {
           var addr = IPAddress.TryParse(ip, out var parsed) ? parsed : IPAddress.IPv6None;
           return addr.AddressFamily == AddressFamily.InterNetwork ? addr.MapToIPv6() : addr;
       }
   }
   ```

3. **Label sink** — create `TelemetryGuard.Analytics.ClickHouse/ClickHouseLabelSink.cs`. Same pattern, small surface — write it as its own concrete class (do not extract a shared generic base; two concrete classes are the accepted duplication):
   ```csharp
   public sealed class ClickHouseLabelSink : ILabelSink, IHostedService
   {
       internal static readonly string[] ColumnNames =
           { "tenant_id", "session_id", "label", "label_source", "created_at" };
       // Meter "TelemetryGuard.Analytics.ClickHouse", counters:
       //   tg.labels.enqueued, tg.labels.dropped_queue_full, tg.labels.written,
       //   tg.labels.batches_flushed, tg.labels.batch_retries, tg.labels.rows_dropped_flush_failed

       public ValueTask WriteAsync(LabelEvent label, CancellationToken ct)
       {
           _channel.Writer.TryWrite(label);
           Enqueued.Add(1);
           return ValueTask.CompletedTask;
       }
       // Channel.CreateBounded<LabelEvent>(LabelQueueCapacity, DropWrite + drop counter),
       // RunAsync identical shape to ClickHouseEventSink but with LabelMaxBatchSize /
       // LabelMaxBatchAgeSeconds, FlushBatchAsync bulk-copies into "tg_labels" with
       // the same 3x-backoff-then-drop policy, StartAsync/StopAsync identical.

       internal static object?[] MapRow(LabelEvent l) =>
       [
           l.TenantId.Value, l.SessionId, l.Label, l.LabelSource,
           DateTime.SpecifyKind(l.CreatedAtUtc, DateTimeKind.Utc)
       ];
   }
   ```
   Implement the elided members by mirroring `ClickHouseEventSink` exactly (constructor, `StartAsync`, `StopAsync`, `RunAsync`, `FlushBatchAsync` targeting `tg_labels`).

   **Mirror checklist for `ClickHouseLabelSink`** (every elided member, so nothing drifts):
   - Constructor takes `IOptions<ClickHouseAnalyticsOptions>` + `ILogger<ClickHouseLabelSink>`; bounded channel from `LabelQueueCapacity` with `BoundedChannelFullMode.DropWrite`, `SingleReader = true`, `SingleWriter = false`, and the itemDropped callback incrementing `tg.labels.dropped_queue_full`.
   - Implements `IHostedService`: `StartAsync` launches `RunAsync` via `Task.Run`; `StopAsync` completes the writer and waits with the same `ShutdownDrainTimeoutSeconds` drain + timeout warning.
   - `RunAsync` batch loop uses `LabelMaxBatchSize` / `LabelMaxBatchAgeSeconds` (NOT the Event* options) with the identical age-CTS shape and post-completion drain.
   - `FlushBatchAsync` targets `DestinationTableName = "tg_labels"` with its own 5-entry `ColumnNames` (`tenant_id, session_id, label, label_source, created_at`), calls `InitAsync()`, and applies the same `FlushMaxRetries`/exponential-backoff/drop policy with the label counters: `tg.labels.written`, `tg.labels.batches_flushed`, `tg.labels.batch_retries`, `tg.labels.rows_dropped_flush_failed` (plus `tg.labels.enqueued` in `WriteAsync`).
   - Same `Meter` name `"TelemetryGuard.Analytics.ClickHouse"`; no exception ever reaches the `WriteAsync` caller.

4. **No DI registration here.** ANA-05 owns registration (singleton + `IHostedService` for both sinks). Keep both classes `public` so tests and ANA-05 can construct them.

5. If the installed `ClickHouse.Client` version has no `ClickHouseBulkCopy.InitAsync()` (pre-7.x API), remove that call — older versions resolve columns on first write. Prefer upgrading the package to 7.x.

## Files to create or modify

- `TelemetryGuard.Analytics.ClickHouse/ClickHouseAnalyticsOptions.cs`
- `TelemetryGuard.Analytics.ClickHouse/ClickHouseEventSink.cs`
- `TelemetryGuard.Analytics.ClickHouse/ClickHouseLabelSink.cs`
- `TelemetryGuard.Analytics.ClickHouse/TelemetryGuard.Analytics.ClickHouse.csproj` (add Hosting.Abstractions/Options/Logging.Abstractions packages)

## Acceptance criteria

- `dotnet build` passes.
- `WriteBatchAsync` contains no `await` on I/O and no locking: enqueue + counter only. A unit test can call it 200 000 times against a capacity-1000 sink (flusher not started) and it returns immediately every time; `tg.events.dropped_queue_full` observed via `MeterListener` equals 199 000.
- With ClickHouse up (FND-02 stack or Testcontainers) and schema applied (ANA-02): start the sink (`StartAsync`), write 12 000 events → within ~3 s `SELECT count() FROM tg_events` reaches 12 000 (two 5000-batches by size, remainder by 2 s age).
- Batch-age flush: write 10 events, no more; rows are queryable within ≤ 3 s (age trigger, not size).
- Shutdown drain: write 100 events, immediately call `StopAsync` — all 100 rows are in `tg_events` afterwards.
- Failure policy: with an unreachable ConnectionString, writing events produces exactly `FlushMaxRetries` increments of `tg.events.batch_retries` per batch, then `tg.events.rows_dropped_flush_failed` increases by the batch size; no exception ever reaches the `WriteBatchAsync` caller and the flusher keeps running for subsequent batches.
- NaN round-trip: an event with default (absent) SDK numerics stores `NaN` — `SELECT isNaN(storage_age_sec), isNaN(mouse_path_linearity) ...` returns 1s, never 0.
- `ClickHouseLabelSink.WriteAsync` + flush lands rows in `tg_labels` with the given label/label_source strings.

## Testing

- Unit (`tests/TelemetryGuard.Tests.Unit`):
  - `MapRow` order test: `ClickHouseEventSink.ColumnNames.Length == 68` and `MapRow(sampleEvent).Length == 68`; spot-assert index of `retention_days` (66) and `timestamp` (67), booleans map to `(byte)1/(byte)0/null`, `Kind` maps to `"tracker"` etc., IPv4 `"1.2.3.4"` maps via `ToIpV6` to an IPv6-mapped address.
  - Drop-counter test with `MeterListener` as described in acceptance criteria.
- Integration (`tests/TelemetryGuard.Tests.Integration`, Testcontainers.ClickHouse): the batching, age-flush, drain, and NaN cases above. Use small options (`EventMaxBatchSize=50`, `EventMaxBatchAgeSeconds=0.3`) to keep tests fast. The full cross-provider behavior suite is ANA-06 — do not duplicate it here beyond these smoke cases.

## Out of scope / guardrails

- **Hot path never blocks (50 ms budget):** no `Wait`, no `.Result`, no synchronous flush, no unbounded channel, no `FullMode.Wait`. If you find yourself awaiting the flusher from `WriteBatchAsync`, stop — that is the forbidden design.
- **Eventual delivery only (D7):** do not add acknowledgements, do not throw on flush failure, do not build a dead-letter store or disk spill — count and drop.
- **No broker (D12):** no Kafka/Redpanda/Event Hubs/Redis Streams here.
- **No DI wiring** — ANA-05 owns the provider switch; adding registrations here would duplicate it.
- Tenancy: `ClickEvent.TenantId` is required and written on every row; never default it, never write a row without it.
- Missing ≠ zero: never coerce NaN→0 or null→false in `MapRow`.
- No server-side Python/Node; no EF Core; no generic cross-engine abstractions — this class is deliberately ClickHouse-specific (the Kusto sink, task P2-05, will be its own implementation honoring the same weak guarantee).
