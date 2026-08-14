#if TG_KUSTO_CONTRACTS
namespace TelemetryGuard.Tests.Contracts.Kusto;

/// <summary>Kusto runner of the shared analytics contract suite (D7).</summary>
[Trait("requires", "docker")]
[Trait("provider", "kusto")]
public sealed class KustoAnalyticsContractTests(KustoProviderFixture fx)
    : AnalyticsContractTests<KustoProviderFixture>(fx);
#endif
