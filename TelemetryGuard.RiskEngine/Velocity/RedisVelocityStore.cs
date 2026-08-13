using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Core.Time;

namespace TelemetryGuard.RiskEngine.Velocity;

/// <summary>
/// Redis implementation of <see cref="IVelocityStore"/> (D5: StackExchange.Redis directly,
/// no provider abstraction). Every key is built by <see cref="K"/> — the single key
/// constructor — and therefore always carries the t:{tenantId}: prefix from the ambient
/// <see cref="ITenantContext"/> (D11). Each public method performs exactly one network
/// round trip via one CreateBatch/Execute pair (&lt; 50 ms scoring budget, D3).
/// Register as scoped: the tenant context is scoped per request.
/// </summary>
public sealed class RedisVelocityStore : IVelocityStore
{
    private const int MinuteKeyTtlSeconds = 180;
    private const int HllTtlSeconds = 7200;
    private const int ClickIdTtlSeconds = 86400;

    private readonly IConnectionMultiplexer _mux;
    private readonly ITenantContext _tenant;
    private readonly IClock _clock;

    public RedisVelocityStore(IConnectionMultiplexer mux, ITenantContext tenant, IClock clock)
    {
        _mux = mux;
        _tenant = tenant;
        _clock = clock;
    }

    public async Task<bool?> RecordClickAsync(string ip, string? userAgent, string? clickId, CancellationToken ct)
    {
        RequireIp(ip);
        ct.ThrowIfCancellationRequested();

        var ua = NormalizeUa(userAgent);
        var now = _clock.UtcNow;
        var minB = Render(MinuteBucketOf(now));
        var hourB = Render(HourBucketOf(now));

        var batch = _mux.GetDatabase().CreateBatch();
        var pending = new List<Task>(5);

        var minuteKey = K($"v:ipm:{ip}:{minB}");
        pending.Add(batch.StringIncrementAsync(minuteKey));
        pending.Add(batch.KeyExpireAsync(minuteKey, TimeSpan.FromSeconds(MinuteKeyTtlSeconds)));

        if (ua is not null)
        {
            var uaKey = K($"v:ipua:{ip}:{hourB}");
            pending.Add(batch.HyperLogLogAddAsync(uaKey, UaHash(ua)));
            pending.Add(batch.KeyExpireAsync(uaKey, TimeSpan.FromSeconds(HllTtlSeconds)));
        }

        Task<bool>? setNx = null;
        if (!string.IsNullOrWhiteSpace(clickId))
        {
            setNx = batch.StringSetAsync(
                K($"cid:{clickId}"), "1", TimeSpan.FromSeconds(ClickIdTtlSeconds), When.NotExists);
            pending.Add(setNx);
        }

        batch.Execute();
        await Task.WhenAll(pending).ConfigureAwait(false);

        return setNx is null ? null : setNx.Result;
    }

    public async Task RecordSessionAsync(string ip, string? userAgent, string? visitorId, string sessionId,
                                         bool storageAgeZero, CancellationToken ct)
    {
        RequireIp(ip);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("Session id must not be null or empty.", nameof(sessionId));
        }

        ct.ThrowIfCancellationRequested();

        var ua = NormalizeUa(userAgent);
        var vid = string.IsNullOrWhiteSpace(visitorId) ? null : visitorId;
        var hourB = Render(HourBucketOf(_clock.UtcNow));

        var batch = _mux.GetDatabase().CreateBatch();
        var pending = new List<Task>(8);

        if (vid is not null)
        {
            var devKey = K($"v:dev:{vid}:{hourB}");
            pending.Add(batch.HyperLogLogAddAsync(devKey, sessionId));
            pending.Add(batch.KeyExpireAsync(devKey, TimeSpan.FromSeconds(HllTtlSeconds)));

            var ipDevKey = K($"v:ipdev:{ip}:{hourB}");
            pending.Add(batch.HyperLogLogAddAsync(ipDevKey, vid));
            pending.Add(batch.KeyExpireAsync(ipDevKey, TimeSpan.FromSeconds(HllTtlSeconds)));
        }

        if (ua is not null)
        {
            var uaKey = K($"v:ipua:{ip}:{hourB}");
            pending.Add(batch.HyperLogLogAddAsync(uaKey, UaHash(ua)));
            pending.Add(batch.KeyExpireAsync(uaKey, TimeSpan.FromSeconds(HllTtlSeconds)));
        }

