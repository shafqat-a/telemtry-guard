using Dapper;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Data.Models;

namespace TelemetryGuard.Data.Repositories;

public interface IConversionRepository
{
    Task<IReadOnlyList<ConversionGoalRecord>> ListGoalsAsync(string? siteKey,CancellationToken ct);
    Task<ConversionGoalRecord?> GetGoalAsync(Guid goalId,CancellationToken ct);
    Task UpsertGoalAsync(ConversionGoalRecord goal,CancellationToken ct);
    Task<bool> DeleteGoalAsync(Guid goalId,CancellationToken ct);
    Task<bool> InsertEventAsync(ConversionEventRecord conversion,CancellationToken ct);
}

internal sealed class ConversionRepository(ITenantConnectionFactory connections,ITenantContext tenant)
    : IConversionRepository
{
    public async Task<IReadOnlyList<ConversionGoalRecord>> ListGoalsAsync(string? siteKey,CancellationToken ct)
    {
        await using var conn=await connections.OpenAsync(ct);
        var rows=await conn.QueryAsync<ConversionGoalRecord>(new CommandDefinition(
            """SELECT * FROM dbo.ConversionGoals WHERE TenantId=@TenantId AND (@SiteKey IS NULL OR SiteKey=@SiteKey) ORDER BY SiteKey,Name,GoalId""",
            new { TenantId=tenant.TenantId.Value,SiteKey=siteKey },cancellationToken:ct));
        return rows.AsList();
    }
    public async Task<ConversionGoalRecord?> GetGoalAsync(Guid goalId,CancellationToken ct)
    {
        await using var conn=await connections.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ConversionGoalRecord>(new CommandDefinition(
            "SELECT * FROM dbo.ConversionGoals WHERE TenantId=@TenantId AND GoalId=@GoalId",
            new { TenantId=tenant.TenantId.Value,GoalId=goalId },cancellationToken:ct));
    }
    public async Task UpsertGoalAsync(ConversionGoalRecord g,CancellationToken ct)
    {
        if(g.TenantId!=tenant.TenantId.Value) throw new InvalidOperationException("Tenant mismatch.");
        await using var conn=await connections.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
MERGE dbo.ConversionGoals WITH (HOLDLOCK) AS t USING (SELECT @TenantId TenantId,@GoalId GoalId) s
ON t.TenantId=s.TenantId AND t.GoalId=s.GoalId
WHEN MATCHED THEN UPDATE SET SiteKey=@SiteKey,Name=@Name,TriggerType=@TriggerType,PagePathsJson=@PagePathsJson,
 Selector=@Selector,MinimumSeconds=@MinimumSeconds,IsPrimary=@IsPrimary,SendMarketIq=@SendMarketIq,
 SendMeta=@SendMeta,SendGoogleAds=@SendGoogleAds,SendGa4=@SendGa4,SendTikTok=@SendTikTok,IsActive=@IsActive,UpdatedUtc=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (TenantId,GoalId,SiteKey,Name,TriggerType,PagePathsJson,Selector,MinimumSeconds,IsPrimary,SendMarketIq,SendMeta,SendGoogleAds,SendGa4,SendTikTok,IsActive)
 VALUES (@TenantId,@GoalId,@SiteKey,@Name,@TriggerType,@PagePathsJson,@Selector,@MinimumSeconds,@IsPrimary,@SendMarketIq,@SendMeta,@SendGoogleAds,@SendGa4,@SendTikTok,@IsActive);
""",g,cancellationToken:ct));
    }
    public async Task<bool> DeleteGoalAsync(Guid goalId,CancellationToken ct)
    {
        await using var conn=await connections.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE dbo.ConversionGoals WHERE TenantId=@TenantId AND GoalId=@GoalId AND NOT EXISTS (SELECT 1 FROM dbo.ConversionEvents e WHERE e.TenantId=@TenantId AND e.GoalId=@GoalId)",
            new { TenantId=tenant.TenantId.Value,GoalId=goalId },cancellationToken:ct))==1;
    }
    public async Task<bool> InsertEventAsync(ConversionEventRecord e,CancellationToken ct)
    {
        if(e.TenantId!=tenant.TenantId.Value) throw new InvalidOperationException("Tenant mismatch.");
        await using var conn=await connections.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
IF EXISTS (SELECT 1 FROM dbo.ConversionEvents WHERE TenantId=@TenantId AND EventId=@EventId) SELECT 0
ELSE BEGIN INSERT dbo.ConversionEvents (TenantId,EventId,GoalId,SiteKey,SessionId,VisitId,PageUrl,OccurredUtc,PageToConversionMs,Verified,EvidenceSource,Value,Currency)
VALUES (@TenantId,@EventId,@GoalId,@SiteKey,@SessionId,@VisitId,@PageUrl,@OccurredUtc,@PageToConversionMs,@Verified,@EvidenceSource,@Value,@Currency); SELECT 1; END
""",e,cancellationToken:ct))==1;
    }
}
