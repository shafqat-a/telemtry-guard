using ClickHouse.Client.ADO;
using ClickHouse.Client.Utility;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Analytics.ClickHouse;
using TelemetryGuard.Core.Tenancy;
using Testcontainers.ClickHouse;

namespace TelemetryGuard.Tests.Contracts.ClickHouse;

/// <summary>
/// ClickHouse implementation of the provider fixture seam: one real ClickHouse
/// container (Testcontainers), ANA-02 schema via SchemaMigrator, and the real
/// ANA-03 sinks flushing on a short batch age so the suite's 5 s eventual-
/// visibility window is comfortably met.
/// </summary>
public sealed class ClickHouseProviderFixture : IAnalyticsProviderFixture
{
    private readonly ClickHouseContainer _container =
        new ClickHouseBuilder().WithImage("clickhouse/clickhouse-server:24.8").Build();
    private ClickHouseEventSink? _sink;
    private ClickHouseLabelSink? _labelSink;
    private ClickHouseAnalyticsOptions? _opts;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var cs = _container.GetConnectionString();
        await new SchemaMigrator(cs).ApplyAsync();
        _opts = new ClickHouseAnalyticsOptions
        {
            ConnectionString = cs,
            EventMaxBatchSize = 100, EventMaxBatchAgeSeconds = 0.3,
            LabelMaxBatchSize = 10,  LabelMaxBatchAgeSeconds = 0.3
        };
        _sink = new ClickHouseEventSink(Options.Create(_opts), NullLogger<ClickHouseEventSink>.Instance);
        _labelSink = new ClickHouseLabelSink(Options.Create(_opts), NullLogger<ClickHouseLabelSink>.Instance);
        await _sink.StartAsync(CancellationToken.None);
        await _labelSink.StartAsync(CancellationToken.None);
    }

    public IEventSink Sink => _sink!;
    public ILabelSink LabelSink => _labelSink!;

    public IAnalyticsQueries CreateQueries(TenantId tenantId, DateTime? utcNow = null) =>
        new ClickHouseAnalyticsQueries(
            new FixedTenantContext(tenantId),
            Options.Create(_opts!),
            new FixedClock(DateTime.SpecifyKind(utcNow ?? DateTime.UtcNow, DateTimeKind.Utc)));

    public async Task<float> ReadStorageAgeSecAsync(TenantId tenantId, string sessionId)
    {
        await using var conn = new ClickHouseConnection(_opts!.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT storage_age_sec FROM tg_events
            WHERE tenant_id = {t:UUID} AND session_id = {s:String}
            LIMIT 1
            """;
        cmd.AddParameter("t", tenantId.Value);
        cmd.AddParameter("s", sessionId);
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull
            ? throw new InvalidOperationException($"No tg_events row stored yet for session '{sessionId}'.")
            : Convert.ToSingle(result);
    }

    public async Task<long> CountLabelsAsync(TenantId tenantId)
    {
        await using var conn = new ClickHouseConnection(_opts!.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count() FROM tg_labels WHERE tenant_id = {t:UUID}";
        cmd.AddParameter("t", tenantId.Value);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task DisposeAsync()
    {
        if (_sink is not null) await _sink.StopAsync(CancellationToken.None);
        if (_labelSink is not null) await _labelSink.StopAsync(CancellationToken.None);
        await _container.DisposeAsync();
    }
}
