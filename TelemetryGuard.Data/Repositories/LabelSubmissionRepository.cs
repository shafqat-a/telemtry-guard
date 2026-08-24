using Dapper;
using TelemetryGuard.Core.Tenancy;

namespace TelemetryGuard.Data.Repositories;

public interface ILabelSubmissionRepository
{
    Task<LabelSubmission> UpsertAsync(string sessionId, string label, string source, float weight, CancellationToken ct);
    Task MarkDeliveredAsync(string sessionId, string source, long version, CancellationToken ct);
}

public sealed record LabelSubmission(string SessionId, string Label, string Source, float Weight, long Version, DateTime UpdatedUtc);

internal sealed class LabelSubmissionRepository(ITenantConnectionFactory connections, ITenantContext tenant)
    : ILabelSubmissionRepository
{
    public async Task<LabelSubmission> UpsertAsync(string sessionId, string label, string source, float weight, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        return await conn.QuerySingleAsync<LabelSubmission>(new CommandDefinition(
            """
            MERGE dbo.LabelSubmissions WITH (HOLDLOCK) AS target
            USING (SELECT @TenantId TenantId, @SessionId SessionId, @Source LabelSource) AS src
            ON target.TenantId=src.TenantId AND target.SessionId=src.SessionId AND target.LabelSource=src.LabelSource
            WHEN MATCHED THEN UPDATE SET LabelValue=@Label, Weight=@Weight, Version=target.Version+1, UpdatedUtc=SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (TenantId,SessionId,LabelSource,LabelValue,Weight,Version,UpdatedUtc)
              VALUES (@TenantId,@SessionId,@Source,@Label,@Weight,1,SYSUTCDATETIME())
            OUTPUT inserted.SessionId, inserted.LabelValue Label, inserted.LabelSource Source,
                   inserted.Weight, inserted.Version, inserted.UpdatedUtc;
            """,
            new { TenantId = tenant.TenantId.Value, SessionId = sessionId, Label = label, Source = source, Weight = weight },
            cancellationToken: ct));
    }

    public async Task MarkDeliveredAsync(string sessionId, string source, long version, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.LabelSubmissions SET DeliveredVersion=@Version WHERE TenantId=@TenantId AND SessionId=@SessionId AND LabelSource=@Source AND Version=@Version;",
            new { TenantId = tenant.TenantId.Value, SessionId = sessionId, Source = source, Version = version }, cancellationToken: ct));
    }
}
