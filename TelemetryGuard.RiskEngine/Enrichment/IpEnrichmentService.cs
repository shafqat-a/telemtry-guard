// IP enrichment lookup service (spec D3, §7). Synchronous, in-process, microsecond
// latency. Since D24 this type owns only what is true of every dataset — parsing,
// normalization, and the private/loopback short-circuit — and delegates the actual
// lookup to the configured IIpIntelligenceProvider (iplegence by default, MaxMind +
// IP2Proxy as the alternative). Missing database files degrade to null fields (NaN
// downstream) — the process always starts (D13).
//
// See IpEnrichmentOptions for the "IpEnrichment" configuration section and each
// provider file for its own field mapping.

using System.Net;
using TelemetryGuard.RiskEngine.Enrichment.Providers;

namespace TelemetryGuard.RiskEngine.Enrichment;

public sealed class IpEnrichmentService : IIpEnrichmentService, IDisposable
{
    private readonly IIpIntelligenceProvider _provider;

    public IpEnrichmentService(IIpIntelligenceProvider provider)
    {
        _provider = provider;
    }

    /// <summary>The dataset answering lookups — "Iplegence" or "MaxMind".</summary>
    public string ProviderName => _provider.Name;

    public IpEnrichment Enrich(string ip)
    {
        if (string.IsNullOrEmpty(ip) || !IPAddress.TryParse(ip, out var addr))
            return IpEnrichment.Empty;

        if (addr.IsIPv4MappedToIPv6)
            addr = addr.MapToIPv4();

        if (IsPrivateOrLocal(addr))
            return IpEnrichment.Empty; // avoids noisy lookups in dev

        return _provider.Lookup(addr);
    }

    public void Dispose() => _provider.Dispose();

    /// <summary>Loopback, RFC1918, link-local, unique-local and unspecified addresses —
    /// never worth a database lookup.</summary>
    private static bool IsPrivateOrLocal(IPAddress addr)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!addr.TryWriteBytes(bytes, out var len))
            return true; // unrepresentable — treat as non-routable

        if (len == 4)
        {
            return bytes[0] == 127                              // loopback
                || bytes[0] == 10                               // RFC1918 10.0.0.0/8
                || (bytes[0] == 172 && (bytes[1] & 0xF0) == 16) // RFC1918 172.16.0.0/12
                || (bytes[0] == 192 && bytes[1] == 168)         // RFC1918 192.168.0.0/16
                || (bytes[0] == 169 && bytes[1] == 254)         // link-local 169.254.0.0/16
                || bytes[0] == 0;                               // 0.0.0.0/8
        }

        if (IPAddress.IsLoopback(addr))                         // ::1
            return true;
        if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)      // fe80::/10 link-local
            return true;
        if ((bytes[0] & 0xFE) == 0xFC)                          // fc00::/7 unique local
            return true;

        for (var i = 0; i < 16; i++)                            // :: unspecified
        {
            if (bytes[i] != 0)
                return false;
        }
        return true;
    }
}
