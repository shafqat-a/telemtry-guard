using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Data;
using TelemetryGuard.MigrationRunner;
using Testcontainers.MsSql;

namespace TelemetryGuard.Tests.Integration.Data;

/// <summary>
/// RLS smoke tests (DAT-03): proves migration 0002_rls_policy.sql actually enforces
/// tenant isolation — unstamped sessions see zero rows, the BLOCK predicate rejects
/// cross-tenant writes, the SYSTEM sentinel sees all rows, and the resolution tables
/// (dbo.ApiKeys, dbo.Sites) stay exempt.
///
/// NOTE: this class is the SEED that DAT-08 extends into the full repository proof
/// harness (per-repository-method coverage, deliberate cross-tenant reads returning
/// empty). Keep the predicate/policy names (rls.fn_tenantPredicate,
/// rls.TenantIsolationPolicy) stable — DAT-06/DAT-07 ALTER them by name.
/// </summary>
public sealed class RlsSmokeTests : IClassFixture<RlsSqlServerFixture>
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly RlsSqlServerFixture _fixture;

    public RlsSmokeTests(RlsSqlServerFixture fixture) => _fixture = fixture;

    [Fact]
    public void SecondMigrationRun_IsNoOp()
    {
        // The fixture already ran the migrations once; a second run must succeed
        // and apply nothing (DbUp journal makes it idempotent).
        var exitCode = Migrations.RunSqlServer(_fixture.ConnectionString);
        Assert.Equal(0, exitCode);

        using var conn = new SqlConnection(_fixture.ConnectionString);
        var journaled = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.SchemaVersions WHERE ScriptName LIKE '%0002_rls_policy.sql'");
        Assert.Equal(1, journaled);
    }

    [Fact]
    public async Task UnstampedSession_SeesZeroTenantRows_AndCannotWriteProtectedTables()
    {
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        // FILTER predicate: no session context => every comparison is NULL => zero rows.
        var count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Tenants");
        Assert.Equal(0, count);

        // BLOCK predicate: an unstamped INSERT into a protected table must fail.
        await Assert.ThrowsAsync<SqlException>(() => conn.ExecuteAsync(
            """
            INSERT INTO dbo.Campaigns (TenantId, CampaignId, Platform, LandingUrl)
            VALUES (@TenantId, NEWID(), 'google', N'https://example.com/')
            """,
            new { TenantId = TenantA }));
    }

    [Fact]
    public async Task StampedAsTenantA_SeesOnlyA_AndBlockPredicateRejectsCrossTenantWrite()
    {
        await using var conn = await _fixture.OpenStampedAsync(TenantA);

        // FILTER predicate: exactly tenant A's row is visible.
        var visible = (await conn.QueryAsync<Guid>("SELECT TenantId FROM dbo.Tenants")).ToList();
        var single = Assert.Single(visible);
        Assert.Equal(TenantA, single);

        // BLOCK predicate: writing a row whose TenantId is another tenant throws.
        await Assert.ThrowsAsync<SqlException>(() => conn.ExecuteAsync(
            """
            INSERT INTO dbo.Campaigns (TenantId, CampaignId, Platform, LandingUrl)
            VALUES (@TenantId, NEWID(), 'google', N'https://example.com/')
            """,
            new { TenantId = TenantB }));

        // Same-tenant write succeeds.
        var inserted = await conn.ExecuteAsync(
            """
            INSERT INTO dbo.Campaigns (TenantId, CampaignId, Platform, LandingUrl)
            VALUES (@TenantId, @CampaignId, 'google', N'https://example.com/')
            """,
            new { TenantId = TenantA, CampaignId = Guid.NewGuid() });
        Assert.Equal(1, inserted);
    }

    [Fact]
    public async Task SentinelSession_SeesAllTenants()
    {
        await using var conn = await _fixture.OpenStampedAsync(WellKnownTenants.System);

        var count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Tenants");
        Assert.Equal(2, count); // both seeded tenants
    }

    [Fact]
    public async Task ResolutionTables_ReadableUnstamped_WritableOnlyUnderTheMatchingStamp()
    {
        // 0013: dbo.Sites / dbo.ApiKeys have no FILTER predicate (tenant resolution reads
        // them before a context exists) but DO have a BLOCK predicate — an unstamped or
        // mismatched write is refused. The full matrix lives in Sql/RlsPrincipalTests.
        var siteKey = $"sk_test_{Guid.NewGuid():N}";
        var keyHash = new byte[32];
        Random.Shared.NextBytes(keyHash);

        await using (var unstamped = new SqlConnection(_fixture.ConnectionString))
        {
            await unstamped.OpenAsync();
            await Assert.ThrowsAsync<SqlException>(() => unstamped.ExecuteAsync(
                "INSERT INTO dbo.Sites (TenantId, SiteKey, Domain) VALUES (@TenantId, @SiteKey, N'example.com')",
                new { TenantId = TenantA, SiteKey = siteKey }));
        }

        await using (var asA = await _fixture.OpenStampedAsync(TenantA))
        {
            await asA.ExecuteAsync(
                "INSERT INTO dbo.Sites (TenantId, SiteKey, Domain) VALUES (@TenantId, @SiteKey, N'example.com')",
                new { TenantId = TenantA, SiteKey = siteKey });
            await asA.ExecuteAsync(
                "INSERT INTO dbo.ApiKeys (KeyHash, TenantId, Scopes) VALUES (@KeyHash, @TenantId, N'ingest')",
                new { KeyHash = keyHash, TenantId = TenantA });
        }

        // Unstamped read-back is what SqlTenantResolver relies on.
        await using (var unstamped = new SqlConnection(_fixture.ConnectionString))
        {
            await unstamped.OpenAsync();
            Assert.Equal(TenantA, await unstamped.ExecuteScalarAsync<Guid>(
                "SELECT TenantId FROM dbo.Sites WHERE SiteKey = @SiteKey", new { SiteKey = siteKey }));
            Assert.Equal(TenantA, await unstamped.ExecuteScalarAsync<Guid>(
                "SELECT TenantId FROM dbo.ApiKeys WHERE KeyHash = @KeyHash", new { KeyHash = keyHash }));
        }
    }

    [Fact]
    public async Task TenantConnectionFactory_StampsAmbientTenant()
    {
        var context = new TenantContext();
        context.Resolve(new TenantId(TenantA));
        var factory = new TenantConnectionFactory(context, _fixture.Configuration);

        await using var conn = await factory.OpenAsync(CancellationToken.None);
        var visible = await conn.ExecuteScalarAsync<Guid>("SELECT TenantId FROM dbo.Tenants");
        Assert.Equal(TenantA, visible);
    }

    [Fact]
    public async Task TenantConnectionFactory_Refuses_EmptyAndSentinelTenant()
    {
        // Guid.Empty guard (defense-in-depth: a substituted ITenantContext implementation).
        var emptyFactory = new TenantConnectionFactory(
            new StubTenantContext(Guid.Empty), _fixture.Configuration);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => emptyFactory.OpenAsync(CancellationToken.None));

        // The SYSTEM sentinel may only be stamped by ISystemConnectionFactory.
        var sentinelFactory = new TenantConnectionFactory(
            new StubTenantContext(WellKnownTenants.System), _fixture.Configuration);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sentinelFactory.OpenAsync(CancellationToken.None));

        // FND-04's real TenantContext fails even earlier when unresolved.
        var unresolvedFactory = new TenantConnectionFactory(
            new TenantContext(), _fixture.Configuration);
        await Assert.ThrowsAsync<TenantNotResolvedException>(
            () => unresolvedFactory.OpenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SystemConnectionFactory_SentinelSeesAll_PerTenantScopes_AndGuardsInvalidIds()
    {
        var systemFactory = _fixture.Services.GetRequiredService<ISystemConnectionFactory>();

        await using (var sys = await systemFactory.OpenSystemAsync(CancellationToken.None))
        {
            var count = await sys.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Tenants");
            Assert.Equal(2, count);
        }

        await using (var conn = await systemFactory.OpenForTenantAsync(TenantB, CancellationToken.None))
        {
            var visible = await conn.ExecuteScalarAsync<Guid>("SELECT TenantId FROM dbo.Tenants");
            Assert.Equal(TenantB, visible);
        }

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => systemFactory.OpenForTenantAsync(Guid.Empty, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => systemFactory.OpenForTenantAsync(WellKnownTenants.System, CancellationToken.None));
    }

    private sealed class StubTenantContext : ITenantContext
    {
        private readonly Guid _value;

        public StubTenantContext(Guid value) => _value = value;

        public TenantId TenantId => new(_value);

        public string? SiteKey => null;

        public bool IsResolved => true;
        public IReadOnlyList<string> Scopes => Array.Empty<string>();
    }
}

