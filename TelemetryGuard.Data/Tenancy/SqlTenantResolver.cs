using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.Caching.Memory;

namespace TelemetryGuard.Data.Tenancy;

/// <summary>
/// SQL-backed tenant resolver. Queries ONLY the RLS-exempt resolution tables
/// (dbo.ApiKeys, dbo.Sites) on an unstamped <see cref="IResolutionConnectionFactory"/>
/// connection — every other table returns zero rows there, so this class must never
/// join dbo.Tenants or any RLS-protected table. Raw API keys are never stored or
/// logged: only their SHA-256 hash appears in SQL parameters and cache keys.
/// Negative results are cached too (anti-probing: unknown keys cost one lookup
/// per TTL, not one per request).
/// </summary>
internal sealed partial class SqlTenantResolver(
    IResolutionConnectionFactory connections,
    IMemoryCache cache) : ITenantResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    [GeneratedRegex("^[A-Za-z0-9_-]{4,64}$")]
    private static partial Regex SiteKeyShape();

    private sealed class ApiKeyRow { public Guid TenantId { get; set; } public string Scopes { get; set; } = ""; }
    private sealed class SiteRow { public Guid TenantId { get; set; } public string SiteKey { get; set; } = ""; public string IntegrationMode { get; set; } = "js"; }

    public async Task<ResolvedTenant?> ResolveApiKeyAsync(string apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 256) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        var cacheKey = "tg:res:api:" + Convert.ToHexString(hash);
        if (cache.TryGetValue(cacheKey, out ResolvedTenant? cached)) return cached;

        await using var conn = await connections.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ApiKeyRow>(
            "SELECT TenantId, Scopes FROM dbo.ApiKeys WHERE KeyHash = @hash AND Status = 0",
            new { hash });
        var resolved = row is null
            ? null
            : new ResolvedTenant(row.TenantId,
                row.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                null, null);
        cache.Set(cacheKey, resolved, CacheTtl); // negative results cached too (anti-probing)
        return resolved;
    }

    public async Task<ResolvedTenant?> ResolveSiteKeyAsync(string siteKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(siteKey) || !SiteKeyShape().IsMatch(siteKey)) return null;
        var cacheKey = "tg:res:site:" + siteKey;
        if (cache.TryGetValue(cacheKey, out ResolvedTenant? cached)) return cached;

        await using var conn = await connections.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<SiteRow>(
            "SELECT TenantId, SiteKey, IntegrationMode FROM dbo.Sites WHERE SiteKey = @siteKey",
            new { siteKey });
        var resolved = row is null
            ? null
            : new ResolvedTenant(row.TenantId, Array.Empty<string>(), row.SiteKey, row.IntegrationMode);
        cache.Set(cacheKey, resolved, CacheTtl);
        return resolved;
    }
}
