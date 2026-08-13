using ClickHouse.Client.ADO;
using TelemetryGuard.Analytics.ClickHouse;
using Testcontainers.ClickHouse;

namespace TelemetryGuard.Tests.Integration;

/// <summary>
/// Starts one ClickHouse 24.8 container for the whole test class and applies the
/// ANA-02 schema once so every test sees migrated tables.
/// </summary>
public sealed class ClickHouseSchemaFixture : IAsyncLifetime
{
    private readonly ClickHouseContainer _container = new ClickHouseBuilder()
        .WithImage("clickhouse/clickhouse-server:24.8")
        .Build();

    public string ConnectionString { get; private set; } = "";
    public IReadOnlyList<string> FirstRunApplied { get; private set; } = Array.Empty<string>();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
        FirstRunApplied = await new SchemaMigrator(ConnectionString).ApplyAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

/// <summary>
/// ANA-02 minimal integration proof: idempotent migration, storage contract of
/// tg_events/tg_labels, per-row TTL, and NaN round-trip. Full suite is ANA-06.
/// </summary>
public sealed class SchemaMigratorTests(ClickHouseSchemaFixture fixture)
    : IClassFixture<ClickHouseSchemaFixture>
{
    [Fact]
    public async Task Apply_IsIdempotent_SingleScriptJournaledOnce()
    {
        Assert.Equal(new[] { "0001_events.sql" }, fixture.FirstRunApplied);

        var secondRun = await new SchemaMigrator(fixture.ConnectionString).ApplyAsync();
        Assert.Empty(secondRun);

        Assert.Equal(1UL, await ScalarAsync<ulong>("SELECT count() FROM tg_schema_migrations"));
        Assert.Equal((byte)1, await ScalarAsync<byte>("EXISTS TABLE tg_events"));
        Assert.Equal((byte)1, await ScalarAsync<byte>("EXISTS TABLE tg_labels"));
    }

    [Fact]
    public async Task TgEvents_ShowCreate_MatchesStorageContract()
    {
        var ddl = await ScalarAsync<string>("SHOW CREATE TABLE tg_events");

        Assert.Contains("ENGINE = MergeTree", ddl, StringComparison.Ordinal);
        Assert.Contains("PARTITION BY toYYYYMM(timestamp)", ddl, StringComparison.Ordinal);
        Assert.Contains("ORDER BY (tenant_id, timestamp)", ddl, StringComparison.Ordinal);
        Assert.Contains("toDateTime(timestamp) + toIntervalDay(retention_days)", ddl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PerRowTtl_ExpiredRowDeleted_UnexpiredRowKept()
    {
        const string tenant = "11111111-1111-1111-1111-111111111111";
        await ExecAsync(
            $"""
            INSERT INTO tg_events
                (tenant_id, site_key, session_id, kind, ip, has_js_beacon,
                 retention_days, timestamp)
            VALUES
                ('{tenant}', 'sk-ttl', 'ttl-expired', 'tracker', toIPv6('::ffff:203.0.113.7'), 0,
                 1, now64(3) - INTERVAL 3 DAY),
                ('{tenant}', 'sk-ttl', 'ttl-kept', 'tracker', toIPv6('::ffff:203.0.113.8'), 0,
                 90, now64(3) - INTERVAL 3 DAY)
            """);

        await ExecAsync("OPTIMIZE TABLE tg_events FINAL");

        Assert.Equal(1UL, await ScalarAsync<ulong>(
            $"SELECT count() FROM tg_events WHERE tenant_id = '{tenant}'"));
        Assert.Equal("ttl-kept", await ScalarAsync<string>(
            $"SELECT session_id FROM tg_events WHERE tenant_id = '{tenant}'"));
    }

    [Fact]
    public async Task Float32Nan_IsStoredAsNan_NotZero()
    {
        const string tenant = "22222222-2222-2222-2222-222222222222";
        await ExecAsync(
            $"""
            INSERT INTO tg_events
                (tenant_id, site_key, session_id, kind, ip, has_js_beacon,
                 storage_age_sec, retention_days, timestamp)
            VALUES
                ('{tenant}', 'sk-nan', 'nan-proof', 'pixel', toIPv6('::ffff:198.51.100.9'), 0,
                 nan, 90, now64(3))
            """);

        Assert.Equal((byte)1, await ScalarAsync<byte>(
            $"SELECT isNaN(storage_age_sec) FROM tg_events WHERE tenant_id = '{tenant}' AND session_id = 'nan-proof'"));
    }

    [Fact]
    public async Task TgLabels_AcceptsFraudRow_AndHasFixed400DayTtl()
    {
        const string tenant = "33333333-3333-3333-3333-333333333333";
        await ExecAsync(
            $"""
            INSERT INTO tg_labels (tenant_id, session_id, label, label_source, created_at)
            VALUES ('{tenant}', 'label-proof', 'fraud', 't1_rule', now64(3))
            """);

        Assert.Equal(1UL, await ScalarAsync<ulong>(
            $"SELECT count() FROM tg_labels WHERE tenant_id = '{tenant}' AND label = 'fraud' AND label_source = 't1_rule'"));

        var ddl = await ScalarAsync<string>("SHOW CREATE TABLE tg_labels");
        Assert.Contains("toDateTime(created_at) + toIntervalDay(400)", ddl, StringComparison.Ordinal);
        Assert.Contains("ORDER BY (tenant_id, session_id, created_at)", ddl, StringComparison.Ordinal);
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = new ClickHouseConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var conn = new ClickHouseConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var value = await cmd.ExecuteScalarAsync()
            ?? throw new InvalidOperationException($"Query returned no scalar: {sql}");
        return (T)Convert.ChangeType(value, typeof(T));
    }
}
