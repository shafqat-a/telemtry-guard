using Microsoft.Extensions.DependencyInjection;
using TelemetryGuard.Data.Models;
using TelemetryGuard.Data.Repositories;

namespace TelemetryGuard.Tests.Integration.Sql;

/// <summary>
/// DAT-05 ICampaignRepository against real migrated SQL Server, including the /c
/// hot-path projection (GetRedirectAsync) and the open-redirect guardrail
/// (LandingUrl must be an absolute http(s) URI).
/// </summary>
[Collection("sqlserver")]
[Trait("Category", "Integration")]
public sealed class CampaignRepositoryTests(SqlServerFixture fx)
{
    private static CampaignRecord NewCampaign(
        Guid tenantId, Guid campaignId, string platform = "meta",
        string landingUrl = "https://a.example.com/lp", string? geoTargets = null,
        string? externalId = null, byte status = 0)
        => new(tenantId, campaignId, platform, externalId, landingUrl, geoTargets, status, DateTime.UtcNow);

    [Fact]
    public async Task CreateAsync_then_GetAsync_roundtrips()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICampaignRepository>();

        var id = Guid.NewGuid();
        await repo.CreateAsync(
            NewCampaign(SqlServerFixture.TenantA, id, "meta", "https://a.example.com/lp",
                geoTargets: """["US","CA"]""", externalId: "ext-123"),
            CancellationToken.None);

        var got = await repo.GetAsync(id, CancellationToken.None);
        Assert.NotNull(got);
        Assert.Equal(SqlServerFixture.TenantA, got.TenantId);
        Assert.Equal(id, got.CampaignId);
        Assert.Equal("meta", got.Platform);
        Assert.Equal("ext-123", got.ExternalCampaignId);
        Assert.Equal("https://a.example.com/lp", got.LandingUrl);
        Assert.Equal("""["US","CA"]""", got.GeoTargets); // JSON round-trips verbatim
        Assert.Equal((byte)0, got.Status);
        Assert.True(got.CreatedUtc > DateTime.UtcNow.AddMinutes(-10)); // DB-assigned
    }

    [Fact]
    public async Task CreateAsync_rejects_relative_or_empty_LandingUrl()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICampaignRepository>();

        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<ArgumentException>(() => repo.CreateAsync(
            NewCampaign(SqlServerFixture.TenantA, id, landingUrl: "/relative/landing"),
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repo.CreateAsync(
            NewCampaign(SqlServerFixture.TenantA, id, landingUrl: ""),
            CancellationToken.None));

        // Guards threw before SQL executed: nothing was inserted.
        Assert.Null(await repo.GetAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task GetRedirectAsync_returns_landing_url_and_status()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICampaignRepository>();

        var redirect = await repo.GetRedirectAsync(SqlServerFixture.CampaignA1, CancellationToken.None);

        Assert.NotNull(redirect);
        Assert.Equal(SqlServerFixture.CampaignA1LandingUrl, redirect.LandingUrl);
        Assert.Equal((byte)0, redirect.Status); // 0 = Active
    }

    [Fact]
    public async Task GetRedirectAsync_unknown_campaign_returns_null()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICampaignRepository>();

        Assert.Null(await repo.GetRedirectAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task ListAsync_and_UpdateAsync_roundtrip()
    {
        await using var provider = RepositoryFactory.BuildServices(fx, SqlServerFixture.TenantA);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICampaignRepository>();

        var id = Guid.NewGuid();
        var created = NewCampaign(SqlServerFixture.TenantA, id, "tiktok", "https://a.example.com/original");
        await repo.CreateAsync(created, CancellationToken.None);

        var listed = await repo.ListAsync(CancellationToken.None);
        Assert.Contains(listed, c => c.CampaignId == id);
        Assert.All(listed, c => Assert.Equal(SqlServerFixture.TenantA, c.TenantId));

        var updated = created with
        {
            Platform = "google",
            ExternalCampaignId = "ext-upd",
            LandingUrl = "https://a.example.com/updated",
            GeoTargets = """["GB"]""",
            Status = 1,
        };
        Assert.True(await repo.UpdateAsync(updated, CancellationToken.None));

        var got = await repo.GetAsync(id, CancellationToken.None);
        Assert.Equal("google", got!.Platform);
        Assert.Equal("ext-upd", got.ExternalCampaignId);
        Assert.Equal("https://a.example.com/updated", got.LandingUrl);
        Assert.Equal("""["GB"]""", got.GeoTargets);
        Assert.Equal((byte)1, got.Status);

        // Unknown campaign updates nothing.
        Assert.False(await repo.UpdateAsync(updated with { CampaignId = Guid.NewGuid() },
            CancellationToken.None));
    }
}
