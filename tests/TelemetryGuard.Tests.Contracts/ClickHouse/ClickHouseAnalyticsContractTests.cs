namespace TelemetryGuard.Tests.Contracts.ClickHouse;

/// <summary>ClickHouse runner of the shared analytics contract suite (D7).</summary>
[Trait("requires", "docker")]
public sealed class ClickHouseAnalyticsContractTests(ClickHouseProviderFixture fx)
    : AnalyticsContractTests<ClickHouseProviderFixture>(fx);
