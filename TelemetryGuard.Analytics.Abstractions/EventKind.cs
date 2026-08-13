namespace TelemetryGuard.Analytics.Abstractions;

/// <summary>
/// The kind of analytics event a <see cref="ClickEvent"/> row represents.
/// </summary>
public enum EventKind
{
    Tracker = 0,
    Pixel = 1,
    Beacon = 2,
    Verdict = 3
}

/// <summary>
/// Wire/storage string mapping for <see cref="EventKind"/>. The strings
/// <c>tracker|pixel|beacon|verdict</c> are the storage contract every
/// provider (ClickHouse now, Kusto later) must use.
/// </summary>
public static class EventKindWire
{
    public static string ToWire(this EventKind kind) => kind switch
    {
        EventKind.Tracker => "tracker",
        EventKind.Pixel   => "pixel",
        EventKind.Beacon  => "beacon",
        EventKind.Verdict => "verdict",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}
