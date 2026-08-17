using System.Diagnostics.Metrics;
using System.Threading.Channels;
using ClickHouse.Client.ADO;
using ClickHouse.Client.Copy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TelemetryGuard.Analytics.Abstractions;

namespace TelemetryGuard.Analytics.ClickHouse;

/// <summary>
/// Non-blocking, batched writer to tg_labels (D18/D19 label loop). Same weak
/// guarantee as <see cref="ClickHouseEventSink"/>: eventual, batched delivery;
/// drops are counted, never thrown (D7). Deliberately its own concrete class —
/// no shared generic base with the event sink.
/// Register as singleton AND as IHostedService (ANA-05) for shutdown drain.
/// </summary>
public sealed class ClickHouseLabelSink : ILabelSink, IHostedService
{
    // MUST match schema/0001_events.sql tg_labels column order (ANA-02) exactly.
    internal static readonly string[] ColumnNames =
        { "tenant_id", "session_id", "label", "label_source", "created_at", "weight" };

    private static readonly Meter Meter = new("TelemetryGuard.Analytics.ClickHouse");
    private static readonly Counter<long> Enqueued        = Meter.CreateCounter<long>("tg.labels.enqueued");
    private static readonly Counter<long> DroppedFull     = Meter.CreateCounter<long>("tg.labels.dropped_queue_full");
    private static readonly Counter<long> Written         = Meter.CreateCounter<long>("tg.labels.written");
    private static readonly Counter<long> BatchesFlushed  = Meter.CreateCounter<long>("tg.labels.batches_flushed");
    private static readonly Counter<long> BatchRetries    = Meter.CreateCounter<long>("tg.labels.batch_retries");
    private static readonly Counter<long> DroppedFlush    = Meter.CreateCounter<long>("tg.labels.rows_dropped_flush_failed");

    private readonly Channel<LabelEvent> _channel;
    private readonly ClickHouseAnalyticsOptions _opts;
    private readonly ILogger<ClickHouseLabelSink> _log;
    private Task? _runTask;

    public ClickHouseLabelSink(IOptions<ClickHouseAnalyticsOptions> opts, ILogger<ClickHouseLabelSink> log)
    {
        _opts = opts.Value;
        _log = log;
        _channel = Channel.CreateBounded<LabelEvent>(
            new BoundedChannelOptions(_opts.LabelQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite, // newest dropped when full — caller NEVER waits
                SingleReader = true,
                SingleWriter = false
            },
            static _ => DroppedFull.Add(1)); // itemDropped callback counts every drop
    }

    public ValueTask WriteAsync(LabelEvent label, CancellationToken ct)
    {
        _channel.Writer.TryWrite(label); // DropWrite: always returns without blocking
        Enqueued.Add(1);
        return ValueTask.CompletedTask;  // storage outcome is never surfaced to the caller
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
            _log.LogWarning("ClickHouseLabelSink shutdown drain exceeded {Seconds}s; remaining labels abandoned",
                _opts.ShutdownDrainTimeoutSeconds);
        }
    }

    private async Task RunAsync()
    {
        var reader = _channel.Reader;
        var batch = new List<LabelEvent>(_opts.LabelMaxBatchSize);
        var maxAge = TimeSpan.FromSeconds(_opts.LabelMaxBatchAgeSeconds);

        while (await reader.WaitToReadAsync().ConfigureAwait(false)) // false only when writer completed
        {
            using var ageCts = new CancellationTokenSource(maxAge);
            try
            {
                while (batch.Count < _opts.LabelMaxBatchSize &&
                       await reader.WaitToReadAsync(ageCts.Token).ConfigureAwait(false))
                {
                    while (batch.Count < _opts.LabelMaxBatchSize && reader.TryRead(out var l))
                        batch.Add(l);
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
        while (reader.TryRead(out var l))
        {
            batch.Add(l);
            if (batch.Count >= _opts.LabelMaxBatchSize)
            {
                await FlushBatchAsync(batch).ConfigureAwait(false);
                batch.Clear();
            }
        }
        if (batch.Count > 0) await FlushBatchAsync(batch).ConfigureAwait(false);
    }

    private async Task FlushBatchAsync(List<LabelEvent> batch)
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
                    DestinationTableName = "tg_labels",
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
                _log.LogWarning(ex, "tg_labels flush attempt {Attempt}/{Max} failed; retrying in {Delay}",
                    attempt, _opts.FlushMaxRetries, delay);
                await Task.Delay(delay).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DroppedFlush.Add(batch.Count);
                _log.LogError(ex, "Dropping batch of {Count} labels after {Attempts} failed attempts (D7: eventual delivery only)",
                    batch.Count, attempt);
                return;
            }
        }
    }

    internal static object?[] MapRow(LabelEvent l) =>
    [
        l.TenantId.Value, l.SessionId, l.Label, l.LabelSource,
        DateTime.SpecifyKind(l.CreatedAtUtc, DateTimeKind.Utc), l.Weight
    ];
}
