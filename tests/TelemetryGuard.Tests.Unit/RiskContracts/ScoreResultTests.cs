using TelemetryGuard.RiskEngine.Contracts;

namespace TelemetryGuard.Tests.Unit.RiskContracts;

public class ScoreResultTests
{
    [Fact]
    public void RecordsWithSameValues_AreEqual()
    {
        var hits = new[] { "honeypot_touched" };
        var a = new ScoreResult(42, hits, "heuristic-1", FraudFeatureVector.FeatureSetVersion);
        var b = new ScoreResult(42, hits, "heuristic-1", FraudFeatureVector.FeatureSetVersion);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void WithCloning_ChangesOnlyTargetedMember()
    {
        var original = new ScoreResult(10, Array.Empty<string>(), "heuristic-1", 1);

        var cloned = original with { Score = 85 };

        Assert.Equal(85, cloned.Score);
        Assert.Equal(original.RuleHits, cloned.RuleHits);
        Assert.Equal(original.ScorerVersion, cloned.ScorerVersion);
        Assert.Equal(original.FeatureSetVersion, cloned.FeatureSetVersion);
        Assert.NotEqual(original, cloned);
        Assert.Equal(10, original.Score);
    }
}
