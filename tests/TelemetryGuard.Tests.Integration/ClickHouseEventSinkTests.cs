using System.Diagnostics.Metrics;
using ClickHouse.Client.ADO;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Analytics.ClickHouse;
using TelemetryGuard.Core.Tenancy;

namespace TelemetryGuard.Tests.Integration;

/// <summary>
/// ANA-03 smoke cases against a real ClickHouse (schema applied by the fixture):
/// size/age batching, age-only flush, shutdown drain, NaN round-trip, and the
/// label sink write path. The full cross-provider behavior suite is ANA-06.
/// </summary>
public sealed class ClickHouseEventSinkTests(ClickHouseSchemaFixture fixture)
    : IClassFixture<ClickHouseSchemaFixture>
{
    [Fact]
    public async Task Batching_SizeAndAgeTriggers_LandAllRows()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var sink = CreateEventSink(maxBatch: 50, maxAgeSeconds: 0.3);
        await sink.StartAsync(CancellationToken.None);
        try
        {
            // 120 events = two full 50-batches by size + 20-remainder by age.
            var events = Enumerable.Range(0, 120).Select(i => NewEvent(tenant, $"batch-{i}")).ToArray();
            await sink.WriteBatchAsync(events, CancellationToken.None);

            var count = await PollUntilAsync(
                () => CountEventsAsync(tenant), c => c == 120, TimeSpan.FromSeconds(5));
            Assert.Equal(120UL, count);
        }
        finally
        {
            await sink.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AgeFlush_SmallBatch_IsQueryableWithinSeconds()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var sink = CreateEventSink(maxBatch: 5_000, maxAgeSeconds: 0.3);
        await sink.StartAsync(CancellationToken.None);
        try
        {
            var events = Enumerable.Range(0, 10).Select(i => NewEvent(tenant, $"age-{i}")).ToArray();
            await sink.WriteBatchAsync(events, CancellationToken.None);

            // Far below batch size — only the age trigger can flush these.
            var count = await PollUntilAsync(
                () => CountEventsAsync(tenant), c => c == 10, TimeSpan.FromSeconds(3));
            Assert.Equal(10UL, count);
        }
        finally
        {
            await sink.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ShutdownDrain_FlushesEverythingLeftInQueue()
    {
        var tenant = new TenantId(Guid.NewGuid());
        // Age/size chosen so nothing flushes on its own before StopAsync.
        var sink = CreateEventSink(maxBatch: 5_000, maxAgeSeconds: 30);
        await sink.StartAsync(CancellationToken.None);

        var events = Enumerable.Range(0, 100).Select(i => NewEvent(tenant, $"drain-{i}")).ToArray();
        await sink.WriteBatchAsync(events, CancellationToken.None);
        await sink.StopAsync(CancellationToken.None); // drains synchronously

        Assert.Equal(100UL, await CountEventsAsync(tenant));
    }

    [Fact]
    public async Task NanRoundTrip_AbsentSdkNumerics_StoreAsNan_NotZero()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var sink = CreateEventSink(maxBatch: 50, maxAgeSeconds: 0.3);
        await sink.StartAsync(CancellationToken.None);
        try
        {
            // Pixel-mode event: every SDK numeric left at its absent default (NaN).
            await sink.WriteBatchAsync(new[] { NewEvent(tenant, "nan-proof") }, CancellationToken.None);
            await PollUntilAsync(() => CountEventsAsync(tenant), c => c == 1, TimeSpan.FromSeconds(5));

            var flags = await ScalarAsync<string>(
                $"""
                 SELECT concat(
                     toString(isNaN(storage_age_sec)),
                     toString(isNaN(mouse_path_linearity)),
                     toString(isNaN(mean_inter_event_ms)),
                     toString(isNaN(pages_viewed)))
                 FROM tg_events
                 WHERE tenant_id = '{tenant.Value:D}' AND session_id = 'nan-proof'
                 """);
            Assert.Equal("1111", flags);
        }
        finally
        {
            await sink.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task LabelSink_WriteAndDrain_LandsRowInTgLabels()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var sink = new ClickHouseLabelSink(
            Options.Create(new ClickHouseAnalyticsOptions
            {
                ConnectionString = fixture.ConnectionString,
                LabelMaxBatchSize = 50,
                LabelMaxBatchAgeSeconds = 0.3
            }),
            NullLogger<ClickHouseLabelSink>.Instance);
        await sink.StartAsync(CancellationToken.None);

        await sink.WriteAsync(
            new LabelEvent(tenant, "label-sess", LabelValues.Fraud, LabelSources.SyntheticBot, DateTime.UtcNow),
            CancellationToken.None);
        await sink.StopAsync(CancellationToken.None); // drain guarantees visibility

        Assert.Equal(1UL, await ScalarAsync<ulong>(
            $"""
             SELECT count() FROM tg_labels
             WHERE tenant_id = '{tenant.Value:D}'
               AND session_id = 'label-sess' AND label = 'fraud' AND label_source = 'synthetic_bot'
             """));
    }

    // ---- helpers ----

    private ClickHouseEventSink CreateEventSink(int maxBatch, double maxAgeSeconds) =>
        new(Options.Create(new ClickHouseAnalyticsOptions
            {
                ConnectionString = fixture.ConnectionString,
                EventMaxBatchSize = maxBatch,
                EventMaxBatchAgeSeconds = maxAgeSeconds
            }),
            NullLogger<ClickHouseEventSink>.Instance);

    private static ClickEvent NewEvent(TenantId tenant, string sessionId) => new()
    {
        TenantId = tenant,
        SiteKey = "sk-sink",
        SessionId = sessionId,
        Kind = EventKind.Tracker,
        Ip = "203.0.113.10",
        HasJsBeacon = false,
        RetentionDays = 90,
        TimestampUtc = DateTime.UtcNow
    };

    private Task<ulong> CountEventsAsync(TenantId tenant) =>
        ScalarAsync<ulong>($"SELECT count() FROM tg_events WHERE tenant_id = '{tenant.Value:D}'");

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

    private static async Task<T> PollUntilAsync<T>(Func<Task<T>> get, Func<T, bool> done, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var value = await get();
        while (!done(value) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            value = await get();
        }
        return value;
    }
}

/// <summary>
/// ANA-03 failure policy (D7): with an unreachable ClickHouse every batch is
/// retried FlushMaxRetries times, then dropped and counted; the caller never
/// sees an exception and the flusher keeps running for subsequent batches.
/// Needs no container — the connection string points at a closed port.
/// </summary>
public sealed class ClickHouseEventSinkFailurePolicyTests
{
    [Fact]
    public async Task FlushFailure_RetriesThenDrops_CountersMove_FlusherSurvives()
    {
        const int maxRetries = 3;

        long retries = 0, droppedRows = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name != "TelemetryGuard.Analytics.ClickHouse") return;
            if (instrument.Name is "tg.events.batch_retries" or "tg.events.rows_dropped_flush_failed")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == "tg.events.batch_retries") Interlocked.Add(ref retries, value);
            else Interlocked.Add(ref droppedRows, value);
        });
        listener.Start();

        var sink = new ClickHouseEventSink(
            Options.Create(new ClickHouseAnalyticsOptions
            {
                ConnectionString = "Host=127.0.0.1;Port=59999", // nothing listens here
                EventMaxBatchSize = 10,
                EventMaxBatchAgeSeconds = 0.1,
                FlushMaxRetries = maxRetries,
                FlushRetryBaseDelayMs = 10
            }),
            NullLogger<ClickHouseEventSink>.Instance);
        await sink.StartAsync(CancellationToken.None);
        try
        {
            var tenant = new TenantId(Guid.NewGuid());

            // First full batch: no exception may reach the caller (D7).
            await sink.WriteBatchAsync(
                Enumerable.Range(0, 10).Select(i => NewEvent(tenant, $"fail-a-{i}")).ToArray(),
                CancellationToken.None);
            await PollUntilAsync(() => Interlocked.Read(ref droppedRows) >= 10, TimeSpan.FromSeconds(10));

            Assert.Equal(10, Interlocked.Read(ref droppedRows));
            Assert.Equal(maxRetries, Interlocked.Read(ref retries));

            // Second batch: the flusher must still be alive and apply the same policy.
            await sink.WriteBatchAsync(
                Enumerable.Range(0, 10).Select(i => NewEvent(tenant, $"fail-b-{i}")).ToArray(),
                CancellationToken.None);
            await PollUntilAsync(() => Interlocked.Read(ref droppedRows) >= 20, TimeSpan.FromSeconds(10));

            Assert.Equal(20, Interlocked.Read(ref droppedRows));
            Assert.Equal(2L * maxRetries, Interlocked.Read(ref retries));
        }
        finally
        {
            await sink.StopAsync(CancellationToken.None);
        }
    }

    private static ClickEvent NewEvent(TenantId tenant, string sessionId) => new()
    {
        TenantId = tenant,
        SiteKey = "sk-fail",
        SessionId = sessionId,
        Kind = EventKind.Tracker,
        Ip = "203.0.113.11",
        HasJsBeacon = false,
        RetentionDays = 90,
        TimestampUtc = DateTime.UtcNow
    };

    private static async Task PollUntilAsync(Func<bool> done, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!done() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
    }
}
