using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Training;

namespace TelemetryGuard.Tests.Unit.Training;

/// <summary>RSK-08 step 4 acceptance: read-time dedupe/conflict resolution of
/// tg_labels rows (pure, no DB — LabelBuilder/Trainer both delegate to this).</summary>
public sealed class LabelResolverTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void T1RuleOnly_UsesConfiguredWeight_NotStoredWeight()
    {
        var rows = new[]
        {
            new RawLabelRow("s1", LabelValues.Fraud, LabelSources.T1Rule, Weight: 1f, T0),
        };

        var result = LabelResolver.Resolve(rows, t1PositiveWeight: 0.6f);

        var only = Assert.Single(result.Resolved);
        Assert.Equal("s1", only.SessionId);
        Assert.True(only.Fraud);
        Assert.Equal(0.6f, only.Weight); // read-time override, not the stored weight=1
        Assert.Equal(0, result.DroppedConflicts);
    }

    [Fact]
    public void DuplicateT1RuleRows_LiveWritePlusBackfill_Dedupe_ToOne()
    {
        // API-06 writes one live at finalize time; LabelBuilder's backfill re-derives
        // the same session — both rows exist in tg_labels (plain MergeTree, nothing
        // dedupes on disk); Resolve must collapse them to exactly one.
        var rows = new[]
        {
            new RawLabelRow("s1", LabelValues.Fraud, LabelSources.T1Rule, 1f, T0),
            new RawLabelRow("s1", LabelValues.Fraud, LabelSources.T1Rule, 1f, T0.AddSeconds(5)),
        };

        var result = LabelResolver.Resolve(rows, t1PositiveWeight: 0.6f);

        Assert.Single(result.Resolved);
        Assert.Equal(0, result.DroppedConflicts);
    }

    [Theory]
    [InlineData(LabelSources.SyntheticBot)]
    [InlineData(LabelSources.ReviewScreen)]
    [InlineData(LabelSources.Conversion)]
    public void GuaranteedSource_BeatsT1Rule(string guaranteedSource)
    {
        // t1_rule says fraud (a weak positive), but the GUARANTEED source disagrees
        // with a legit verdict — the guaranteed source must win, not t1_rule.
        var rows = new[]
        {
            new RawLabelRow("s1", LabelValues.Fraud, LabelSources.T1Rule, 1f, T0),
            new RawLabelRow("s1", LabelValues.Legit, guaranteedSource, 1f, T0.AddSeconds(1)),
        };

        var result = LabelResolver.Resolve(rows, t1PositiveWeight: 0.6f);

        var only = Assert.Single(result.Resolved);
        Assert.False(only.Fraud); // guaranteed source's label wins
        Assert.Equal(0, result.DroppedConflicts);
    }

    [Fact]
    public void ContradictoryGuaranteedLabels_AreDropped_AndCounted()
    {
        var rows = new[]
        {
            new RawLabelRow("s1", LabelValues.Fraud, LabelSources.SyntheticBot, 1f, T0),
            new RawLabelRow("s1", LabelValues.Legit, LabelSources.ReviewScreen, 1f, T0.AddSeconds(1)),
        };

        var result = LabelResolver.Resolve(rows, t1PositiveWeight: 0.6f);

        Assert.Empty(result.Resolved);
        Assert.Equal(1, result.DroppedConflicts);
    }

    [Fact]
    public void SameGuaranteedSourceAgreeingTwice_DedupesWithoutDropping()
    {
        var rows = new[]
        {
            new RawLabelRow("s1", LabelValues.Legit, LabelSources.ReviewScreen, 1f, T0),
            new RawLabelRow("s1", LabelValues.Legit, LabelSources.ReviewScreen, 1f, T0.AddSeconds(1)),
        };

        var result = LabelResolver.Resolve(rows, t1PositiveWeight: 0.6f);

        var only = Assert.Single(result.Resolved);
        Assert.False(only.Fraud);
        Assert.Equal(0, result.DroppedConflicts);
    }

    [Fact]
    public void IndependentSessions_AllResolved()
    {
        var rows = new[]
        {
            new RawLabelRow("s1", LabelValues.Fraud, LabelSources.T1Rule, 1f, T0),
            new RawLabelRow("s2", LabelValues.Legit, LabelSources.ReviewScreen, 1f, T0),
            new RawLabelRow("s3", LabelValues.Fraud, LabelSources.SyntheticBot, 1f, T0),
        };

        var result = LabelResolver.Resolve(rows, t1PositiveWeight: 0.6f);

        Assert.Equal(3, result.Resolved.Count);
        Assert.Equal(0, result.DroppedConflicts);
    }

    [Fact]
    public void ReviewScreenWeight_IsStoredWeight_NotT1Override()
    {
        var rows = new[]
        {
            new RawLabelRow("s1", LabelValues.Legit, LabelSources.ReviewScreen, 1.0f, T0),
        };

        var result = LabelResolver.Resolve(rows, t1PositiveWeight: 0.6f);

        Assert.Equal(1.0f, Assert.Single(result.Resolved).Weight);
    }
}
