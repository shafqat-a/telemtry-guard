using Microsoft.Extensions.DependencyInjection;
using TelemetryGuard.Data.Tenancy;

namespace TelemetryGuard.Tests.Integration.Sql;

/// <summary>
/// DAT-04 SQL resolver path against the real migrated schema: API-key and site-key
/// resolution over the RLS-exempt tables via IResolutionConnectionFactory, including
/// negative caching of unknown keys (anti-probing).
/// </summary>
[Collection("sqlserver")]
[Trait("Category", "Integration")]
public sealed class TenantResolverTests(SqlServerFixture fx)
{
    [Fact]
    public async Task ResolveApiKeyAsync_returns_tenant_and_scopes()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        var resolver = provider.GetRequiredService<ITenantResolver>();

        var resolved = await resolver.ResolveApiKeyAsync(SqlServerFixture.ApiKeyA, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(SqlServerFixture.TenantA, resolved.TenantId);
        Assert.Contains("admin", resolved.Scopes);
        Assert.Null(resolved.SiteKey); // API-key resolutions carry no site key
    }

    [Fact]
    public async Task ResolveSiteKeyAsync_returns_tenant()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        var resolver = provider.GetRequiredService<ITenantResolver>();

        var resolved = await resolver.ResolveSiteKeyAsync(SqlServerFixture.SiteKeyA, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(SqlServerFixture.TenantA, resolved.TenantId);
        Assert.Equal(SqlServerFixture.SiteKeyA, resolved.SiteKey);
        Assert.Equal("js", resolved.IntegrationMode); // seeded default
        Assert.Empty(resolved.Scopes); // site-key resolutions carry no scopes
    }

    [Fact]
    public async Task Unknown_keys_resolve_to_null_twice_second_hit_from_cache()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        var resolver = provider.GetRequiredService<ITenantResolver>();

        var unknownApiKey = $"unknown-api-key-{Guid.NewGuid():N}";
        Assert.Null(await resolver.ResolveApiKeyAsync(unknownApiKey, CancellationToken.None));
        Assert.Null(await resolver.ResolveApiKeyAsync(unknownApiKey, CancellationToken.None)); // cached negative

        var unknownSiteKey = $"unknown-{Guid.NewGuid():N}";
        Assert.Null(await resolver.ResolveSiteKeyAsync(unknownSiteKey, CancellationToken.None));
        Assert.Null(await resolver.ResolveSiteKeyAsync(unknownSiteKey, CancellationToken.None)); // cached negative
    }
}
