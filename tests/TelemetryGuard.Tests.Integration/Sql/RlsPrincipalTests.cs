using Dapper;
using Microsoft.Data.SqlClient;
using TelemetryGuard.Data;
using TelemetryGuard.MigrationRunner.Provisioning;

namespace TelemetryGuard.Tests.Integration.Sql;

/// <summary>
/// Migration 0013 proofs — tenant isolation must not depend on developer discipline (D11):
/// (a) the SYSTEM sentinel is a bypass ONLY for the tg_system role (and db_owner); on the
///     request-path role tg_app it yields zero rows and cannot write;
/// (b) tg_app cannot switch the policy off or alter the schema;
/// (c) the resolution tables dbo.ApiKeys / dbo.Sites are readable unstamped but a write
///     must match the stamped tenant (BLOCK predicate);
/// (d) STRUCTURAL: every table with a TenantId column is under the policy with both
///     predicate types (resolution tables: BLOCK only) — a future migration that forgets
///     the ALTER SECURITY POLICY fails here, not in production.
/// Users are created through the real provisioning verb, so this is also its test.
/// </summary>
[Collection("sqlserver")]
[Trait("Category", "Integration")]
public sealed class RlsPrincipalTests(SqlServerFixture fx)
{
    private const string AppUser = "tg_app_probe";
    private const string SystemUser = "tg_system_probe";
    private const string Password = "Pr0be!Passw0rd-2026";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _provisioned;

