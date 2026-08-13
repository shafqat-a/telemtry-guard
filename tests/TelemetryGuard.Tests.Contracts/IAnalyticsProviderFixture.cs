using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Core.Tenancy;

namespace TelemetryGuard.Tests.Contracts;

/// <summary>
/// One implementation per analytics provider. The Kusto provider (P2-05) MUST
/// supply an implementation backed by the Kusto emulator container and run the
/// same AnalyticsContractTests subclass — that is the D7 guardrail.
/// </summary>
public interface IAnalyticsProviderFixture : IAsyncLifetime  // xunit calls InitializeAsync/DisposeAsync
{
    IEventSink Sink { get; }        // started and flushing
    ILabelSink LabelSink { get; }   // started and flushing
    IAnalyticsQueries CreateQueries(TenantId tenantId, DateTime? utcNow = null);
    /// <summary>Provider-native raw readback used only by the NaN round-trip test.</summary>
    Task<float> ReadStorageAgeSecAsync(TenantId tenantId, string sessionId);
    /// <summary>Provider-native raw count of label rows for a tenant.</summary>
    Task<long> CountLabelsAsync(TenantId tenantId);
}
