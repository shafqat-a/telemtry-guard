using Dapper;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Data.Models;

namespace TelemetryGuard.Data.Repositories;

/// <summary>
/// Dapper repository for dbo.Campaigns. Connections come exclusively from
/// <see cref="ITenantConnectionFactory"/> (RLS-scoped); every statement additionally
/// carries an explicit TenantId = @TenantId predicate for index seeks (D11.3).
/// Campaigns.LandingUrl is the ONLY source of redirect destinations — API-02 must read
/// it through <see cref="GetRedirectAsync"/> and nowhere else (open-redirect guardrail).
/// </summary>
internal sealed class CampaignRepository(ITenantConnectionFactory connections, ITenantContext tenant)
    : ICampaignRepository
{
    public async Task CreateAsync(CampaignRecord campaign, CancellationToken ct)
    {
        if (campaign.Platform is not ("google" or "meta" or "tiktok" or "other"))
        {
            throw new ArgumentException(
                "Platform must be one of: google, meta, tiktok, other.", nameof(campaign));
        }

        // Throw a clear error on ambient-tenant mismatch rather than relying on the
        // RLS BLOCK predicate's opaque SqlException.
        if (campaign.TenantId != tenant.TenantId.Value)
        {
            throw new InvalidOperationException(
                "CampaignRecord.TenantId does not match the ambient tenant; refusing to insert.");
        }

        // Open-redirect guardrail: LandingUrl must be a non-empty absolute http(s) URI.
        if (string.IsNullOrWhiteSpace(campaign.LandingUrl)
            || !Uri.TryCreate(campaign.LandingUrl, UriKind.Absolute, out var landingUri)
            || (landingUri.Scheme != Uri.UriSchemeHttps && landingUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException(
                "LandingUrl must be an absolute https:// or http:// URI.", nameof(campaign));
        }

        await using var conn = await connections.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO dbo.Campaigns
                (TenantId, CampaignId, Platform, ExternalCampaignId, LandingUrl, GeoTargets, Status)
            VALUES (@TenantId, @CampaignId, @Platform, @ExternalCampaignId, @LandingUrl, @GeoTargets, @Status);
            """,
            new
            {
                TenantId = tenant.TenantId.Value,
                campaign.CampaignId,
                campaign.Platform,
                campaign.ExternalCampaignId,
                campaign.LandingUrl,
                campaign.GeoTargets,
                campaign.Status,
            },
            cancellationToken: ct));
    }

    public async Task<CampaignRecord?> GetAsync(Guid campaignId, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<CampaignRecord>(new CommandDefinition(
            """
            SELECT TenantId, CampaignId, Platform, ExternalCampaignId, LandingUrl, GeoTargets, Status, CreatedUtc
            FROM dbo.Campaigns WHERE TenantId = @TenantId AND CampaignId = @CampaignId;
            """,
            new { TenantId = tenant.TenantId.Value, CampaignId = campaignId },
            cancellationToken: ct));
    }

    /// <summary>
    /// /c HOT PATH (API-02, inside the &lt;50 ms scoring/redirect budget — D3).
    /// Single round-trip, single Clustered Index Seek on PK_Campaigns (TenantId, CampaignId):
    /// the explicit TenantId predicate plus PK column order guarantees the seek, and the
    /// clustered PK IS the covering index for (LandingUrl, Status) — do NOT add another
    /// index for this query, and do not widen the two-column projection.
    /// </summary>
    public async Task<CampaignRedirect?> GetRedirectAsync(Guid campaignId, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<CampaignRedirect>(new CommandDefinition(
            """
            SELECT LandingUrl, Status FROM dbo.Campaigns
            WHERE TenantId = @TenantId AND CampaignId = @CampaignId;
            """,
            new { TenantId = tenant.TenantId.Value, CampaignId = campaignId },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<CampaignRecord>> ListAsync(CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        var rows = await conn.QueryAsync<CampaignRecord>(new CommandDefinition(
            """
            SELECT TenantId, CampaignId, Platform, ExternalCampaignId, LandingUrl, GeoTargets, Status, CreatedUtc
            FROM dbo.Campaigns WHERE TenantId = @TenantId ORDER BY CreatedUtc;
            """,
            new { TenantId = tenant.TenantId.Value },
            cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<bool> UpdateAsync(CampaignRecord campaign, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.Campaigns
            SET Platform = @Platform, ExternalCampaignId = @ExternalCampaignId,
                LandingUrl = @LandingUrl, GeoTargets = @GeoTargets, Status = @Status
            WHERE TenantId = @TenantId AND CampaignId = @CampaignId;
            """,
            new
            {
                campaign.Platform,
                campaign.ExternalCampaignId,
                campaign.LandingUrl,
                campaign.GeoTargets,
                campaign.Status,
                TenantId = tenant.TenantId.Value,
                campaign.CampaignId,
            },
            cancellationToken: ct));
        return rows == 1;
    }
}
