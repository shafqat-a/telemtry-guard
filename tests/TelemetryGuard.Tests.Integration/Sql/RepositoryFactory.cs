using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Data;

namespace TelemetryGuard.Tests.Integration.Sql;

/// <summary>
/// Builds the REAL production object graph for a given ambient tenant: FND-04's
/// TenantContext (resolved exactly once), an IConfiguration pointing
/// ConnectionStrings:Main / ConnectionStrings:Redis at the fixture containers, and
/// AddTelemetryGuardData(). The DI route is required — repository implementations
/// are internal, so tests can only reach them through the registered interfaces,
/// exactly like production code does.
/// </summary>
public static class RepositoryFactory
{
    /// <param name="redisConnectionString">Override for ConnectionStrings:Redis —
    /// used by the whitelist Redis-outage test to point at a closed port with
    /// abortConnect=false; defaults to the fixture's Redis container.</param>
    public static ServiceProvider BuildServices(
        SqlServerFixture fx,
        Guid tenantId,
        ILabelSink? labelSink = null,
        string? redisConnectionString = null)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Main"] = fx.ConnectionString,
            ["ConnectionStrings:Redis"] = redisConnectionString ?? fx.RedisConnectionString,
        }).Build();

        var ctx = new TenantContext();
        ctx.Resolve(new TenantId(tenantId));

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(cfg);
        services.AddLogging();
        services.AddSingleton<ITenantContext>(ctx);
        if (labelSink is not null) services.AddSingleton(labelSink);
        services.AddTelemetryGuardData();
        return services.BuildServiceProvider();
    }
}
