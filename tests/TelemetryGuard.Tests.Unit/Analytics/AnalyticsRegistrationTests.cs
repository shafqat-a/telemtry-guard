using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Analytics.ClickHouse;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Core.Time;

namespace TelemetryGuard.Tests.Unit.Analytics;

/// <summary>
/// ANA-05: the D7 provider switch (<see cref="AnalyticsServiceCollectionExtensions"/>)
/// against a real ServiceCollection + in-memory configuration.
/// </summary>
public class AnalyticsRegistrationTests
{
    private const string ValidConnectionString =
        "Host=localhost;Port=8123;Database=telemetry_guard;Username=tg;Password=tg-dev-password";

    private sealed class StubTenantContext : ITenantContext
    {
        public TenantId TenantId { get; } = new(Guid.NewGuid());
        public string? SiteKey => null;
        public bool IsResolved => true;
    }

    private sealed class StubClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private static IConfiguration BuildConfig(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => p.Value))
            .Build();

    private static ServiceCollection NewServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ITenantContext, StubTenantContext>();
        services.AddSingleton<IClock, StubClock>();
        return services;
    }

    [Fact]
    public void ClickHouseProvider_RegistersSingletonSinks_ForwardedAsHostedServices()
    {
        var cfg = BuildConfig(
            ("Analytics:Provider", "ClickHouse"),
            ("Analytics:ClickHouse:ConnectionString", ValidConnectionString));
        var services = NewServices();

        services.AddTelemetryGuardAnalytics(cfg);

        using var root = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });

        var eventSink = root.GetRequiredService<IEventSink>();
        var labelSink = root.GetRequiredService<ILabelSink>();
        Assert.IsType<ClickHouseEventSink>(eventSink);
        Assert.IsType<ClickHouseLabelSink>(labelSink);

        // Singleton: same instance on every resolution, including from a scope.
        Assert.Same(eventSink, root.GetRequiredService<IEventSink>());
        Assert.Same(labelSink, root.GetRequiredService<ILabelSink>());
        using (var scope = root.CreateScope())
        {
            Assert.Same(eventSink, scope.ServiceProvider.GetRequiredService<IEventSink>());
            Assert.Same(labelSink, scope.ServiceProvider.GetRequiredService<ILabelSink>());
        }

        // IHostedService forwards to the SAME instances — a second instance would
        // split the in-memory queue from the flusher.
        var hosted = root.GetServices<IHostedService>().ToList();
        Assert.Contains(hosted, h => ReferenceEquals(h, eventSink));
        Assert.Contains(hosted, h => ReferenceEquals(h, labelSink));
    }

    [Fact]
    public void ClickHouseProvider_QueriesAreScoped_NotResolvableFromRoot()
    {
        var cfg = BuildConfig(
            ("Analytics:Provider", "ClickHouse"),
            ("Analytics:ClickHouse:ConnectionString", ValidConnectionString));
        var services = NewServices();

        services.AddTelemetryGuardAnalytics(cfg);

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IAnalyticsQueries));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);

        using var root = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });

        using (var scope = root.CreateScope())
        {
            var queries = scope.ServiceProvider.GetRequiredService<IAnalyticsQueries>();
            Assert.IsType<ClickHouseAnalyticsQueries>(queries);
        }

        // NOT a singleton: root resolution of the scoped service must fail.
        Assert.Throws<InvalidOperationException>(() => root.GetRequiredService<IAnalyticsQueries>());
    }

    [Fact]
    public void KustoProvider_ThrowsNotSupported_AtRegistration()
    {
        var cfg = BuildConfig(("Analytics:Provider", "Kusto"));
        var services = NewServices();

        var ex = Assert.Throws<NotSupportedException>(
            () => services.AddTelemetryGuardAnalytics(cfg));
        Assert.Contains("Kusto", ex.Message);
    }

    [Fact]
    public void UnknownProvider_ThrowsInvalidOperation_WithProviderName()
    {
        var cfg = BuildConfig(("Analytics:Provider", "Postgres"));
        var services = NewServices();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddTelemetryGuardAnalytics(cfg));
        Assert.Contains("Postgres", ex.Message);
    }

    [Fact]
    public void MissingProvider_ThrowsAtRegistration()
    {
        var cfg = BuildConfig(); // no Analytics section at all
        var services = NewServices();

        Assert.Throws<InvalidOperationException>(
            () => services.AddTelemetryGuardAnalytics(cfg));
    }

    [Fact]
    public async Task ClickHouseProvider_EmptyConnectionString_FailsHostStart_ViaValidateOnStart()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Analytics:Provider"] = "ClickHouse",
            ["Analytics:ClickHouse:ConnectionString"] = ""
        });
        builder.Services.AddScoped<ITenantContext, StubTenantContext>();
        builder.Services.AddSingleton<IClock, StubClock>();
        builder.Services.AddTelemetryGuardAnalytics(builder.Configuration);

        using var host = builder.Build();
        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains("ConnectionString", ex.Message);
    }
}
