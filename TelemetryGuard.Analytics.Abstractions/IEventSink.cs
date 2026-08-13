namespace TelemetryGuard.Analytics.Abstractions;

/// <summary>
/// Write path into the analytics event store. Delivery guarantee (spec D7):
/// eventual, batched — implementations must never block the caller on storage
/// I/O; delivery is best-effort under backpressure. Nothing about this
/// interface implies synchronous or immediate visibility of written events.
/// </summary>
public interface IEventSink
{
    ValueTask WriteBatchAsync(ReadOnlyMemory<ClickEvent> events, CancellationToken ct);
}
