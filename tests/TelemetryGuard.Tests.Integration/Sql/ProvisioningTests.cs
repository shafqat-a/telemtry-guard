using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using TelemetryGuard.Data;
using TelemetryGuard.Data.Tenancy;
using TelemetryGuard.MigrationRunner.Provisioning;

namespace TelemetryGuard.Tests.Integration.Sql;

/// <summary>
/// DAT-09 provisioning verbs against the DAT-08 harness: every write goes through
/// DAT-03's ISystemConnectionFactory.OpenForTenantAsync with an explicit TenantId,
/// so each passing INSERT is also a proof the RLS BLOCK predicate accepts
/// row-value == session-value. The headline test proves a provisioned tenant is
/// visible under its own stamped context, invisible under another tenant's, and
/// visible under the SYSTEM sentinel.
/// </summary>
[Collection("sqlserver")]
[Trait("Category", "Integration")]
public sealed partial class ProvisioningTests(SqlServerFixture fx)
{
    [GeneratedRegex("^tg_ak_[0-9A-Za-z]{43}$")]
    private static partial Regex ApiKeyShape();

    [GeneratedRegex("^tg_sk_[0-9A-Za-z]{22}$")]
    private static partial Regex SiteKeyShape();

    private Task<Guid> ProvisionFreshTenantAsync(Guid? tenantId = null) =>
        ProvisionCommand.CreateTenantAsync(
            fx.ConnectionString, tenantId, $"Tenant-{Guid.NewGuid():N}", 90, 0);

