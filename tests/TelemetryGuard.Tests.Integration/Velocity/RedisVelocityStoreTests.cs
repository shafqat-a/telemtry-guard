using System.Diagnostics;
using StackExchange.Redis;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Core.Time;
using TelemetryGuard.RiskEngine.Velocity;
using Testcontainers.Redis;

namespace TelemetryGuard.Tests.Integration.Velocity;

/// <summary>Shared Redis container + multiplexer for the velocity-store tests.</summary>
public sealed class RedisVelocityFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .Build();

    public IConnectionMultiplexer Mux { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var options = ConfigurationOptions.Parse(_container.GetConnectionString());
        options.AllowAdmin = true; // for FLUSHALL in the key-isolation test only
        Mux = await ConnectionMultiplexer.ConnectAsync(options);
    }

    public async Task DisposeAsync()
    {
        Mux?.Dispose();
        await _container.DisposeAsync();
    }
}

public sealed class RedisVelocityStoreTests : IClassFixture<RedisVelocityFixture>
{
    // FND-04's TenantId wraps a GUID; its canonical string form ("D", lowercase) is what
    // lands in the key prefix. "tenant-a"/"tenant-b" from the task's acceptance criteria
    // are therefore represented by two fixed GUIDs.
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb");

    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero); // minute- and hour-aligned

    private readonly RedisVelocityFixture _fixture;

    public RedisVelocityStoreTests(RedisVelocityFixture fixture) => _fixture = fixture;

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = BaseTime;
    }

    private sealed class FakeTenantContext : ITenantContext
    {
        public FakeTenantContext(Guid tenant) => TenantId = new TenantId(tenant);

        public TenantId TenantId { get; }

        public string? SiteKey => null;

        public bool IsResolved => true;
    }

    private RedisVelocityStore Store(FakeClock clock, Guid? tenant = null)
        => new(_fixture.Mux, new FakeTenantContext(tenant ?? TenantA), clock);

    private static string UniqueIp() => $"203.0.{Random.Shared.Next(0, 254)}.{Random.Shared.Next(1, 254)}";

    // Criterion 1: three clicks in the same minute.
    [Fact]
    public async Task ThreeClicksInSameMinute_ReadsThree()
    {
        var clock = new FakeClock { UtcNow = BaseTime.AddSeconds(10) };
        var store = Store(clock);
        var ip = UniqueIp();

        for (var i = 0; i < 3; i++)
        {
            await store.RecordClickAsync(ip, "Mozilla/5.0", null, CancellationToken.None);
        }

        var snapshot = await store.ReadAsync(ip, null, CancellationToken.None);
        Assert.Equal(3, snapshot.IpClicksLastMin);
    }

    // Criterion 2: sliding window — 10 clicks at minute M, read at M+1 second 30 → 5.
    [Fact]
    public async Task SlidingWindow_WeighsPreviousMinuteByRemainingFraction()
    {
        var clock = new FakeClock { UtcNow = BaseTime };
        var store = Store(clock);
        var ip = UniqueIp();

        for (var i = 0; i < 10; i++)
        {
            await store.RecordClickAsync(ip, null, null, CancellationToken.None);
        }

        clock.UtcNow = BaseTime.AddSeconds(90); // minute M+1, second 30
        var snapshot = await store.ReadAsync(ip, null, CancellationToken.None);
        Assert.Equal(5, snapshot.IpClicksLastMin); // round(10 * 30/60)
    }

    // Criterion 3: clock advanced >= 2 minutes → 0 (bucket exclusion).
    [Fact]
    public async Task ClockAdvancedTwoMinutes_CountReturnsToZero()
    {
        var clock = new FakeClock { UtcNow = BaseTime };
        var store = Store(clock);
        var ip = UniqueIp();

        for (var i = 0; i < 10; i++)
        {
            await store.RecordClickAsync(ip, null, null, CancellationToken.None);
        }

        clock.UtcNow = BaseTime.AddMinutes(2);
        var snapshot = await store.ReadAsync(ip, null, CancellationToken.None);
        Assert.Equal(0, snapshot.IpClicksLastMin);
    }

    // Criterion 4: click-id dedupe via SET NX.
    [Fact]
    public async Task ClickIdDedupe_FirstFresh_SecondReplay_NullIsNull()
    {
        var clock = new FakeClock();
        var store = Store(clock);
        var ip = UniqueIp();

        var first = await store.RecordClickAsync(ip, null, "g123", CancellationToken.None);
        var second = await store.RecordClickAsync(ip, null, "g123", CancellationToken.None);
        var none = await store.RecordClickAsync(ip, null, null, CancellationToken.None);

        Assert.True(first);
        Assert.False(second);
        Assert.Null(none);
    }

    // Criterion 5: HLL cardinalities are exact at tiny counts.
    [Fact]
    public async Task SessionHlls_CountDistinctVisitorsSessionsAndUas()
    {
        var clock = new FakeClock();
        var store = Store(clock);
        var ip = UniqueIp();

        // Visitor V1: 3 sessions; visitors V2/V3: one each. Two distinct UAs overall.
        await store.RecordSessionAsync(ip, "ua-A", "V1", "s1", false, CancellationToken.None);
        await store.RecordSessionAsync(ip, "ua-A", "V1", "s2", false, CancellationToken.None);
        await store.RecordSessionAsync(ip, "ua-B", "V1", "s3", false, CancellationToken.None);
        await store.RecordSessionAsync(ip, "ua-A", "V2", "s4", false, CancellationToken.None);
        await store.RecordSessionAsync(ip, "ua-B", "V3", "s5", false, CancellationToken.None);

        var snapshot = await store.ReadAsync(ip, "V1", CancellationToken.None);

        Assert.Equal(3, snapshot.DeviceIdsThisIpHour);
        Assert.Equal(3, snapshot.DeviceSessionsLastHour);
        Assert.Equal(2, snapshot.IpDistinctUasLastHour);
    }

    // Criterion 6: fpz counter + 7-day TTL.
    [Fact]
    public async Task StorageAgeZeroRepeat_CountsAndCarriesSevenDayTtl()
    {
        var clock = new FakeClock();
        var store = Store(clock);
        var ip = UniqueIp();
        const string visitor = "V-fpz";

        await store.RecordSessionAsync(ip, null, visitor, "s1", true, CancellationToken.None);
        await store.RecordSessionAsync(ip, null, visitor, "s2", true, CancellationToken.None);

        var snapshot = await store.ReadAsync(ip, visitor, CancellationToken.None);
        Assert.Equal(2, snapshot.StorageAgeZeroRepeat);

        var db = _fixture.Mux.GetDatabase();
        var ttl = db.KeyTimeToLive($"t:{new TenantId(TenantA)}:fpz:{visitor}");
        Assert.NotNull(ttl);
        Assert.True(ttl <= TimeSpan.FromDays(7), $"TTL {ttl} should be <= 7 days");
        Assert.True(ttl > TimeSpan.FromDays(6), $"TTL {ttl} should be > 6 days");
    }

    // Criterion 7: tenant isolation — tenant-b sees zeros; every key is tenant-prefixed.
    // SCAN is used from the TEST only (never production code).
    [Fact]
    public async Task TenantIsolation_OtherTenantReadsZeros_AllKeysArePrefixed()
    {
        var server = _fixture.Mux.GetServer(_fixture.Mux.GetEndPoints()[0]);
        await server.FlushAllDatabasesAsync();

        var clock = new FakeClock();
        var storeA = Store(clock, TenantA);
        var storeB = Store(clock, TenantB);
        var ip = UniqueIp();

        await storeA.RecordClickAsync(ip, "ua-A", "gclid-iso", CancellationToken.None);
        await storeA.RecordSessionAsync(ip, "ua-A", "V1", "s1", true, CancellationToken.None);

        var snapshotA = await storeA.ReadAsync(ip, "V1", CancellationToken.None);
        Assert.True(snapshotA.IpClicksLastMin > 0);

        var snapshotB = await storeB.ReadAsync(ip, "V1", CancellationToken.None);
        Assert.Equal(0, snapshotB.IpClicksLastMin);
        Assert.Equal(0, snapshotB.IpDistinctUasLastHour);
        Assert.Equal(0, snapshotB.DeviceSessionsLastHour);
        Assert.Equal(0, snapshotB.DeviceIdsThisIpHour);
        Assert.Equal(0, snapshotB.StorageAgeZeroRepeat);

        var prefixA = $"t:{new TenantId(TenantA)}:";
        var prefixB = $"t:{new TenantId(TenantB)}:";
        var keys = server.Keys(pattern: "*").Select(k => (string)k!).ToList();
        Assert.NotEmpty(keys);
        Assert.All(keys, k => Assert.True(
            k.StartsWith(prefixA, StringComparison.Ordinal) || k.StartsWith(prefixB, StringComparison.Ordinal),
            $"Key '{k}' is not tenant-prefixed"));
    }

    // Criterion 8: single round trip per ReadAsync — 100 sequential reads well under 2 s.
    [Fact]
    public async Task HundredSequentialReads_CompleteUnderTwoSeconds()
    {
        var clock = new FakeClock();
        var store = Store(clock);
        var ip = UniqueIp();
        await store.RecordSessionAsync(ip, "ua", "V1", "s1", true, CancellationToken.None);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
        {
            await store.ReadAsync(ip, "V1", CancellationToken.None);
        }

        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"100 sequential reads took {sw.Elapsed.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public async Task EmptyIp_Throws()
    {
        var store = Store(new FakeClock());

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.RecordClickAsync("", null, null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.RecordSessionAsync(" ", null, null, "s1", false, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.ReadAsync("", null, CancellationToken.None));
    }
}
