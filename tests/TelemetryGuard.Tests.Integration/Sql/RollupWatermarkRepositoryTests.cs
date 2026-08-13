using Dapper;
using Microsoft.Extensions.DependencyInjection;
using TelemetryGuard.Data.Repositories;

namespace TelemetryGuard.Tests.Integration.Sql;

/// <summary>
/// DAT-06 IRollupWatermarkRepository against real migrated SQL Server. Rollup names
/// are fresh per test so tests run in any order.
/// </summary>
[Collection("sqlserver")]
[Trait("Category", "Integration")]
public sealed class RollupWatermarkRepositoryTests(SqlServerFixture fx)
{
    private static string FreshRollupName() => $"rollup-{Guid.NewGuid():N}";

    [Fact]
    public async Task GetAsync_returns_null_before_first_set()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRollupWatermarkRepository>();

        Assert.Null(await repo.GetAsync(FreshRollupName(), CancellationToken.None));
    }

    [Fact]
    public async Task SetAsync_then_GetAsync_roundtrips_and_overwrites()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRollupWatermarkRepository>();

        var name = FreshRollupName();
        // Millisecond precision matches the column's datetime2(3) — exact round-trip.
        var first = new DateTime(2024, 8, 1, 12, 30, 45, 123, DateTimeKind.Utc);
        await repo.SetAsync(name, first, CancellationToken.None);
        Assert.Equal(first, await repo.GetAsync(name, CancellationToken.None));

        var second = first.AddHours(5);
        await repo.SetAsync(name, second, CancellationToken.None); // MERGE overwrites
        Assert.Equal(second, await repo.GetAsync(name, CancellationToken.None));

        // Overwrite kept a single row (MERGE matched, no duplicate).
        await using var conn = await fx.OpenAsync(SqlServerFixture.TenantA);
        var rows = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.RollupWatermarks WHERE TenantId = @TenantId AND RollupName = @Name",
            new { TenantId = SqlServerFixture.TenantA, Name = name });
        Assert.Equal(1, rows);
    }
}