        if (storageAgeZero && vid is not null)
        {
            var fpzKey = K($"fpz:{vid}");
            pending.Add(batch.StringIncrementAsync(fpzKey));
            pending.Add(batch.KeyExpireAsync(fpzKey, TimeSpan.FromDays(7)));
        }

        batch.Execute();
        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    public async Task<VelocitySnapshot> ReadAsync(string ip, string? visitorId, CancellationToken ct)
    {
        RequireIp(ip);
        ct.ThrowIfCancellationRequested();

        var vid = string.IsNullOrWhiteSpace(visitorId) ? null : visitorId;
        var now = _clock.UtcNow;
        var unixSeconds = now.ToUnixTimeSeconds();
        var minBucket = unixSeconds / 60;
        var hourBucket = unixSeconds / 3600;
        var curMin = Render(minBucket);
        var prevMin = Render(minBucket - 1);
        var curHour = Render(hourBucket);
        var prevHour = Render(hourBucket - 1);

        var batch = _mux.GetDatabase().CreateBatch();

        var curMinTask = batch.StringGetAsync(K($"v:ipm:{ip}:{curMin}"));
        var prevMinTask = batch.StringGetAsync(K($"v:ipm:{ip}:{prevMin}"));
        var ipUaTask = batch.HyperLogLogLengthAsync(
            new RedisKey[] { K($"v:ipua:{ip}:{curHour}"), K($"v:ipua:{ip}:{prevHour}") });
        var ipDevTask = batch.HyperLogLogLengthAsync(
            new RedisKey[] { K($"v:ipdev:{ip}:{curHour}"), K($"v:ipdev:{ip}:{prevHour}") });
        Task<long>? devTask = vid is null
            ? null
            : batch.HyperLogLogLengthAsync(
                new RedisKey[] { K($"v:dev:{vid}:{curHour}"), K($"v:dev:{vid}:{prevHour}") });
        Task<RedisValue>? fpzTask = vid is null ? null : batch.StringGetAsync(K($"fpz:{vid}"));

        batch.Execute();

        var pending = new List<Task>(6) { curMinTask, prevMinTask, ipUaTask, ipDevTask };
        if (devTask is not null)
        {
            pending.Add(devTask);
        }

        if (fpzTask is not null)
        {
            pending.Add(fpzTask);
        }

        await Task.WhenAll(pending).ConfigureAwait(false);

        // Classic two-bucket sliding-window approximation.
        var current = AsLong(curMinTask.Result);
        var previous = AsLong(prevMinTask.Result);
        var secondsIntoCurrentMinute = unixSeconds % 60;
        var ipClicks = current + (long)Math.Round(
            previous * (60 - secondsIntoCurrentMinute) / 60.0, MidpointRounding.AwayFromZero);

        return new VelocitySnapshot(
            IpClicksLastMin: (int)Math.Clamp(ipClicks, 0, int.MaxValue),
            IpDistinctUasLastHour: ipUaTask.Result,
            DeviceSessionsLastHour: devTask?.Result ?? 0,
            DeviceIdsThisIpHour: ipDevTask.Result,
            StorageAgeZeroRepeat: fpzTask is null ? 0 : AsLong(fpzTask.Result));
    }

    /// <summary>The ONLY key constructor — guarantees the t:{tenantId}: prefix (D11).
    /// TenantId.ToString() is the canonical lowercase "D" GUID form (FND-04).</summary>
    private RedisKey K(string suffix) => $"t:{_tenant.TenantId}:{suffix}";

    /// <summary>Lowercase hex of the first 8 bytes of SHA-256 of the raw UA string
    /// (bounds HLL element size).</summary>
    internal static string UaHash(string userAgent)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(userAgent));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    internal static long MinuteBucketOf(DateTimeOffset utcNow) => utcNow.ToUnixTimeSeconds() / 60;

    internal static long HourBucketOf(DateTimeOffset utcNow) => utcNow.ToUnixTimeSeconds() / 3600;

    private static string Render(long bucket) => bucket.ToString(CultureInfo.InvariantCulture);

    private static string? NormalizeUa(string? userAgent)
        => string.IsNullOrWhiteSpace(userAgent) ? null : userAgent;

    private static long AsLong(RedisValue value) => value.IsNullOrEmpty ? 0L : (long)value;

    private static void RequireIp(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            throw new ArgumentException("IP must not be null or empty.", nameof(ip));
        }
    }
}
