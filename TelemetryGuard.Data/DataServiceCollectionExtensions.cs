using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;
using TelemetryGuard.Data.Tenancy;

namespace TelemetryGuard.Data;

public static class DataServiceCollectionExtensions
{
    /// <summary>Registers the connection factories and the tenant resolver.
    /// Repositories (DAT-05/06/07) extend this method with their own registrations.</summary>
    public static IServiceCollection AddTelemetryGuardData(this IServiceCollection services)
    {
        services.TryAddScoped<ITenantConnectionFactory, TenantConnectionFactory>();
        services.TryAddSingleton<IResolutionConnectionFactory, ResolutionConnectionFactory>();
        services.TryAddSingleton<ISystemConnectionFactory, SystemConnectionFactory>();
        services.AddMemoryCache();
        services.TryAddSingleton<ITenantResolver, SqlTenantResolver>();

        // DAT-05 config repositories — scoped: they depend on the scoped ITenantContext
        // via ITenantConnectionFactory.
        services.TryAddScoped<Repositories.ITenantRepository, Repositories.TenantRepository>();
        services.TryAddScoped<Repositories.ILabelSubmissionRepository, Repositories.LabelSubmissionRepository>();
        services.TryAddScoped<Repositories.IWebhookOutboxRepository, Repositories.WebhookOutboxRepository>();
        services.TryAddScoped<Repositories.IMarketIqOutboxRepository, Repositories.MarketIqOutboxRepository>();
        services.TryAddScoped<Repositories.ISiteRepository, Repositories.SiteRepository>();
        services.TryAddScoped<Repositories.ICampaignRepository, Repositories.CampaignRepository>();

        // DAT-06 summary/watermark repositories — scoped: they depend on the scoped
        // ITenantContext via ITenantConnectionFactory.
        services.TryAddScoped<Repositories.IVerdictSummaryRepository, Repositories.VerdictSummaryRepository>();
        services.TryAddScoped<Repositories.IRollupWatermarkRepository, Repositories.RollupWatermarkRepository>();

        // P2-01 publisher/site aggregate repository — scoped like the DAT-06 pair above.
        services.TryAddScoped<Repositories.IPublisherSummaryRepository, Repositories.PublisherSummaryRepository>();

        // API-06 exclusion-queue writer — DAT-06 ships the dbo.ExclusionQueue table
        // only ("API-06 (writer) owns its access path"); scoped like the repositories above.
        services.TryAddScoped<Repositories.IExclusionQueueRepository, Repositories.ExclusionQueueRepository>();

        // INT-02 approval-queue read/transition path (pending -> approved | rejected)
        // + dbo.EnforcementAudit — scoped like the repositories above.
        services.TryAddScoped<Repositories.IEnforcementQueueRepository, Repositories.EnforcementQueueRepository>();

        // DAT-07 whitelist repository — scoped (scoped ITenantContext). Redis multiplexer:
        // registered only if the host has not already added one (RSK-03's AddVelocityStore
        // also TryAdds it — first registration wins, one multiplexer per process).
        // Config key: ConnectionStrings:Redis.
        services.TryAddScoped<Repositories.IWhitelistRepository, Repositories.WhitelistRepository>();
        services.TryAddSingleton<IConnectionMultiplexer>(sp =>
            ConnectionMultiplexer.Connect(
                sp.GetRequiredService<IConfiguration>().GetConnectionString("Redis") ?? "localhost:6379"));

        // P2-02 model registry — SINGLETON (platform table, no ambient tenant; reads go
        // through ISystemConnectionFactory, never a request-path connection).
        services.TryAddSingleton<Repositories.IModelRegistryRepository, Repositories.ModelRegistryRepository>();
        return services;
    }
}
