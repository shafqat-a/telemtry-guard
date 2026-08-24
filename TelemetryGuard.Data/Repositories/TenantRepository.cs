using Dapper;
using System.Text.Json;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Data.Models;

namespace TelemetryGuard.Data.Repositories;

/// <summary>
/// Dapper repository for the ambient tenant's own dbo.Tenants row. Connections come
/// exclusively from <see cref="ITenantConnectionFactory"/> (RLS-scoped); every statement
/// additionally carries an explicit TenantId = @TenantId predicate for index seeks (D11.3).
/// </summary>
internal sealed class TenantRepository(ITenantConnectionFactory connections, ITenantContext tenant)
    : ITenantRepository
{
    public async Task<TenantRecord?> GetCurrentAsync(CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<TenantRecord>(new CommandDefinition(
            """
            SELECT TenantId, Name, Status, RetentionDays, EnforcementMode, CreatedUtc,
                   AllowMax, ChallengeMax, ObserveOnly, ExternalAuthority, PolicyUpdatedUtc
            FROM dbo.Tenants WHERE TenantId = @TenantId;
            """,
            new { TenantId = tenant.TenantId.Value },
            cancellationToken: ct));
    }

    public async Task<bool> UpdateRetentionDaysAsync(int retentionDays, CancellationToken ct)
    {
        // D20: 30–180 days. The DB CHECK enforces this too; validate here for clean errors
        // and to avoid touching the database at all on invalid input.
        if (retentionDays is < 30 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionDays), retentionDays,
                "RetentionDays must be between 30 and 180 (D20).");
        }

        await using var conn = await connections.OpenAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.Tenants SET RetentionDays = @RetentionDays WHERE TenantId = @TenantId;",
            new { RetentionDays = retentionDays, TenantId = tenant.TenantId.Value },
            cancellationToken: ct));
        return rows == 1;
    }

    public async Task<bool> UpdateEnforcementModeAsync(byte enforcementMode, CancellationToken ct)
    {
        // D21: 0 = AutoEnforce, 1 = ApprovalQueue.
        if (enforcementMode > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(enforcementMode), enforcementMode,
                "EnforcementMode must be 0 (AutoEnforce) or 1 (ApprovalQueue) (D21).");
        }

        await using var conn = await connections.OpenAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.Tenants SET EnforcementMode = @EnforcementMode WHERE TenantId = @TenantId;",
            new { EnforcementMode = enforcementMode, TenantId = tenant.TenantId.Value },
            cancellationToken: ct));
        return rows == 1;
    }

    public async Task<TenantRecord?> UpdatePolicyAsync(
        TenantPolicyUpdate update, byte[]? actorKeyHash, CancellationToken ct)
    {
        if (update.AllowMax is > 100)
            throw new ArgumentOutOfRangeException(nameof(update.AllowMax));
        if (update.ChallengeMax is > 100)
            throw new ArgumentOutOfRangeException(nameof(update.ChallengeMax));
        if (update.EnforcementMode > 1)
            throw new ArgumentOutOfRangeException(nameof(update.EnforcementMode));

        await using var conn = await connections.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var before = await conn.QuerySingleOrDefaultAsync<TenantRecord>(new CommandDefinition(
            """
            SELECT TenantId, Name, Status, RetentionDays, EnforcementMode, CreatedUtc,
                   AllowMax, ChallengeMax, ObserveOnly, ExternalAuthority, PolicyUpdatedUtc
            FROM dbo.Tenants WITH (UPDLOCK, HOLDLOCK) WHERE TenantId = @TenantId;
            """,
            new { TenantId = tenant.TenantId.Value }, tx, cancellationToken: ct));
        if (before is null)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        var updatedUtc = DateTime.UtcNow;
        var after = before with
        {
            AllowMax = update.AllowMax,
            ChallengeMax = update.ChallengeMax,
            ObserveOnly = update.ObserveOnly,
            EnforcementMode = update.EnforcementMode,
            ExternalAuthority = update.ExternalAuthority ?? before.ExternalAuthority,
            PolicyUpdatedUtc = updatedUtc,
        };
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.Tenants
            SET AllowMax = @AllowMax, ChallengeMax = @ChallengeMax, ObserveOnly = @ObserveOnly,
                EnforcementMode = @EnforcementMode, ExternalAuthority = @ExternalAuthority,
                PolicyUpdatedUtc = @PolicyUpdatedUtc
            WHERE TenantId = @TenantId;

            INSERT dbo.TenantPolicyAudit
                (TenantId, AuditId, ActorKeyHash, OldPolicy, NewPolicy, CreatedUtc)
            VALUES
                (@TenantId, @AuditId, @ActorKeyHash, @OldPolicyJson, @NewPolicyJson, @PolicyUpdatedUtc);
            """,
            new
            {
                TenantId = tenant.TenantId.Value,
                update.AllowMax,
                update.ChallengeMax,
                update.ObserveOnly,
                update.EnforcementMode,
                after.ExternalAuthority,   // merged: null in the update = keep the stored value
                PolicyUpdatedUtc = updatedUtc,
                AuditId = Guid.NewGuid(),
                ActorKeyHash = actorKeyHash,
                OldPolicyJson = JsonSerializer.Serialize(ToAudit(before), jsonOptions),
                NewPolicyJson = JsonSerializer.Serialize(ToAudit(after), jsonOptions),
            }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return after;
    }

    private static object ToAudit(TenantRecord value) => new
    {
        value.AllowMax,
        value.ChallengeMax,
        value.ObserveOnly,
        value.EnforcementMode,
        value.ExternalAuthority,
        value.PolicyUpdatedUtc,
    };
}