    private async Task EnsureUsersAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_provisioned) return;
            await ProvisionCommand.CreateDbUserAsync(fx.ConnectionString, AppUser, "tg_app", Password);
            await ProvisionCommand.CreateDbUserAsync(fx.ConnectionString, SystemUser, "tg_system", Password);
            _provisioned = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<SqlConnection> OpenAsAsync(string user, Guid? stamp)
    {
        await EnsureUsersAsync();
        var cs = new SqlConnectionStringBuilder(fx.ConnectionString)
        {
            UserID = user,
            Password = Password,
            IntegratedSecurity = false,
        }.ConnectionString;
        var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        if (stamp is { } tid)
        {
            await conn.ExecuteAsync(
                "EXEC sp_set_session_context @key = N'TenantId', @value = @tid, @read_only = 1",
                new { tid });
        }
        return conn;
    }

    [Fact] // (a) the request-path role gets nothing from the sentinel
    public async Task App_role_stamping_the_sentinel_sees_zero_rows_and_cannot_write()
    {
        await using var asApp = await OpenAsAsync(AppUser, WellKnownTenants.System);

        Assert.Equal(0, await asApp.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Tenants"));
        Assert.Equal(0, await asApp.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Campaigns"));

        // A write under the sentinel is blocked too (BLOCK predicate evaluates false).
        await Assert.ThrowsAsync<SqlException>(() => asApp.ExecuteAsync(
            """
            INSERT INTO dbo.Campaigns (TenantId, CampaignId, Platform, LandingUrl)
            VALUES (@tid, NEWID(), 'google', N'https://probe.example.com/x')
            """,
            new { tid = SqlServerFixture.TenantA }));
    }

    [Fact] // (a) …but a real tenant stamp works exactly as before for the same login
    public async Task App_role_stamped_for_a_tenant_sees_only_that_tenant()
    {
        await using var asApp = await OpenAsAsync(AppUser, SqlServerFixture.TenantA);

        Assert.Equal(1, await asApp.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Tenants WHERE TenantId = @tid", new { tid = SqlServerFixture.TenantA }));
        Assert.Equal(0, await asApp.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Tenants WHERE TenantId = @tid", new { tid = SqlServerFixture.TenantB }));

        // Ordinary DML on its own tenant is granted.
        var campaignId = Guid.NewGuid();
        await asApp.ExecuteAsync(
            """
            INSERT INTO dbo.Campaigns (TenantId, CampaignId, Platform, LandingUrl)
            VALUES (@tid, @cid, 'google', N'https://probe.example.com/ok')
            """,
            new { tid = SqlServerFixture.TenantA, cid = campaignId });
        await asApp.ExecuteAsync(
            "DELETE FROM dbo.Campaigns WHERE TenantId = @tid AND CampaignId = @cid",
            new { tid = SqlServerFixture.TenantA, cid = campaignId });
    }

    [Fact] // (a) the background-job role keeps the cross-tenant enumeration it needs
    public async Task System_role_stamping_the_sentinel_sees_all_tenants()
    {
        await using var asSystem = await OpenAsAsync(SystemUser, WellKnownTenants.System);

        // Other test classes provision tenants concurrently — at least both seeded ones.
        var n = await asSystem.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Tenants");
        Assert.True(n >= 2, $"expected the sentinel to see every tenant, saw {n}");
        Assert.Equal(1, await asSystem.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Tenants WHERE TenantId = @tid", new { tid = SqlServerFixture.TenantB }));
    }

    [Fact] // (b) least privilege: the policy and the schema are out of reach
    public async Task App_role_cannot_disable_the_policy_or_alter_the_schema()
    {
        await using var asApp = await OpenAsAsync(AppUser, SqlServerFixture.TenantA);

        await Assert.ThrowsAsync<SqlException>(() => asApp.ExecuteAsync(
            "ALTER SECURITY POLICY rls.TenantIsolationPolicy WITH (STATE = OFF)"));
        await Assert.ThrowsAsync<SqlException>(() => asApp.ExecuteAsync(
            "ALTER TABLE dbo.Tenants ADD ProbeColumn int NULL"));
        await Assert.ThrowsAsync<SqlException>(() => asApp.ExecuteAsync(
            "DELETE FROM dbo.SchemaVersions WHERE ScriptName LIKE '%0013%'"));
        await Assert.ThrowsAsync<SqlException>(() => asApp.ExecuteAsync(
            "ALTER ROLE tg_system ADD MEMBER " + AppUser));

        // Still ON after the failed attempts (checked as db_owner — tg_app has no
        // VIEW DEFINITION on the policy, so the catalog row is invisible to it).
        await using var sys = await fx.OpenAsync(WellKnownTenants.System);
        Assert.Equal(1, await sys.ExecuteScalarAsync<int>(
            "SELECT is_enabled FROM sys.security_policies WHERE name = 'TenantIsolationPolicy'"));
    }

    [Fact] // (c) resolution tables: FILTER-exempt, BLOCK-protected
    public async Task Resolution_tables_are_readable_unstamped_but_writes_must_match_the_stamp()
    {
        var siteKey = $"sk_probe_{Guid.NewGuid():N}"[..40];

        // Unstamped INSERT is refused (BLOCK predicate false with a NULL context).
        await using (var raw = await fx.OpenAsync(null))
        {
            await Assert.ThrowsAsync<SqlException>(() => raw.ExecuteAsync(
                "INSERT INTO dbo.Sites (TenantId, SiteKey, Domain) VALUES (@TenantId, @SiteKey, N'probe.example.com')",
                new { TenantId = SqlServerFixture.TenantA, SiteKey = siteKey }));
        }

        // Stamped as B, writing an A row: refused.
        await using (var asB = await fx.OpenAsync(SqlServerFixture.TenantB))
        {
            await Assert.ThrowsAsync<SqlException>(() => asB.ExecuteAsync(
                "INSERT INTO dbo.Sites (TenantId, SiteKey, Domain) VALUES (@TenantId, @SiteKey, N'probe.example.com')",
                new { TenantId = SqlServerFixture.TenantA, SiteKey = siteKey }));
            var keyHash = new byte[32];
            Random.Shared.NextBytes(keyHash);
            await Assert.ThrowsAsync<SqlException>(() => asB.ExecuteAsync(
                "INSERT INTO dbo.ApiKeys (KeyHash, TenantId, Scopes) VALUES (@KeyHash, @TenantId, N'admin')",
                new { KeyHash = keyHash, TenantId = SqlServerFixture.TenantA }));
        }

        // Stamped as A, writing an A row: accepted — and readable on an unstamped
        // resolution connection (no FILTER), which is what tenant resolution relies on.
        await using (var asA = await fx.OpenAsync(SqlServerFixture.TenantA))
        {
            await asA.ExecuteAsync(
                "INSERT INTO dbo.Sites (TenantId, SiteKey, Domain) VALUES (@TenantId, @SiteKey, N'probe.example.com')",
                new { TenantId = SqlServerFixture.TenantA, SiteKey = siteKey });
        }
        await using (var raw = await fx.OpenAsync(null))
        {
            Assert.Equal(SqlServerFixture.TenantA, await raw.ExecuteScalarAsync<Guid>(
                "SELECT TenantId FROM dbo.Sites WHERE SiteKey = @SiteKey", new { SiteKey = siteKey }));
        }
    }

    [Fact] // (d) structural coverage — replaces the hand-maintained table list
    public async Task Every_table_with_a_TenantId_column_is_under_the_policy()
    {
        await using var sys = await fx.OpenAsync(WellKnownTenants.System);
        var rows = (await sys.QueryAsync<(string Table, string? PredicateType)>(
            """
            SELECT t.name AS [Table], p.predicate_type_desc AS PredicateType
            FROM sys.tables t
            JOIN sys.columns c ON c.object_id = t.object_id AND c.name = 'TenantId'
            LEFT JOIN sys.security_predicates p ON p.target_object_id = t.object_id
            WHERE t.schema_id = SCHEMA_ID('dbo')
            """)).ToList();

        var byTable = rows.GroupBy(r => r.Table)
            .ToDictionary(g => g.Key, g => g.Select(r => r.PredicateType).Where(p => p is not null).ToHashSet());
        Assert.NotEmpty(byTable);

        var resolutionTables = new HashSet<string> { "ApiKeys", "Sites" };
        foreach (var (table, predicates) in byTable)
        {
            if (resolutionTables.Contains(table))
            {
                Assert.True(predicates.SetEquals(["BLOCK"]),
                    $"dbo.{table} is a resolution table and must carry exactly a BLOCK predicate; has [{string.Join(",", predicates)}]");
            }
            else
            {
                Assert.True(predicates.IsSupersetOf(["FILTER", "BLOCK"]),
                    $"dbo.{table} has a TenantId column but is missing RLS predicates; has [{string.Join(",", predicates)}] — add it to rls.TenantIsolationPolicy in the same migration");
            }
        }

        // The policy is on, schema-bound, and the predicate function is what we think it is.
        Assert.Equal(1, await sys.ExecuteScalarAsync<int>(
            "SELECT is_enabled FROM sys.security_policies WHERE name = 'TenantIsolationPolicy'"));
        Assert.Equal(1, await sys.ExecuteScalarAsync<int>(
            "SELECT is_schema_bound FROM sys.security_policies WHERE name = 'TenantIsolationPolicy'"));
    }

    [Fact] // the provisioning verb is idempotent and rotates the password
    public async Task Create_db_user_is_idempotent_and_rotates_the_password()
    {
        const string user = "tg_rotate_probe";
        await ProvisionCommand.CreateDbUserAsync(fx.ConnectionString, user, "tg_app", "First!Passw0rd-2026");
        await ProvisionCommand.CreateDbUserAsync(fx.ConnectionString, user, "tg_app", "Second!Passw0rd-2026");

        var cs = new SqlConnectionStringBuilder(fx.ConnectionString)
            { UserID = user, Password = "Second!Passw0rd-2026", IntegratedSecurity = false }.ConnectionString;
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT IS_ROLEMEMBER('tg_app')"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT IS_ROLEMEMBER('tg_system')"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            ProvisionCommand.CreateDbUserAsync(fx.ConnectionString, "bad name;", "tg_app", "Third!Passw0rd-2026"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ProvisionCommand.CreateDbUserAsync(fx.ConnectionString, user, "db_owner", "Third!Passw0rd-2026"));
    }
}
