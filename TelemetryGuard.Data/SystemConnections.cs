using Microsoft.Extensions.Configuration;

namespace TelemetryGuard.Data;

/// <summary>
/// Composition-root escape hatch (P2-02): an <see cref="ISystemConnectionFactory"/>
/// built straight from configuration, for the ONE caller that needs a SYSTEM-stamped
/// connection BEFORE the service provider exists — TelemetryGuard.Api/Program.cs's
/// model-registry bootstrap. Every other caller injects ISystemConnectionFactory.
/// </summary>
public static class SystemConnections
{
    public static ISystemConnectionFactory FromConfiguration(IConfiguration cfg)
        => new SystemConnectionFactory(cfg);
}
