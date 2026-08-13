using TelemetryGuard.RiskEngine.Features;

namespace TelemetryGuard.Tests.Unit.Features;

/// <summary>Exact-formula tests. Inputs are the AGGREGATES a session would produce at
/// ingest (API-04) — no raw event points exist server-side.</summary>
public class TrajectoryStatsTests
{
    // ---- mouse_path_linearity ----

    [Fact]
    public void StraightFivePointLine_LinearityIsOne()
    {
        // Points (0,0),(1,0),(2,0),(3,0),(4,0): 4 gaps, path length 4, chord 4.
        var beacon = new BeaconData { MmN = 4, FirstX = 0, FirstY = 0, PrevX = 4, PrevY = 0, MmPathLen = 4 };

        Assert.Equal(1.0f, TrajectoryStats.MousePathLinearity(beacon), 4);
    }

    [Fact]
    public void SquareWave_PathMuchLongerThanChord_LinearityBelowHalf()
    {
        // Zig-zag: 10 points, accumulated path 20, chord (0,0)->(4,0) = 4 -> 0.2.
        var beacon = new BeaconData { MmN = 9, FirstX = 0, FirstY = 0, PrevX = 4, PrevY = 0, MmPathLen = 20 };

        var linearity = TrajectoryStats.MousePathLinearity(beacon);

        Assert.False(float.IsNaN(linearity));
        Assert.True(linearity < 0.5f);
    }

    [Fact]
    public void FourPoints_LinearityIsNaN()
    {
        var beacon = new BeaconData { MmN = 3, FirstX = 0, FirstY = 0, PrevX = 3, PrevY = 0, MmPathLen = 3 };

        Assert.True(float.IsNaN(TrajectoryStats.MousePathLinearity(beacon)));
    }

    [Fact]
    public void AllPointsIdentical_ZeroPathLength_LinearityIsNaN()
    {
        var beacon = new BeaconData { MmN = 5, FirstX = 5, FirstY = 5, PrevX = 5, PrevY = 5, MmPathLen = 0 };

        Assert.True(float.IsNaN(TrajectoryStats.MousePathLinearity(beacon)));
    }

    [Fact]
    public void NoMovesSeen_MousePointsZero_LinearityIsNaN()
    {
        var beacon = new BeaconData(); // FirstX = NaN default

        Assert.Equal(0, TrajectoryStats.MousePoints(beacon));
        Assert.True(float.IsNaN(TrajectoryStats.MousePathLinearity(beacon)));
    }

    [Fact]
    public void SingleMove_CountsAsOnePoint()
    {
        var beacon = new BeaconData { MmN = 0, FirstX = 3, FirstY = 4, PrevX = 3, PrevY = 4 };

        Assert.Equal(1, TrajectoryStats.MousePoints(beacon));
    }

    // ---- mean / std of inter-move gaps ----

    [Fact]
    public void UniformGaps_MeanExact_StdZero()
    {
        // Moves at t = 0,100,200,300 -> MmN=3, mean 100, M2 0.
        var beacon = new BeaconData { MmN = 3, MmMeanMs = 100, MmM2 = 0, FirstX = 0, FirstY = 0 };

        Assert.Equal(100f, TrajectoryStats.MeanInterEventMs(beacon));
        Assert.Equal(0f, TrajectoryStats.StdInterEventMs(beacon));
    }

    [Fact]
    public void PopulationStd_DividesByGapCount_NotGapCountMinusOne()
    {
        // Moves at t = 0,100,300 -> gaps 100,200: MmN=2, mean 150, M2 5000.
        // Population std = sqrt(5000/2) = 50. Sample std would be ~70.71 -> wrong divisor.
        var beacon = new BeaconData { MmN = 2, MmMeanMs = 150, MmM2 = 5000, FirstX = 0, FirstY = 0 };

        Assert.Equal(150f, TrajectoryStats.MeanInterEventMs(beacon));
        Assert.Equal(50f, TrajectoryStats.StdInterEventMs(beacon));
    }

