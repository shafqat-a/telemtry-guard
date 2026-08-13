using Microsoft.Extensions.DependencyInjection;
using TelemetryGuard.Data.Models;
using TelemetryGuard.Data.Repositories;

namespace TelemetryGuard.Tests.Integration.Sql;

/// <summary>
/// DAT-05 ISiteRepository against real migrated SQL Server. dbo.Sites is an
/// RLS-EXEMPT resolution table, so tenant scoping here rests entirely on the
/// repository's explicit TenantId = @TenantId predicates (D11.3) — which is exactly
/// what ListAsync_returns_only_current_tenant_sites proves.
/// </summary>
[Collection("sqlserver")]
[Trait("Category", "Integration")]
public sealed class SiteRepositoryTests(SqlServerFixture fx)
{
    private static string FreshKey() => $"sk-{Guid.NewGuid():N}";

    private static SiteRecord NewSite(Guid tenantId, string siteKey, string domain = "shop.example.com",
        string integrationMode = "js")
        => new(tenantId, siteKey, domain, integrationMode, DateTime.UtcNow);

    [Fact]
    public async Task CreateAsync_then_GetBySiteKeyAsync_roundtrips()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISiteRepository>();

        var key = FreshKey();
        await repo.CreateAsync(NewSite(SqlServerFixture.TenantA, key, "roundtrip.example.com", "pixel"),
            CancellationToken.None);

        var site = await repo.GetBySiteKeyAsync(key, CancellationToken.None);
        Assert.NotNull(site);
        Assert.Equal(SqlServerFixture.TenantA, site.TenantId);
        Assert.Equal(key, site.SiteKey);
        Assert.Equal("roundtrip.example.com", site.Domain);
        Assert.Equal("pixel", site.IntegrationMode);
        Assert.True(site.CreatedUtc > DateTime.UtcNow.AddMinutes(-10)); // DB-assigned

        Assert.Null(await repo.GetBySiteKeyAsync(FreshKey(), CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_rejects_ambient_tenant_mismatch()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISiteRepository>();

        var key = FreshKey();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.CreateAsync(NewSite(SqlServerFixture.TenantB, key), CancellationToken.None));

        // The guard throws before SQL executes: nothing was inserted.
        Assert.Null(await repo.GetBySiteKeyAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task ListAsync_returns_only_current_tenant_sites()
    {
        var keyA = FreshKey();
        var keyB = FreshKey();

        await using (var providerA = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA))
        using (var scopeA = providerA.CreateScope())
        {
            await scopeA.ServiceProvider.GetRequiredService<ISiteRepository>()
                .CreateAsync(NewSite(SqlServerFixture.TenantA, keyA), CancellationToken.None);
        }

        await using (var providerB = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantB))
        using (var scopeB = providerB.CreateScope())
        {
            await scopeB.ServiceProvider.GetRequiredService<ISiteRepository>()
                .CreateAsync(NewSite(SqlServerFixture.TenantB, keyB), CancellationToken.None);
        }

        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var listedAsA = await scope.ServiceProvider.GetRequiredService<ISiteRepository>()
            .ListAsync(CancellationToken.None);

        Assert.Contains(listedAsA, s => s.SiteKey == keyA);
        Assert.DoesNotContain(listedAsA, s => s.SiteKey == keyB);
        Assert.All(listedAsA, s => Assert.Equal(SqlServerFixture.TenantA, s.TenantId));
    }

    [Fact]
    public async Task UpdateAsync_changes_domain_and_mode()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISiteRepository>();

        var key = FreshKey();
        await repo.CreateAsync(NewSite(SqlServerFixture.TenantA, key, "old.example.com", "js"),
            CancellationToken.None);

        Assert.True(await repo.UpdateAsync(key, "new.example.com", "pixel", CancellationToken.None));

        var updated = await repo.GetBySiteKeyAsync(key, CancellationToken.None);
        Assert.Equal("new.example.com", updated!.Domain);
        Assert.Equal("pixel", updated.IntegrationMode);

        // Unknown key updates nothing.
        Assert.False(await repo.UpdateAsync(FreshKey(), "x.example.com", "js", CancellationToken.None));
    }

    [Fact]
    public async Task UpdateAsync_rejects_bad_mode()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISiteRepository>();

        var key = FreshKey();
        await repo.CreateAsync(NewSite(SqlServerFixture.TenantA, key, "keep.example.com", "js"),
            CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repo.UpdateAsync(key, "changed.example.com", "iframe", CancellationToken.None));

        // Guard threw before SQL: row unchanged.
        var site = await repo.GetBySiteKeyAsync(key, CancellationToken.None);
        Assert.Equal("keep.example.com", site!.Domain);
        Assert.Equal("js", site.IntegrationMode);
    }

    [Fact]
    public async Task DeleteAsync_removes_and_returns_true_then_false()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISiteRepository>();

        var key = FreshKey();
        await repo.CreateAsync(NewSite(SqlServerFixture.TenantA, key), CancellationToken.None);

        Assert.True(await repo.DeleteAsync(key, CancellationToken.None));
        Assert.Null(await repo.GetBySiteKeyAsync(key, CancellationToken.None));
        Assert.False(await repo.DeleteAsync(key, CancellationToken.None));
    }
}
