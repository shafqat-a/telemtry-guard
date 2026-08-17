using Microsoft.Extensions.DependencyInjection;
using TelemetryGuard.Data.Repositories;

namespace TelemetryGuard.Tests.Integration.Sql;

/// <summary>
/// DAT-05 ITenantRepository against real migrated SQL Server (per D9.4 this suite is
/// the replacement for EF's compile-time query checking — every method runs real SQL).
/// Defaults are asserted on tenant A (which no test mutates); mutation tests use
/// tenant B so the suite is order-independent.
/// </summary>
[Collection("sqlserver")]
[Trait("Category", "Integration")]
public sealed class TenantRepositoryTests(SqlServerFixture fx)
{
    [Fact]
    public async Task GetCurrentAsync_returns_seeded_tenant_defaults()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();

        var tenant = await repo.GetCurrentAsync(CancellationToken.None);

        Assert.NotNull(tenant);
        Assert.Equal(SqlServerFixture.TenantA, tenant.TenantId);
        Assert.Equal("Tenant A", tenant.Name);
        Assert.Equal((byte)0, tenant.Status);
        Assert.Equal(90, tenant.RetentionDays);      // D20 default
        Assert.Equal((byte)0, tenant.EnforcementMode); // D21 AutoEnforce default
        Assert.False(tenant.ExternalAuthority);       // REQ-07 backwards-compatible default
        Assert.Null(tenant.AllowMax);
        Assert.Null(tenant.ChallengeMax);
        Assert.Null(tenant.ObserveOnly);
        Assert.Null(tenant.PolicyUpdatedUtc);
    }

    [Fact]
    public async Task UpdateRetentionDaysAsync_persists_within_range()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantB);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();

        Assert.True(await repo.UpdateRetentionDaysAsync(45, CancellationToken.None));
        Assert.Equal(45, (await repo.GetCurrentAsync(CancellationToken.None))!.RetentionDays);

        // Boundary values are legal and overwrite the previous value.
        Assert.True(await repo.UpdateRetentionDaysAsync(30, CancellationToken.None));
        Assert.True(await repo.UpdateRetentionDaysAsync(180, CancellationToken.None));
        Assert.Equal(180, (await repo.GetCurrentAsync(CancellationToken.None))!.RetentionDays);
    }

    [Fact]
    public async Task UpdateRetentionDaysAsync_rejects_out_of_range()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();

        var before = (await repo.GetCurrentAsync(CancellationToken.None))!.RetentionDays;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repo.UpdateRetentionDaysAsync(29, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repo.UpdateRetentionDaysAsync(181, CancellationToken.None));

        // Guard throws before SQL: no DB change.
        Assert.Equal(before, (await repo.GetCurrentAsync(CancellationToken.None))!.RetentionDays);
    }

    [Fact]
    public async Task UpdateEnforcementModeAsync_persists_and_rejects_invalid()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantB);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();

        Assert.True(await repo.UpdateEnforcementModeAsync(1, CancellationToken.None));
        Assert.Equal((byte)1, (await repo.GetCurrentAsync(CancellationToken.None))!.EnforcementMode);

        Assert.True(await repo.UpdateEnforcementModeAsync(0, CancellationToken.None));
        Assert.Equal((byte)0, (await repo.GetCurrentAsync(CancellationToken.None))!.EnforcementMode);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repo.UpdateEnforcementModeAsync(2, CancellationToken.None));
        Assert.Equal((byte)0, (await repo.GetCurrentAsync(CancellationToken.None))!.EnforcementMode);
    }
}