    [Fact]
    public void SingleGap_MeanAndStdAreNaN()
    {
        var beacon = new BeaconData { MmN = 1, MmMeanMs = 100, MmM2 = 0, FirstX = 0, FirstY = 0 };

        Assert.True(float.IsNaN(TrajectoryStats.MeanInterEventMs(beacon)));
        Assert.True(float.IsNaN(TrajectoryStats.StdInterEventMs(beacon)));
    }

    // ---- first_interaction_delay_ms passthrough (ingest-computed, never recomputed) ----

    [Fact]
    public void FirstInteractionDelay_PassedThroughVerbatim()
    {
        var beacon = RawSessionDataBuilder.HumanBeacon() with { FirstInteractionDelayMs = 450 };
        var vector = RawSessionDataBuilder.Extractor()
            .Extract(new RawSessionDataBuilder().WithBeacon(beacon).Build());

        Assert.Equal(450f, vector.FirstInteractionDelayMs);
    }

    [Fact]
    public void FirstInteractionDelay_NullBecomesNaN()
    {
        var beacon = RawSessionDataBuilder.HumanBeacon() with { FirstInteractionDelayMs = null };
        var vector = RawSessionDataBuilder.Extractor()
            .Extract(new RawSessionDataBuilder().WithBeacon(beacon).Build());

        Assert.True(float.IsNaN(vector.FirstInteractionDelayMs));
    }

    // ---- counters / durations ----

    [Fact]
    public void InputEventCount_ExcludesScroll_ScrollReportedSeparately()
    {
        // 5 mouse points (MmN=4) + 2 clicks + 3 keys + 1 touch = 11; 7 scrolls excluded.
        var beacon = new BeaconData
        {
            MmN = 4, FirstX = 0, FirstY = 0, PrevX = 4, PrevY = 0, MmPathLen = 4,
            ClickCount = 2, KeyCount = 3, TouchCount = 1, ScrollEventCount = 7,
        };
        var vector = RawSessionDataBuilder.Extractor()
            .Extract(new RawSessionDataBuilder().WithBeacon(beacon).Build());

        Assert.Equal(11f, vector.InputEventCount);
        Assert.Equal(7f, vector.ScrollEvents);
    }

    [Fact]
    public void TimeOnPage_And_FormFillTime_ConvertedToSeconds()
    {
        var beacon = RawSessionDataBuilder.HumanBeacon() with
        {
            SessionDurationMs = 45_000,
            FormFirstFocusTMs = 1000,
            FormSubmitTMs = 5500,
        };
        var vector = RawSessionDataBuilder.Extractor()
            .Extract(new RawSessionDataBuilder().WithBeacon(beacon).Build());

        Assert.Equal(45f, vector.TimeOnPageSec);
        Assert.Equal(4.5f, vector.FormFillTimeSec);
    }

    [Fact]
    public void FormTimesMissingEitherSide_FormFillTimeIsNaN()
    {
        var focusOnly = RawSessionDataBuilder.HumanBeacon() with { FormFirstFocusTMs = 1000, FormSubmitTMs = null };
        var submitOnly = RawSessionDataBuilder.HumanBeacon() with { FormFirstFocusTMs = null, FormSubmitTMs = 5500 };

        Assert.True(float.IsNaN(TrajectoryStats.FormFillTimeSec(focusOnly)));
        Assert.True(float.IsNaN(TrajectoryStats.FormFillTimeSec(submitOnly)));
    }

    [Fact]
    public void ExtractWiring_StraightLineBeacon_VectorLinearityIsOne()
    {
        var beacon = new BeaconData { MmN = 4, FirstX = 0, FirstY = 0, PrevX = 4, PrevY = 0, MmPathLen = 4 };
        var vector = RawSessionDataBuilder.Extractor()
            .Extract(new RawSessionDataBuilder().WithBeacon(beacon).Build());

        Assert.Equal(1.0f, vector.MousePathLinearity, 4);
    }
}
