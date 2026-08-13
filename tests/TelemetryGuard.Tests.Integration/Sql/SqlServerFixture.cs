using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;
using StackExchange.Redis;
using TelemetryGuard.Data;
using TelemetryGuard.MigrationRunner;
using Testcontainers.MsSql;
using Testcontainers.Redis;

namespace TelemetryGuard.Tests.Integration.Sql;

[CollectionDefinition("sqlserver")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture> { }

/// <summary>
/// DAT-08 harness fixture: ONE SQL Server container and ONE Redis container shared
/// by every [Collection("sqlserver")] test class. Applies ALL migrations
/// programmatically via DAT-01's <see cref="Migrations.RunSqlServer"/> (never
/// hand-created schema — the point is proving the real migrations produce a working,
/// isolated schema) and seeds two tenants, one site, one API key and one campaign
/// under the SYSTEM sentinel session — the only session allowed to write rows for
/// arbitrary tenants. Tests must use unique-per-test natural keys (fresh GUIDs /
/// values) so the suite is order-independent and re-runnable.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("redis:7-alpine").Build();

    public static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid CampaignA1 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    public const string SiteKeyA = "site-a-key-0001";
    public const string ApiKeyA = "test-api-key-tenant-a";        // raw key; only its SHA-256 is stored
    public const string CampaignA1LandingUrl = "https://a.example.com/landing";

    public string ConnectionString { get; private set; } = "";
    public string RedisConnectionString { get; private set; } = "";

    /// <summary>Direct Redis handle for cache-contract assertions (SISMEMBER/TTL).</summary>
    public IConnectionMultiplexer Redis { get; private set; } = null!;

    /// <summary>The DAT-07 Redis cache key contract: t:{tenantId:D}:wl:{sourceType}.</summary>
    public static string WhitelistKey(Guid tenantId, string sourceType)
        => $"t:{tenantId:D}:wl:{sourceType}";

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_sql.StartAsync(), _redis.StartAsync());
        ConnectionString = new SqlConnectionStringBuilder(_sql.GetConnectionString())
            { InitialCatalog = "TelemetryGuard" }.ConnectionString;
        RedisConnectionString = _redis.GetConnectionString();
        Redis = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString);

        if (Migrations.RunSqlServer(ConnectionString) != 0)
            throw new InvalidOperationException("Migrations failed — see console output.");
        await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        if (Redis is not null)
        {
            await Redis.DisposeAsync();
        }

        await _sql.DisposeAsync();
        await _redis.DisposeAsync();
    }

    /// <summary>Open a connection stamped for the given tenant (or unstamped when null).</summary>
    public async Task<SqlConnection> OpenAsync(Guid? tenantId)
    {
        var conn = new SqlConnection(ConnectionString);
        try
        {
            await conn.OpenAsync();
            if (tenantId is { } tid)
                await conn.ExecuteAsync(
                    "EXEC sp_set_session_context @key = N'TenantId', @value = @tid, @read_only = 1",
                    new { tid });
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    private async Task SeedAsync()
    {
        await using var sys = await OpenAsync(WellKnownTenants.System);
        await sys.ExecuteAsync(
            "INSERT INTO dbo.Tenants (TenantId, Name) VALUES (@TenantA, N'Tenant A'), (@TenantB, N'Tenant B')",
            new { TenantA, TenantB });
        await sys.ExecuteAsync(
            "INSERT INTO dbo.Sites (TenantId, SiteKey, Domain) VALUES (@TenantA, @SiteKeyA, N'a.example.com')",
            new { TenantA, SiteKeyA });
        await sys.ExecuteAsync(
            "INSERT INTO dbo.ApiKeys (KeyHash, TenantId, Scopes) VALUES (@hash, @TenantA, N'admin')",
            new { hash = SHA256.HashData(Encoding.UTF8.GetBytes(ApiKeyA)), TenantA });
        await sys.ExecuteAsync(
            """
            INSERT INTO dbo.Campaigns (TenantId, CampaignId, Platform, LandingUrl)
            VALUES (@TenantA, @CampaignA1, 'google', @LandingUrl)
            """,
            new { TenantA, CampaignA1, LandingUrl = CampaignA1LandingUrl });
    }
}
