using System.Net;

namespace TelemetryGuard.RiskEngine.Enrichment.Providers;

/// <summary>
/// The IP-intelligence seam (D24): one implementation per dataset, abstracted by intent
/// — "tell me everything you know about this address" — not by query, exactly as the
/// analytics providers are (D7). Implementations own their own file formats, refresh
/// rules and field mappings; nothing above this interface knows which dataset answered.
///
/// Contract every implementation must honor:
/// <list type="bullet">
/// <item>Lookups are synchronous and in-process (D3): memory-mapped reads, no network
/// hop anywhere in the scoring path.</item>
/// <item>Never throws. A missing/corrupt database, an uncovered address, or a decode
/// failure yields null fields — "missing signal ≠ zero" (D13).</item>
/// <item>The caller has already parsed the address, unmapped IPv4-in-IPv6, and filtered
/// private/loopback ranges; implementations do not repeat that work.</item>
/// <item>Safe for concurrent lookups while <see cref="ReloadIfChanged"/> runs.</item>
/// </list>
/// </summary>
public interface IIpIntelligenceProvider : IDisposable
{
    /// <summary>Dataset name for logs and diagnostics ("Iplegence", "MaxMind").</summary>
    string Name { get; }

    /// <summary>Everything the dataset knows about <paramref name="address"/>.</summary>
    IpEnrichment Lookup(IPAddress address);

    /// <summary>Reloads backing files when they have changed on disk (the ops refresh
    /// scripts replace them in place). Returns true when something was swapped in. On
    /// failure the previous data keeps serving — a refresh never degrades a working
    /// provider.</summary>
    bool ReloadIfChanged();
}
