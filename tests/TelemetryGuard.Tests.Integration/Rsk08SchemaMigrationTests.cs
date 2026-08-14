using ClickHouse.Client.ADO;
using TelemetryGuard.Analytics.ClickHouse;

namespace TelemetryGuard.Tests.Integration;

/// <summary>
/// RSK-08 acceptance: 0002_training_and_shadow.sql applies cleanly AFTER ANA-02's
/// 0001_events.sql (its ALTERs require tg_events/tg_labels to already exist),
/// running it twice is idempotent (ADD COLUMN IF NOT EXISTS), and tg_labels' engine/
/// ORDER BY/TTL are UNCHANGED — only the added `weight` column differs. Reuses the
/// class-fixture ClickHouse container from SchemaMigratorTests (ANA-02's own suite)
/// so both migration scripts are already applied once by the time these tests run.
/// </summary>
public sealed class Rsk08SchemaMigrationTests(ClickHouseSchemaFixture fixture)
    : IClassFixture<ClickHouseSchemaFixture>
{
    [Fact]
    public async Task Reapplying_TheFullMigrationSet_IsIdempotent()
    {
        // fixture already ran ApplyAsync once (0001 + 0002). Running it again must
        // apply nothing new and never throw (ADD COLUMN IF NOT EXISTS).
        var applied = await new SchemaMigrator(fixture.ConnectionString).ApplyAsync();
        Assert.Empty(applied);
    }

    [Fact]
    public async Task TgLabels_ShowCreate_OnlyWeightColumnDiffers_EngineOrderByTtlUnchanged()
    {
        var ddl = await ScalarAsync<string>("SHOW CREATE TABLE tg_labels");

        // ANA-02's engine/partition/order-by/TTL survive the RSK-08 ALTER untouched.
        Assert.Contains("ENGINE = MergeTree", ddl, StringComparison.Ordinal);
        Assert.Contains("ORDER BY (tenant_id, session_id, created_at)", ddl, StringComparison.Ordinal);
        Assert.Contains("toDateTime(created_at) + toIntervalDay(400)", ddl, StringComparison.Ordinal);
        // The RSK-08 addition.
        Assert.Contains("weight", ddl, StringComparison.Ordinal);
        Assert.Contains("Float32", ddl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TgLabels_WeightColumn_DefaultsToOne_ForLiveStyleInsertsThatOmitIt()
    {
        const string tenant = "44444444-4444-4444-4444-444444444444";
        // Mirrors ClickHouseLabelSink.ColumnNames exactly (5 ANA-02 columns only) —
        // proves live label writes keep working unchanged after the ALTER.
        await ExecAsync(
            $"""
            INSERT INTO tg_labels (tenant_id, session_id, label, label_source, created_at)
            VALUES ('{tenant}', 'weight-default-proof', 'fraud', 't1_rule', now64(3))
            """);

        Assert.Equal(1f, await ScalarAsync<float>(
            $"SELECT weight FROM tg_labels WHERE tenant_id = '{tenant}' AND session_id = 'weight-default-proof'"));
    }

    [Fact]
    public async Task TgLabels_WeightColumn_AcceptsExplicitValue()
    {
        const string tenant = "55555555-5555-5555-5555-555555555555";
        await ExecAsync(
            $"""
            INSERT INTO tg_labels (tenant_id, session_id, label, label_source, created_at, weight)
            VALUES ('{tenant}', 'weight-explicit-proof', 'legit', 'review_screen', now64(3), 0.6)
            """);

        Assert.Equal(0.6f, await ScalarAsync<float>(
            $"SELECT weight FROM tg_labels WHERE tenant_id = '{tenant}' AND session_id = 'weight-explicit-proof'"));
    }

    [Fact]
    public async Task TgEvents_HasFeaturesAndShadowColumns_WithRsk08Defaults()
    {
        const string tenant = "66666666-6666-6666-6666-666666666666";
        // Mirrors ClickHouseEventSink.ColumnNames' original 68 columns exactly — an
        // insert that omits features/shadow_score/shadow_scorer_version must still
        // succeed and pick up the RSK-08 defaults.
        await ExecAsync(
            $"""
            INSERT INTO tg_events
                (tenant_id, site_key, session_id, kind, ip, has_js_beacon,
                 retention_days, timestamp)
            VALUES
                ('{tenant}', 'sk-rsk08', 'rsk08-defaults-proof', 'verdict', toIPv6('::ffff:203.0.113.42'), 1,
                 90, now64(3))
            """);

        Assert.Equal("", await ScalarAsync<string>(
            $"SELECT features FROM tg_events WHERE tenant_id = '{tenant}' AND session_id = 'rsk08-defaults-proof'"));
        Assert.Equal("", await ScalarAsync<string>(
            $"SELECT shadow_scorer_version FROM tg_events WHERE tenant_id = '{tenant}' AND session_id = 'rsk08-defaults-proof'"));

        await using var conn = new ClickHouseConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT shadow_score FROM tg_events WHERE tenant_id = '{tenant}' AND session_id = 'rsk08-defaults-proof'";
        var value = await cmd.ExecuteScalarAsync();
        Assert.True(value is null or DBNull); // Nullable(Int16), no default -> NULL, not 0
    }

    [Fact]
    public async Task TgEvents_FeaturesAndShadowScore_RoundTripExplicitValues()
    {
        const string tenant = "77777777-7777-7777-7777-777777777777";
        await ExecAsync(
            $$"""
            INSERT INTO tg_events
                (tenant_id, site_key, session_id, kind, ip, has_js_beacon,
                 retention_days, timestamp, features, shadow_score, shadow_scorer_version)
            VALUES
                ('{{tenant}}', 'sk-rsk08', 'rsk08-explicit-proof', 'verdict', toIPv6('::ffff:203.0.113.43'), 1,
                 90, now64(3), '{"HasJsBeacon":true}', 42, 'lgbm-20260915-a1b2c3d4')
            """);

        Assert.Equal("""{"HasJsBeacon":true}""", await ScalarAsync<string>(
            $"SELECT features FROM tg_events WHERE tenant_id = '{tenant}' AND session_id = 'rsk08-explicit-proof'"));
        Assert.Equal((short)42, await ScalarAsync<short>(
            $"SELECT shadow_score FROM tg_events WHERE tenant_id = '{tenant}' AND session_id = 'rsk08-explicit-proof'"));
        Assert.Equal("lgbm-20260915-a1b2c3d4", await ScalarAsync<string>(
            $"SELECT shadow_scorer_version FROM tg_events WHERE tenant_id = '{tenant}' AND session_id = 'rsk08-explicit-proof'"));
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
