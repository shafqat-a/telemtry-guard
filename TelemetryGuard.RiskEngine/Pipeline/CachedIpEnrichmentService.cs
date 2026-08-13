using Microsoft.Extensions.Caching.Memory;
using TelemetryGuard.RiskEngine.Enrichment;

namespace TelemetryGuard.RiskEngine.Pipeline;

/// <summary>
/// Memory-cache decorator over <see cref="IIpEnrichmentService"/> for the scoring
/// pipeline: key "ipe:{ip}", absolute TTL 5 minutes, size-limited cache
/// (SizeLimit = 100_000 entries, each entry size 1). Enrichment is tenant-independent
/// reference data, so a single process-wide cache is correct — session/whitelist data
/// is NEVER cached across tenants, but this may be. The underlying lookups are already
/// microsecond-scale memory-mapped reads (RSK-02); the cache removes repeated parsing
/// and lookup work for hot IPs inside the &lt; 50 ms budget (D3).
/// </summary>
public sealed class CachedIpEnrichmentService : IIpEnrichmentService, IDisposable
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private readonly IIpEnrichmentService _inner;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 100_000 });

    public CachedIpEnrichmentService(IIpEnrichmentService inner) => _inner = inner;

    public IpEnrichment Enrich(string ip)
    {
        if (string.IsNullOrEmpty(ip))
        {
            return _inner.Enrich(ip); // Empty result — not worth a cache slot
        }

        return _cache.GetOrCreate("ipe:" + ip, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Ttl;
            entry.Size = 1;
            return _inner.Enrich(ip);
        })!;
    }

    public void Dispose() => _cache.Dispose();
}
