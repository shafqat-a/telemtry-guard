using TelemetryGuard.Core.Time;

namespace TelemetryGuard.Tests.Unit.Core;

public class SystemClockTests
{
    [Fact]
    public void Instance_UtcNow_IsCloseToSystemUtcNow()
    {
        var clockNow = SystemClock.Instance.UtcNow;
        var systemNow = DateTimeOffset.UtcNow;

        Assert.True((systemNow - clockNow).Duration() < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Instance_UtcNow_HasZeroOffset()
    {
        Assert.Equal(TimeSpan.Zero, SystemClock.Instance.UtcNow.Offset);
    }
}