/// <summary>
/// One SQL Server container per test class: starts mssql, applies all migrations via
/// DAT-01's Migrations.RunSqlServer, and seeds tenants A and B under the SYSTEM
/// sentinel session (the only session allowed to see/write across tenants).
/// </summary>
public sealed class RlsSqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder().Build();
    private ServiceProvider? _services;

    public string ConnectionString { get; private set; } = string.Empty;

    public IConfiguration Configuration { get; private set; } = null!;

    public IServiceProvider Services => _services
        ?? throw new InvalidOperationException("Fixture is not initialized.");

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        ConnectionString = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = "TelemetryGuard",
        }.ConnectionString;

        var exitCode = Migrations.RunSqlServer(ConnectionString);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Migration runner failed with exit code {exitCode}.");
        }

        Configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Main"] = ConnectionString,
            })
            .Build();

        _services = new ServiceCollection()
            .AddSingleton<IConfiguration>(Configuration)
            .AddScoped<ITenantContext, TenantContext>()
            .AddTelemetryGuardData()
            .BuildServiceProvider();

        // Seed two tenants under the sentinel — the only session that may write rows
        // for arbitrary tenants. The sentinel itself is never a data value (DAT-02
        // CK_Tenants_NotSystemSentinel keeps it out of dbo.Tenants).
        await using var conn = await OpenStampedAsync(WellKnownTenants.System);
        await conn.ExecuteAsync(
            """
            INSERT INTO dbo.Tenants (TenantId, Name) VALUES
                ('11111111-1111-1111-1111-111111111111', N'A'),
                ('22222222-2222-2222-2222-222222222222', N'B')
            """);
    }

    /// <summary>Opens a raw connection and stamps SESSION_CONTEXT(N'TenantId') = <paramref name="tenantId"/>.</summary>
    public async Task<SqlConnection> OpenStampedAsync(Guid tenantId)
    {
        var conn = new SqlConnection(ConnectionString);
        try
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "EXEC sp_set_session_context @key = N'TenantId', @value = @tid",
                new { tid = tenantId });
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        await _container.DisposeAsync();
    }
}
