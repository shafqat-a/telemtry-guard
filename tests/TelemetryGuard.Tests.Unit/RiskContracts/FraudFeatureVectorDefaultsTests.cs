using TelemetryGuard.RiskEngine.Contracts;

namespace TelemetryGuard.Tests.Unit.RiskContracts;

public class FraudFeatureVectorDefaultsTests
{
    [Fact]
    public void SdkDerivedFloats_DefaultToNaN()
    {
        var v = new FraudFeatureVector();

        Assert.True(float.IsNaN(v.MousePathLinearity));
        Assert.True(float.IsNaN(v.StdInterEventMs));
        Assert.True(float.IsNaN(v.MeanInterEventMs));
        Assert.True(float.IsNaN(v.StorageAgeZeroRepeat));
        Assert.True(float.IsNaN(v.TimeOnPageSec));
        Assert.True(float.IsNaN(v.FormFillTimeSec));
        Assert.True(float.IsNaN(v.FirstInteractionDelayMs));
        Assert.True(float.IsNaN(v.InputEventCount));
        Assert.True(float.IsNaN(v.ScrollEvents));
        Assert.True(float.IsNaN(v.PagesViewed));
    }

    [Fact]
    public void VelocityCounters_DefaultToZero()
    {
        var v = new FraudFeatureVector();

        Assert.Equal(0, v.IpClicksLastMin);
        Assert.Equal(0L, v.DeviceSessionsLastHour);
        Assert.Equal(0L, v.IpDistinctUasLastHour);
        Assert.Equal(0L, v.DeviceIdsThisIpHour);
        Assert.Equal(0f, v.IpReputationBad);
    }

    [Fact]
    public void AllNullableBoolProperties_DefaultToNull()
    {
        var v = new FraudFeatureVector();

        var nullableBoolProps = typeof(FraudFeatureVector)
            .GetProperties()
            .Where(p => p.PropertyType == typeof(bool?))
            .ToList();

        Assert.NotEmpty(nullableBoolProps);

        foreach (var prop in nullableBoolProps)
        {
            Assert.Null(prop.GetValue(v));
        }
    }

    [Fact]
    public void ContextDefaults_AreCorrect()
    {
        var v = new FraudFeatureVector();

        Assert.Equal(AsnType.Unknown, v.AsnType);
        Assert.Equal(ChallengeOutcome.NotChallenged, v.ChallengeOutcome);
        Assert.False(v.HasJsBeacon);
        Assert.False(v.IsPrivateRelay);
        Assert.False(v.IsPaidClick);
        Assert.False(v.ReferrerMissing);
    }

    [Fact]
    public void FeatureSetVersion_IsOne()
    {
        Assert.Equal(1, FraudFeatureVector.FeatureSetVersion);
    }
}
