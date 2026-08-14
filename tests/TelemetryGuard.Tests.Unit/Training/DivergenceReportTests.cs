using TelemetryGuard.Training;

namespace TelemetryGuard.Tests.Unit.Training;

/// <summary>P2-02 acceptance: DivergenceReport.Compute/RankAuc are pure (no I/O).</summary>
public sealed class DivergenceReportTests
{
    private const int AllowMax = 30;
    private const int ChallengeMax = 70;

    /// <summary>Hand-built 10-row set. Bands: allow &lt;= 30, challenge 31..70, block &gt; 70.
    /// Worked out by hand in the task's PR description — see the row-by-row table below.</summary>
    private static readonly DivergenceRow[] TenRows =
    [
        new("s1", EnforcedScore: 10, ShadowScore: 15, Fraud: null),  // allow/allow, agree, delta 5
        new("s2", EnforcedScore: 20, ShadowScore: 50, Fraud: null),  // allow/challenge, shadow more, delta 30
        new("s3", EnforcedScore: 50, ShadowScore: 20, Fraud: null),  // challenge/allow, shadow fewer, delta 30
        new("s4", EnforcedScore: 60, ShadowScore: 65, Fraud: null),  // challenge/challenge, agree, delta 5
        new("s5", EnforcedScore: 80, ShadowScore: 90, Fraud: null),  // block/block, agree, delta 10
        new("s6", EnforcedScore: 90, ShadowScore: 40, Fraud: null),  // block/challenge, shadow fewer, delta 50
        new("s7", EnforcedScore: 25, ShadowScore: 25, Fraud: null),  // allow/allow, agree, delta 0
        new("s8", EnforcedScore: 45, ShadowScore: 80, Fraud: null),  // challenge/block, shadow more, delta 35
        new("s9", EnforcedScore: 5, ShadowScore: 5, Fraud: null),    // allow/allow, agree, delta 0
        new("s10", EnforcedScore: 95, ShadowScore: 95, Fraud: null), // block/block, agree, delta 0
    ];

    [Fact]
    public void Compute_TenRowSet_ExactBandMatrixAgreementDeltaAndBlockCounts()
    {
        var summary = DivergenceReport.Compute("lgbm-shadow", TenRows, ruleFlooredSessions: 0, AllowMax, ChallengeMax);

        Assert.Equal(10, summary.Sessions);
        Assert.Equal(0, summary.RuleFlooredSessions);

        // row = enforced band, col = shadow band (allow=0, challenge=1, block=2)
        int[] expected =
        [
            3, 1, 0, // enforced=allow:     allow x3 (s1,s7,s9), challenge x1 (s2), block x0
            1, 1, 1, // enforced=challenge: allow x1 (s3), challenge x1 (s4), block x1 (s8)
            0, 1, 2, // enforced=block:     allow x0, challenge x1 (s6), block x2 (s5,s10)
        ];
        Assert.Equal(expected, summary.BandMatrix);

        Assert.Equal(0.6, summary.BandAgreementRate, precision: 10);
        Assert.Equal(16.5, summary.MeanAbsDelta, precision: 10);
        Assert.Equal(2, summary.ShadowWouldBlockMore);  // s2, s8
        Assert.Equal(2, summary.ShadowWouldBlockFewer);  // s3, s6
    }

    [Fact]
    public void Compute_RuleFlooredSessions_CountedButNotPartOfComparableSessions()
    {
        var summary = DivergenceReport.Compute("lgbm-shadow", TenRows, ruleFlooredSessions: 4, AllowMax, ChallengeMax);

        Assert.Equal(10, summary.Sessions);            // comparable rows unaffected
        Assert.Equal(4, summary.RuleFlooredSessions);   // reported separately
    }

    [Fact]
    public void Compute_UnlabeledSubset_AucsAreNaN_NeverZero()
    {
        var summary = DivergenceReport.Compute("lgbm-shadow", TenRows, ruleFlooredSessions: 0, AllowMax, ChallengeMax);

        Assert.Equal(0, summary.LabeledSessions);
        Assert.True(double.IsNaN(summary.EnforcedAuc));
        Assert.True(double.IsNaN(summary.ShadowAuc));
    }

    [Fact]
    public void Compute_LabeledSubset_ComputesBothAucs()
    {
        var rows = new[]
        {
            new DivergenceRow("f1", EnforcedScore: 10, ShadowScore: 10, Fraud: false),
            new DivergenceRow("f2", EnforcedScore: 90, ShadowScore: 90, Fraud: true),
            new DivergenceRow("f3", EnforcedScore: 20, ShadowScore: 80, Fraud: false),
            new DivergenceRow("f4", EnforcedScore: 80, ShadowScore: 20, Fraud: true),
        };

        var summary = DivergenceReport.Compute("lgbm-shadow", rows, ruleFlooredSessions: 0, AllowMax, ChallengeMax);

        Assert.Equal(4, summary.LabeledSessions);
        Assert.False(double.IsNaN(summary.EnforcedAuc));
        Assert.False(double.IsNaN(summary.ShadowAuc));
    }

    [Fact]
    public void RankAuc_PerfectSeparation_ReturnsOne()
    {
        var rows = new (double Score, bool Positive)[]
        {
            (10, false), (20, false), (30, false),
            (40, true), (50, true), (60, true),
        };

        Assert.Equal(1.0, DivergenceReport.RankAuc(rows), precision: 10);
    }

    [Fact]
    public void RankAuc_ConstantScore_ReturnsHalf()
    {
        var rows = new (double Score, bool Positive)[]
        {
            (50, false), (50, true), (50, false), (50, true),
        };

        Assert.Equal(0.5, DivergenceReport.RankAuc(rows), precision: 10);
    }

    [Fact]
    public void RankAuc_MixedTies_ReturnsTieAveragedValue()
    {
        // neg=1, pos=2 (tie), neg=2 (tie), pos=3 — worked out by hand to 0.875 (see class doc).
        var rows = new (double Score, bool Positive)[]
        {
            (1, false), (2, true), (2, false), (3, true),
        };

        Assert.Equal(0.875, DivergenceReport.RankAuc(rows), precision: 10);
    }

    [Fact]
    public void RankAuc_OneClassMissing_ReturnsNaN_NeverZero()
    {
        var allPositive = new (double Score, bool Positive)[] { (10, true), (20, true) };
        var allNegative = new (double Score, bool Positive)[] { (10, false), (20, false) };
        var empty = Array.Empty<(double Score, bool Positive)>();

        Assert.True(double.IsNaN(DivergenceReport.RankAuc(allPositive)));
        Assert.True(double.IsNaN(DivergenceReport.RankAuc(allNegative)));
        Assert.True(double.IsNaN(DivergenceReport.RankAuc(empty)));
    }
}