    [Fact]
    public async Task Provisioned_tenant_visible_under_own_context_invisible_under_others()
    {
        var tenantC = await ProvisionFreshTenantAsync();
        var campaignId = await ProvisionCommand.CreateCampaignAsync(
            fx.ConnectionString, tenantC, "google", "ext-c-001",
            "https://c.example.com/landing", "[\"US\"]");

        // Own stamped context: both rows visible.
        await using (var asC = await fx.OpenAsync(tenantC))
        {
            Assert.Equal(1, await asC.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.Tenants WHERE TenantId = @tid", new { tid = tenantC }));
            Assert.Equal(1, await asC.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.Campaigns WHERE TenantId = @tid AND CampaignId = @cid",
                new { tid = tenantC, cid = campaignId }));
        }

        // Another tenant's context: empty, not an error (RLS FILTER semantics).
        await using (var asA = await fx.OpenAsync(SqlServerFixture.TenantA))
        {
            Assert.Equal(0, await asA.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.Tenants WHERE TenantId = @tid", new { tid = tenantC }));
            Assert.Equal(0, await asA.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.Campaigns WHERE TenantId = @tid AND CampaignId = @cid",
                new { tid = tenantC, cid = campaignId }));
        }

        // SYSTEM sentinel sees all tenants.
        await using var sys = await fx.OpenAsync(WellKnownTenants.System);
        Assert.Equal(1, await sys.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Tenants WHERE TenantId = @tid", new { tid = tenantC }));
    }

    [Fact]
    public async Task Issued_api_key_hash_matches_printed_raw_key()
    {
        var tenant = await ProvisionFreshTenantAsync();
        var rawKey = await ProvisionCommand.IssueApiKeyAsync(fx.ConnectionString, tenant, "admin ingest report");

        Assert.Matches(ApiKeyShape(), rawKey);

        // dbo.ApiKeys is RLS-exempt — readable on an unstamped connection.
        await using var conn = await fx.OpenAsync(null);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.ApiKeys WHERE KeyHash = @hash AND TenantId = @tid",
            new { hash = KeyGenerator.Sha256(rawKey), tid = tenant }));
    }

    [Fact]
    public async Task Issued_api_key_resolves_via_resolver()
    {
        var tenant = await ProvisionFreshTenantAsync();
        var rawKey = await ProvisionCommand.IssueApiKeyAsync(fx.ConnectionString, tenant, "admin ingest report");

        await using var provider = RepositoryFactory.BuildServices(fx, tenant);
        var resolver = provider.GetRequiredService<ITenantResolver>();

        var resolved = await resolver.ResolveApiKeyAsync(rawKey, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(tenant, resolved.TenantId);
        Assert.Equal(new[] { "admin", "ingest", "report" }, resolved.Scopes);
        Assert.Null(resolved.SiteKey);
    }

    [Fact]
    public async Task Registered_site_key_resolvable_unstamped()
    {
        var tenant = await ProvisionFreshTenantAsync();
        var siteKey = await ProvisionCommand.RegisterSiteAsync(fx.ConnectionString, tenant, "example.com", "pixel");

        Assert.Matches(SiteKeyShape(), siteKey);

        // dbo.Sites is RLS-exempt — the resolution path reads it unstamped.
        await using var conn = await fx.OpenAsync(null);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Sites WHERE SiteKey = @siteKey AND TenantId = @tid AND IntegrationMode = 'pixel'",
            new { siteKey, tid = tenant }));
    }

    [Fact]
    public void Generated_keys_are_unique_and_well_formed()
    {
        var apiKey1 = KeyGenerator.NewApiKey();
        var apiKey2 = KeyGenerator.NewApiKey();
        var siteKey1 = KeyGenerator.NewSiteKey();
        var siteKey2 = KeyGenerator.NewSiteKey();

        Assert.NotEqual(apiKey1, apiKey2);
        Assert.NotEqual(siteKey1, siteKey2);
        Assert.Equal(49, apiKey1.Length);
        Assert.Equal(28, siteKey1.Length);
        Assert.Matches(ApiKeyShape(), apiKey1);
        Assert.Matches(ApiKeyShape(), apiKey2);
        Assert.Matches(SiteKeyShape(), siteKey1);
        Assert.Matches(SiteKeyShape(), siteKey2);
    }

    [Fact]
    public async Task Sentinel_empty_and_out_of_range_inputs_rejected_before_sql()
    {
        var rejectedName = $"rejected-{Guid.NewGuid():N}";

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ProvisionCommand.CreateTenantAsync(
            fx.ConnectionString, WellKnownTenants.System, rejectedName, 90, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ProvisionCommand.CreateTenantAsync(
            fx.ConnectionString, Guid.Empty, rejectedName, 90, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ProvisionCommand.CreateTenantAsync(
            fx.ConnectionString, null, rejectedName, 29, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ProvisionCommand.CreateTenantAsync(
            fx.ConnectionString, null, rejectedName, 181, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ProvisionCommand.CreateTenantAsync(
            fx.ConnectionString, null, rejectedName, 90, 2));

        var rejectedCampaignId = Guid.NewGuid();
        await Assert.ThrowsAsync<ArgumentException>(() => ProvisionCommand.CreateCampaignAsync(
            fx.ConnectionString, SqlServerFixture.TenantA, "google", null, "/relative", null,
            rejectedCampaignId));

        // Nothing was written by any rejected call.
        await using var sys = await fx.OpenAsync(WellKnownTenants.System);
        Assert.Equal(0, await sys.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Tenants WHERE Name = @name", new { name = rejectedName }));
        Assert.Equal(0, await sys.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Campaigns WHERE CampaignId = @cid", new { cid = rejectedCampaignId }));
    }

    [Fact]
    public async Task Explicit_id_reprovision_is_idempotent()
    {
        var explicitId = Guid.NewGuid();
        var first = await ProvisionFreshTenantAsync(explicitId);
        var second = await ProvisionFreshTenantAsync(explicitId); // takes the skip path

        Assert.Equal(explicitId, first);
        Assert.Equal(explicitId, second);

        var explicitKey = KeyGenerator.NewApiKey();
        var issued1 = await ProvisionCommand.IssueApiKeyAsync(
            fx.ConnectionString, explicitId, "admin", explicitKey);
        var issued2 = await ProvisionCommand.IssueApiKeyAsync(
            fx.ConnectionString, explicitId, "admin", explicitKey); // takes the skip path
        Assert.Equal(explicitKey, issued1);
        Assert.Equal(explicitKey, issued2);

        await using var sys = await fx.OpenAsync(WellKnownTenants.System);
        Assert.Equal(1, await sys.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Tenants WHERE TenantId = @tid", new { tid = explicitId }));
        Assert.Equal(1, await sys.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.ApiKeys WHERE KeyHash = @hash",
            new { hash = KeyGenerator.Sha256(explicitKey) }));
    }
}
