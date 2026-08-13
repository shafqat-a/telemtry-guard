using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace TelemetryGuard.RiskEngine.Velocity;

public static class VelocityServiceCollectionExtensions
{
    /// <summary>Registers the Redis-backed <see cref="IVelocityStore"/> (scoped — the tenant
    /// context is scoped per request). Expects <c>ConnectionStrings:Redis</c> in configuration
    /// (e.g. "localhost:6379", matching the FND-02 compose stack).</summary>
    public static IServiceCollection AddVelocityStore(this IServiceCollection services, IConfiguration config)
    {
        // Reuse an existing multiplexer registration if the host already added one.
        services.TryAddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(config.GetConnectionString("Redis")
                ?? throw new InvalidOperationException("ConnectionStrings:Redis is required")));
        services.AddScoped<IVelocityStore, RedisVelocityStore>();
        return services;
    }
}
