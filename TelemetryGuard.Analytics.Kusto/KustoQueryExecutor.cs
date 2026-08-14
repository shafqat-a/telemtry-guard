using System.Data;
using Kusto.Data;
using Kusto.Data.Common;
using Kusto.Data.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TelemetryGuard.Analytics.Kusto;

/// <summary>
/// Owns the (expensive, connection-pooling) <see cref="ICslQueryProvider"/> /
/// <see cref="ICslAdminProvider"/> pair for one Kusto engine endpoint. Registered
/// as a singleton (step 9); <c>KustoAnalyticsQueries</c> stays scoped and takes
/// this in via DI. The providers are built LAZILY so that DI resolution never
/// performs I/O — the "Provider=Kusto" boot smoke test must pass with no cluster
/// reachable (step 11c: <c>KustoProvider_RegistersSingletonSinks_...</c> asserts
/// no network I/O happens at registration/resolution time).
/// NEVER logs <see cref="KustoAnalyticsOptions.ConnectionString"/> — it may carry
/// an application key.
/// </summary>
public sealed class KustoQueryExecutor : IKustoQueryExecutor, IDisposable
{
    private readonly KustoAnalyticsOptions _opts;
    private readonly ILogger<KustoQueryExecutor> _log;
    private readonly Lazy<ICslQueryProvider> _queryProvider;
    private readonly Lazy<ICslAdminProvider> _adminProvider;
    private readonly Lazy<KustoConnectionStringBuilder> _kcsb;

    public KustoQueryExecutor(IOptions<KustoAnalyticsOptions> opts, ILogger<KustoQueryExecutor> log)
    {
        _opts = opts.Value;
        _log = log;

        // The KustoConnectionStringBuilder constructor itself performs no I/O, but
        // building it lazily too means an options object with an empty/invalid
        // connection string never throws until the first real call — consistent
        // with "DI resolution never performs I/O".
        _kcsb = new Lazy<KustoConnectionStringBuilder>(() => new KustoConnectionStringBuilder(_opts.ConnectionString));
        _queryProvider = new Lazy<ICslQueryProvider>(() => KustoClientFactory.CreateCslQueryProvider(_kcsb.Value));
        _adminProvider = new Lazy<ICslAdminProvider>(() => KustoClientFactory.CreateCslAdminProvider(_kcsb.Value));
    }

    private ClientRequestProperties NewRequestProperties()
    {
        var props = new ClientRequestProperties { ClientRequestId = $"tg;{Guid.NewGuid()}" };
        props.SetOption(ClientRequestProperties.OptionServerTimeout, TimeSpan.FromSeconds(_opts.QueryTimeoutSeconds));
        return props;
    }

    public Task<IDataReader> ExecuteQueryAsync(string kql, ClientRequestProperties properties, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return _queryProvider.Value.ExecuteQueryAsync(_opts.Database, kql, properties, ct);
    }

    public async Task ExecuteControlCommandAsync(string command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var reader = await _adminProvider.Value
            .ExecuteControlCommandAsync(_opts.Database, command, NewRequestProperties())
            .WaitAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            using var reader = await ExecuteQueryAsync("print 1", NewRequestProperties(), ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Kusto ping failed");
            return false;
        }
    }

    public void Dispose()
    {
        if (_queryProvider.IsValueCreated) _queryProvider.Value.Dispose();
        if (_adminProvider.IsValueCreated) _adminProvider.Value.Dispose();
    }
}
