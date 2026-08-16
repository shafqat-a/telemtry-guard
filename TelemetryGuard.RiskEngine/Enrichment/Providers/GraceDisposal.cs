namespace TelemetryGuard.RiskEngine.Enrichment.Providers;

/// <summary>Disposes a swapped-out reader after a grace period so in-flight lookups on
/// the old memory-mapped handles can finish. Disposing immediately after the swap would
/// race a lookup that read the old reference microseconds earlier.</summary>
internal static class GraceDisposal
{
    internal static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    public static void DisposeLater(IDisposable? old)
    {
        if (old is null)
            return;
        _ = DisposeAfterGraceAsync(old);
    }

    private static async Task DisposeAfterGraceAsync(IDisposable old)
    {
        try
        {
            await Task.Delay(Grace).ConfigureAwait(false);
            old.Dispose();
        }
        catch
        {
            // cleanup must never propagate
        }
    }
}
