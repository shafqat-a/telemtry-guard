namespace TelemetryGuard.RiskEngine.Features;

/// <summary>Finishes the derived trajectory/timing statistics (D4) from API-04's running
/// aggregates. No raw event points exist server-side — API-04 reduces the SDK's raw events
/// to Welford mean/M2 over inter-move gaps, an accumulated path length plus endpoints, and
/// counters; these formulas complete the math from those aggregates alone.</summary>
public static class TrajectoryStats
{
    /// <summary>Mouse POINT count: MmN + 1 when any move was seen (FirstX is not NaN),
    /// else 0. A single observed move yields MmN = 0 and counts as 1 point.</summary>
    public static int MousePoints(BeaconData beacon)
        => float.IsNaN(beacon.FirstX) ? 0 : beacon.MmN + 1;

    /// <summary>straight-line(first, latest) / accumulated path length, range (0, 1].
    /// Near 1.0 = perfectly straight scripted movement. NaN when fewer than 5 points
    /// or the path length is not positive (all points identical).</summary>
    public static float MousePathLinearity(BeaconData beacon)
    {
        if (MousePoints(beacon) < 5) return float.NaN;
        if (beacon.MmPathLen <= 0) return float.NaN;
        double dx = (double)beacon.PrevX - beacon.FirstX;
        double dy = (double)beacon.PrevY - beacon.FirstY;
        double straight = Math.Sqrt((dx * dx) + (dy * dy));
        return (float)(straight / beacon.MmPathLen);
    }

    /// <summary>Welford mean of the MmN inter-move gaps, ms. NaN when MmN &lt; 2.</summary>
    public static float MeanInterEventMs(BeaconData beacon)
        => beacon.MmN >= 2 ? (float)beacon.MmMeanMs : float.NaN;

    /// <summary>POPULATION standard deviation of the inter-move gaps: sqrt(MmM2 / MmN)
    /// — divide by the gap count m = MmN, not m − 1. Normative per RSK-01's
    /// StdInterEventMs doc-comment (API-04 stores only the ingredients mm_m2/mm_n and
    /// never computes a std itself). NaN when MmN &lt; 2.</summary>
    public static float StdInterEventMs(BeaconData beacon)
        => beacon.MmN >= 2 ? (float)Math.Sqrt(beacon.MmM2 / beacon.MmN) : float.NaN;

    /// <summary>Ingest-computed value consumed verbatim (API-04 is authoritative);
    /// null → NaN.</summary>
    public static float FirstInteractionDelayMs(BeaconData beacon)
        => beacon.FirstInteractionDelayMs is double delay ? (float)delay : float.NaN;

    /// <summary>MousePoints + clicks + keys + touches. Scroll events are EXCLUDED —
    /// browsers coalesce scroll non-uniformly.</summary>
    public static float InputEventCount(BeaconData beacon)
        => MousePoints(beacon) + beacon.ClickCount + beacon.KeyCount + beacon.TouchCount;

    public static float TimeOnPageSec(BeaconData beacon)
        => double.IsFinite(beacon.SessionDurationMs) && beacon.SessionDurationMs >= 0
            ? (float)(beacon.SessionDurationMs / 1000.0)
            : float.NaN;

    /// <summary>(FormSubmitTMs − FormFirstFocusTMs) / 1000; either side null → NaN.</summary>
    public static float FormFillTimeSec(BeaconData beacon)
        => beacon.FormSubmitTMs is double submit && beacon.FormFirstFocusTMs is double focus
            ? (float)((submit - focus) / 1000.0)
            : float.NaN;
}
