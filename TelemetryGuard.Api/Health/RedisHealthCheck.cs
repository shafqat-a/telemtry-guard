using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace TelemetryGuard.Api.Health;

/// <summary>Readiness check: PINGs Redis via the shared multiplexer.</summary>
public sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        try { await redis.GetDatabase().PingAsync(); return HealthCheckResult.Healthy(); }
        catch (Exception ex) { return HealthCheckResult.Unhealthy("redis", ex); }
    }
}
