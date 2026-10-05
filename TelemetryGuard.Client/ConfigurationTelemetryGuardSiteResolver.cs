using Microsoft.Extensions.Options;

namespace TelemetryGuard.Client;

/// <summary>Portable fallback for hosts without a management database.</summary>
public sealed class ConfigurationTelemetryGuardSiteResolver(
    IOptions<TelemetryGuardClientOptions> options) : ITelemetryGuardSiteResolver
{
    public Task<TelemetryGuardSiteOptions?> ResolveAsync(string siteKey,
        CancellationToken ct = default)
    {
        var found = options.Value.Sites.TryGetValue(siteKey, out var site) && site.Enabled
            ? site : null;
        return Task.FromResult(found);
    }
}
