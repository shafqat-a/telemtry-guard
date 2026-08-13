using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Analytics.ClickHouse;
using TelemetryGuard.Core.Tenancy;

namespace TelemetryGuard.Tests.Unit.Analytics;

/// <summary>
/// ANA-03 acceptance: the hot path only enqueues — WriteBatchAsync never blocks
/// and never throws; overflow beyond queue capacity is dropped (DropWrite) and
/// every drop is counted on tg.events.dropped_queue_full.
/// </summary>
public sealed class ClickHouseEventSinkDropTests
{
    [Fact]
    public void WriteBatchAsync_QueueFull_DropsNewest_AndCountsEveryDrop()
    {
        const int capacity = 1_000;
        const int writes = 200_000;

        long dropped = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "TelemetryGuard.Analytics.ClickHouse" &&
                instrument.Name == "tg.events.dropped_queue_full")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (_, value, _, _) => Interlocked.Add(ref dropped, value));
        listener.Start();

        // Flusher deliberately NOT started: nothing ever leaves the queue.
        var sink = new ClickHouseEventSink(
            Options.Create(new ClickHouseAnalyticsOptions
            {
                ConnectionString = "Host=unused;Port=8123",
                EventQueueCapacity = capacity
            }),
            NullLogger<ClickHouseEventSink>.Instance);

        var one = new[] { NewEvent() };
        for (var i = 0; i < writes; i++)
        {
            var vt = sink.WriteBatchAsync(one, CancellationToken.None);
            Assert.True(vt.IsCompletedSuccessfully); // returns immediately, every time
        }

        Assert.Equal(writes - capacity, Interlocked.Read(ref dropped));
    }

    private static ClickEvent NewEvent() => new()
    {
        TenantId = new TenantId(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")),
        SiteKey = "sk-drop",
        SessionId = "sess-drop",
        Kind = EventKind.Pixel,
        Ip = "203.0.113.1",
        HasJsBeacon = false,
        RetentionDays = 90,
        TimestampUtc = DateTime.UtcNow
    };
}
