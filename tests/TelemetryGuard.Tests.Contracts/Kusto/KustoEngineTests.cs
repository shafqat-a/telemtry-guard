#if TG_KUSTO_CONTRACTS
using Kusto.Data.Common;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Analytics.Kusto;
using TelemetryGuard.Core.Tenancy;

namespace TelemetryGuard.Tests.Contracts.Kusto;

/// <summary>
/// Kusto-specific behaviors with no cross-provider analog: schema idempotency and
/// the D20 retention sweep. Shares the container-backed KustoProviderFixture with
/// KustoAnalyticsContractTests (xunit collection-scopes IClassFixture per test
/// class by default, but both classes list KustoProviderFixture as their fixture
/// type so xunit still creates/tears down independently — matching the ClickHouse
/// SchemaMigratorTests precedent of a dedicated container per test class).
/// </summary>
[Trait("requires", "docker")]
[Trait("provider", "kusto")]
public sealed class KustoEngineTests(KustoProviderFixture fx) : IClassFixture<KustoProviderFixture>
{
    private static readonly TenantId Tenant = new(Guid.Parse("cccccccc-0000-0000-0000-000000000003"));

    [Fact]
    public async Task ApplyAsync_SecondRun_AppliesNoNewScripts_JournalStaysOneRowPerScript()
    {
        var before = await CountDistinctScriptsAsync();
        Assert.True(before >= 1, "expected the fixture's InitializeAsync to have already applied 0001_events.kql");

        var applied = await new KustoSchemaMigrator(fx.Executor).ApplyAsync();

        Assert.Empty(applied);
        var after = await CountDistinctScriptsAsync();
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task SweepAsync_D20_RemovesRowsPastTheirOwnRetentionDays_KeepsOthers()
    {
        var now = DateTime.UtcNow;
        var expiredSession = $"sweep-expired-{Guid.NewGuid():N}";
        var survivingSession = $"sweep-survives-{Guid.NewGuid():N}";

        // 30-day retention, aged 45 days -> past its own expiry -> swept.
        // 90-day retention, aged 45 days -> well inside its window -> survives.
        var expiredEvent = TestEvents.Create(Tenant, "10.9.9.1", expiredSession,
            timestampUtc: now.AddDays(-45)) with { RetentionDays = 30 };
        var survivingEvent = TestEvents.Create(Tenant, "10.9.9.2", survivingSession,
            timestampUtc: now.AddDays(-45)) with { RetentionDays = 90 };

        await fx.Sink.WriteBatchAsync(new[] { expiredEvent, survivingEvent }, CancellationToken.None);

        // Wait for both rows to become visible before sweeping.
        await PollAsync(() => CountSessionRowsAsync(expiredSession), c => c == 1);
        await PollAsync(() => CountSessionRowsAsync(survivingSession), c => c == 1);

        var sweeper = new KustoRetentionSweeper(fx.Executor,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<KustoRetentionSweeper>.Instance);
        var accepted = await sweeper.SweepAsync(CancellationToken.None);
        Assert.True(accepted, "the sweep command must be accepted by the emulator (step-0 Q5)");

        var expiredCount = await PollAsync(() => CountSessionRowsAsync(expiredSession), c => c == 0);
        Assert.Equal(0, expiredCount);

        var survivingCount = await CountSessionRowsAsync(survivingSession);
        Assert.Equal(1, survivingCount);
    }

    private async Task<long> CountDistinctScriptsAsync()
    {
        using var r = await fx.Executor.ExecuteQueryAsync(
            "tg_schema_migrations | distinct script_name | count", new ClientRequestProperties(), CancellationToken.None);
        r.Read();
        return Convert.ToInt64(r["Count"]);
    }

    private async Task<long> CountSessionRowsAsync(string sessionId)
    {
        var props = new ClientRequestProperties();
        props.SetParameter("s", sessionId);
        using var r = await fx.Executor.ExecuteQueryAsync(
            "declare query_parameters(s: string); tg_events | where session_id == s | count",
            props, CancellationToken.None);
        r.Read();
        return Convert.ToInt64(r["Count"]);
    }

    private static async Task<long> PollAsync(Func<Task<long>> read, Func<long, bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            var v = await read();
            if (done(v)) return v;
            if (DateTime.UtcNow > deadline) return v;
            await Task.Delay(250);
        }
    }
}
#endif
