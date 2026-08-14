using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Kusto.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Analytics.Kusto;
using TelemetryGuard.Core.Tenancy;

// NOTE: this namespace ends in ".Kusto" — always import Kusto.* namespaces with
// `using` and reference their types unqualified (see the same note in
// TelemetryGuard.Analytics.Kusto's own files); a leading "Kusto." prefix here
// resolves relative to this namespace first (CS0234).
namespace TelemetryGuard.Tests.Contracts.Kusto;

/// <summary>
/// Kusto implementation of the provider fixture seam (ANA-06/P2-05): one real
/// Kusto emulator container (kustainer-linux), P2-05's KustoSchemaMigrator, and
/// the real KustoEventSink/KustoLabelSink flushing on a short batch age so the
/// suite's 5 s eventual-visibility window is comfortably met. Deliberately the
/// SAME shape as ClickHouseProviderFixture.
///
/// ALWAYS compiled (unlike the runner class below it in this folder) so it can
/// never rot silently — only the ~40-line xunit runner is gated behind
/// TG_KUSTO_CONTRACTS.
/// </summary>
public sealed class KustoProviderFixture : IAnalyticsProviderFixture
{
    public const string EmulatorImage = "mcr.microsoft.com/azuredataexplorer/kustainer-linux:latest";
    private const int EnginePort = 8080;
    // step 0 / Q7: NetDefaultDB is the emulator's built-in database — `.create
    // database` fails on this image (container MD path constraints), so the
    // fixture targets NetDefaultDB, exactly as the reconnaissance table says to.
    private const string Database = "NetDefaultDB";

    private readonly IContainer _container = new ContainerBuilder()
        .WithImage(EmulatorImage)
        .WithEnvironment("ACCEPT_EULA", "Y")
        .WithPortBinding(EnginePort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().AddCustomWaitStrategy(new KustoEngineReady(EnginePort)))
        // The image is large and the engine takes ~1-2 min to warm on a cold pull.
        .Build();

    private KustoAnalyticsOptions? _opts;
    private KustoQueryExecutor? _executor;
    private KustoEventSink? _sink;
    private KustoLabelSink? _labelSink;

    public async Task InitializeAsync()
    {
        try
        {
            await _container.StartAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to start the Kusto emulator container. Prerequisites: a reachable " +
                "container runtime (podman/docker via DOCKER_HOST), TESTCONTAINERS_RYUK_DISABLED=true " +
                $"under podman, and the '{EmulatorImage}' image pulled or pullable. See " +
                "TelemetryGuard.Analytics.Kusto/README.md.", ex);
        }

        // KustoEngineReady declares the engine ready as soon as `.show version`
        // answers, but the emulator's rowstore subsystem can still throw a
        // transient E_RS_UNAVAILABLE_ERROR on the first few queries/ingests
        // immediately after that — a brief settle delay avoids flaking the
        // suite's very first assertions on that warm-up window.
        await Task.Delay(TimeSpan.FromSeconds(5));

        var port = _container.GetMappedPublicPort(EnginePort);
        _opts = new KustoAnalyticsOptions
        {
            ConnectionString = $"Data Source=http://localhost:{port};Federated Security=False",
            Database = Database,
            IngestMode = KustoAnalyticsOptions.StreamingMode,
            EventMaxBatchSize = 100, EventMaxBatchAgeSeconds = 0.3,
            LabelMaxBatchSize = 10, LabelMaxBatchAgeSeconds = 0.3
        };
        var optionsWrapper = Options.Create(_opts);
        _executor = new KustoQueryExecutor(optionsWrapper, NullLogger<KustoQueryExecutor>.Instance);

        await new KustoSchemaMigrator(_executor).ApplyAsync(enableStreamingIngestion: true);

        var transport = new StreamingKustoIngestTransport(_executor);
        _sink = new KustoEventSink(optionsWrapper, transport, NullLogger<KustoEventSink>.Instance);
        _labelSink = new KustoLabelSink(optionsWrapper, transport, NullLogger<KustoLabelSink>.Instance);
        await _sink.StartAsync(CancellationToken.None);
        await _labelSink.StartAsync(CancellationToken.None);
    }

    public IEventSink Sink => _sink!;
    public ILabelSink LabelSink => _labelSink!;

    /// <summary>Raw engine access for KustoEngineTests (schema idempotency, D20
    /// sweep) — provider-specific tests with no cross-provider analog, so they
    /// need more than the IAnalyticsProviderFixture seam exposes.</summary>
    public IKustoQueryExecutor Executor => _executor!;

    public IAnalyticsQueries CreateQueries(TenantId tenantId, DateTime? utcNow = null) =>
        new KustoAnalyticsQueries(
            new FixedTenantContext(tenantId),
            Options.Create(_opts!),
            new FixedClock(DateTime.SpecifyKind(utcNow ?? DateTime.UtcNow, DateTimeKind.Utc)),
            _executor!);

    public async Task<float> ReadStorageAgeSecAsync(TenantId tenantId, string sessionId)
    {
        var props = new ClientRequestProperties();
        props.SetParameter("t", tenantId.Value);
        props.SetParameter("s", sessionId);
        using var r = await _executor!.ExecuteQueryAsync(
            """
            declare query_parameters(t: guid, s: string);
            tg_events | where tenant_id == t and session_id == s | project storage_age_sec | take 1
            """, props, CancellationToken.None);
        if (!r.Read())
            throw new InvalidOperationException($"No tg_events row stored yet for session '{sessionId}'.");
        return KustoValueMapping.RealToFloat(r["storage_age_sec"]);
    }

    public async Task<long> CountLabelsAsync(TenantId tenantId)
    {
        var props = new ClientRequestProperties();
        props.SetParameter("t", tenantId.Value);
        using var r = await _executor!.ExecuteQueryAsync(
            "declare query_parameters(t: guid); tg_labels | where tenant_id == t | count",
            props, CancellationToken.None);
        r.Read();
        return Convert.ToInt64(r["Count"]);
    }

    public async Task DisposeAsync()
    {
        if (_sink is not null) await _sink.StopAsync(CancellationToken.None);
        if (_labelSink is not null) await _labelSink.StopAsync(CancellationToken.None);
        _executor?.Dispose();
        await _container.DisposeAsync();
    }
}

/// <summary>Polls the emulator's REST mgmt endpoint with `.show version` until it
/// answers 200 — the same probe step 0's manual curl recon used.</summary>
internal sealed class KustoEngineReady(int port) : IWaitUntil
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public async Task<bool> UntilAsync(IContainer container)
    {
        try
        {
            var mappedPort = container.GetMappedPublicPort(port);
            var resp = await Http.PostAsJsonAsync(
                $"http://localhost:{mappedPort}/v1/rest/mgmt",
                new { csl = ".show version" });
            if (!resp.IsSuccessStatusCode) return false;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("Tables", out _);
        }
        catch
        {
            return false;
        }
    }
}
