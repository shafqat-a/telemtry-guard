using System.Security.Cryptography;
using System.Text;
using TelemetryGuard.Core.Time;
using TelemetryGuard.RiskEngine.Velocity;

namespace TelemetryGuard.Tests.Unit.Velocity;

/// <summary>Pure unit tests for the velocity store's bucket math and UA hashing —
/// no Redis involved (accesses internals via InternalsVisibleTo).</summary>
public sealed class RedisVelocityStoreUnitTests
{
    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    [Fact]
    public void UaHash_IsDeterministic()
    {
        const string ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";
        Assert.Equal(RedisVelocityStore.UaHash(ua), RedisVelocityStore.UaHash(ua));
    }

    [Fact]
    public void UaHash_IsLowercaseHexOfFirst8Sha256Bytes()
    {
        const string ua = "curl/8.5.0";
        var expected = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(ua)).AsSpan(0, 8)).ToLowerInvariant();

        var actual = RedisVelocityStore.UaHash(ua);

        Assert.Equal(expected, actual);
        Assert.Equal(16, actual.Length);
        Assert.Equal(actual, actual.ToLowerInvariant());
    }

    [Fact]
    public void UaHash_DiffersForDifferentInputs()
    {
        Assert.NotEqual(RedisVelocityStore.UaHash("ua-one"), RedisVelocityStore.UaHash("ua-two"));
    }

    [Fact]
    public void MinuteBucket_SameWithinMinute_AdvancesAtBoundary()
    {
        var clock = new FakeClock { UtcNow = new DateTimeOffset(2026, 1, 1, 12, 5, 0, TimeSpan.Zero) };
        var atStart = RedisVelocityStore.MinuteBucketOf(clock.UtcNow);

        Assert.Equal(clock.UtcNow.ToUnixTimeSeconds() / 60, atStart);

        clock.UtcNow = new DateTimeOffset(2026, 1, 1, 12, 5, 59, TimeSpan.Zero);
        Assert.Equal(atStart, RedisVelocityStore.MinuteBucketOf(clock.UtcNow));

        clock.UtcNow = new DateTimeOffset(2026, 1, 1, 12, 6, 0, TimeSpan.Zero);
        Assert.Equal(atStart + 1, RedisVelocityStore.MinuteBucketOf(clock.UtcNow));
    }

    [Fact]
    public void HourBucket_SameWithinHour_AdvancesAtBoundary()
    {
        var clock = new FakeClock { UtcNow = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero) };
        var atStart = RedisVelocityStore.HourBucketOf(clock.UtcNow);

        Assert.Equal(clock.UtcNow.ToUnixTimeSeconds() / 3600, atStart);

        clock.UtcNow = new DateTimeOffset(2026, 1, 1, 12, 59, 59, TimeSpan.Zero);
        Assert.Equal(atStart, RedisVelocityStore.HourBucketOf(clock.UtcNow));

        clock.UtcNow = new DateTimeOffset(2026, 1, 1, 13, 0, 0, TimeSpan.Zero);
        Assert.Equal(atStart + 1, RedisVelocityStore.HourBucketOf(clock.UtcNow));
    }
}
