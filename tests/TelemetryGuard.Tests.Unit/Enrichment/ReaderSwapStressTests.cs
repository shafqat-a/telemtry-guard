using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TelemetryGuard.RiskEngine.Enrichment;

namespace TelemetryGuard.Tests.Unit.Enrichment;

/// <summary>SwapReaders publishes a fully-formed ReaderSet atomically: a tight Enrich
/// loop racing 10k swaps must never observe a torn/partial set or throw.</summary>
public class ReaderSwapStressTests
{
    [Fact]
    public async Task Swapping_readers_during_tight_enrich_loop_never_throws()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "tg-geo-swap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        var service = new IpEnrichmentService(
            Options.Create(new IpEnrichmentOptions { DataDir = dataDir }),
            NullLogger<IpEnrichmentService>.Instance);
        try
        {
            var stop = false;
            Exception? failure = null;
            long lookups = 0;

            var enrichLoop = Task.Run(() =>
            {
                try
                {
                    while (!Volatile.Read(ref stop))
                    {
                        var result = service.Enrich("8.8.8.8");
                        if (result is null)
                            throw new InvalidOperationException("Enrich returned null during swap");
                        Interlocked.Increment(ref lookups);
                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });

            for (var i = 0; i < 10_000; i++)
            {
                service.SwapReaders(new ReaderSet
                {
                    CityMtimeUtc = DateTime.UtcNow,
                    AsnMtimeUtc = DateTime.UtcNow,
                    ProxyMtimeUtc = DateTime.UtcNow,
                });
            }

            // Under a loaded thread pool the Task.Run loop may not have been scheduled
            // yet when the swaps finish — keep the race meaningful by waiting (bounded)
            // for at least one lookup before signalling stop.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Interlocked.Read(ref lookups) == 0 && failure is null && DateTime.UtcNow < deadline)
                await Task.Yield();

            Volatile.Write(ref stop, true);
            await enrichLoop;

            Assert.Null(failure);
            Assert.True(Interlocked.Read(ref lookups) > 0, "enrich loop never ran");
            Assert.NotNull(service.Enrich("8.8.8.8")); // still serving after 10k swaps
        }
        finally
        {
            service.Dispose();
            Directory.Delete(dataDir, recursive: true);
        }
    }
}
