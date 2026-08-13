using Dapper;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Data.Models;

namespace TelemetryGuard.Data.Repositories;

/// <summary>
/// Dapper repository for dbo.Sites. Connections come exclusively from
/// <see cref="ITenantConnectionFactory"/> (RLS-scoped); every statement additionally
/// carries an explicit TenantId = @TenantId predicate for index seeks (D11.3).
/// </summary>
internal sealed class SiteRepository(ITenantConnectionFactory connections, ITenantContext tenant)
    : ISiteRepository
{
    public async Task CreateAsync(SiteRecord site, CancellationToken ct)
    {
        if (site.IntegrationMode is not ("js" or "pixel"))
        {
            throw new ArgumentException(
                "IntegrationMode must be \"js\" or \"pixel\" (D22).", nameof(site));
        }

        // Throw a clear error on ambient-tenant mismatch rather than relying on the
        // RLS BLOCK predicate's opaque SqlException.
        if (site.TenantId != tenant.TenantId.Value)
        {
            throw new InvalidOperationException(
                "SiteRecord.TenantId does not match the ambient tenant; refusing to insert.");
        }

        await using var conn = await connections.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO dbo.Sites (TenantId, SiteKey, Domain, IntegrationMode)
            VALUES (@TenantId, @SiteKey, @Domain, @IntegrationMode);
            """,
            new
            {
                TenantId = tenant.TenantId.Value,
                site.SiteKey,
                site.Domain,
                site.IntegrationMode,
            },
            cancellationToken: ct));
    }

    public async Task<SiteRecord?> GetBySiteKeyAsync(string siteKey, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<SiteRecord>(new CommandDefinition(
            """
            SELECT TenantId, SiteKey, Domain, IntegrationMode, CreatedUtc
            FROM dbo.Sites WHERE TenantId = @TenantId AND SiteKey = @SiteKey;
            """,
            new { TenantId = tenant.TenantId.Value, SiteKey = siteKey },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<SiteRecord>> ListAsync(CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        var rows = await conn.QueryAsync<SiteRecord>(new CommandDefinition(
            """
            SELECT TenantId, SiteKey, Domain, IntegrationMode, CreatedUtc
            FROM dbo.Sites WHERE TenantId = @TenantId ORDER BY CreatedUtc;
            """,
            new { TenantId = tenant.TenantId.Value },
            cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<bool> UpdateAsync(string siteKey, string domain, string integrationMode, CancellationToken ct)
    {
        if (integrationMode is not ("js" or "pixel"))
        {
            throw new ArgumentException(
                "IntegrationMode must be \"js\" or \"pixel\" (D22).", nameof(integrationMode));
        }

        await using var conn = await connections.OpenAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.Sites SET Domain = @Domain, IntegrationMode = @IntegrationMode
            WHERE TenantId = @TenantId AND SiteKey = @SiteKey;
            """,
            new
            {
                Domain = domain,
                IntegrationMode = integrationMode,
                TenantId = tenant.TenantId.Value,
                SiteKey = siteKey,
            },
            cancellationToken: ct));
        return rows == 1;
    }

    public async Task<bool> DeleteAsync(string siteKey, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.Sites WHERE TenantId = @TenantId AND SiteKey = @SiteKey;",
            new { TenantId = tenant.TenantId.Value, SiteKey = siteKey },
            cancellationToken: ct));
        return rows == 1;
    }
}
