using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TelemetryGuard.RiskEngine.Contracts;
using TelemetryGuard.RiskEngine.Enrichment;
using TelemetryGuard.RiskEngine.Enrichment.Providers;

namespace TelemetryGuard.Tests.Unit.Enrichment;

/// <summary>Opt-in smoke test against a REAL Superior-IP.mmdb (~130 MB, never committed):
///
///   IPLEGENCE_MMDB=./data/geo/Superior-IP.mmdb dotnet test tests/TelemetryGuard.Tests.Unit
///
/// The fixture-based tests pin the mapping; this one pins the assumption that a live
/// daily build still decodes with the same record layout, and that a real 130 MB
/// memory-mapped file answers well inside the scoring budget (D3). Skipped everywhere
/// the variable is unset, so CI stays green without the download.</summary>
public class RealIplegenceDatabaseTests
{
    [SkippableFact]
    public void Real_database_decodes_and_answers_in_microseconds()
    {
        var path = Environment.GetEnvironmentVariable("IPLEGENCE_MMDB");
        Skip.If(string.IsNullOrWhiteSpace(path),
            "Set IPLEGENCE_MMDB to a real Superior-IP.mmdb to run this (scripts/update-iplegence.sh fetches one).");
        Skip.IfNot(File.Exists(path), $"IPLEGENCE_MMDB points at a missing file: {path}");

        using var provider = new IplegenceIpIntelligenceProvider(
            Options.Create(new IpEnrichmentOptions
            {
                DataDir = Path.GetDirectoryName(Path.GetFullPath(path!))!,
                IplegenceDb = Path.GetFileName(path!),
            }),
            NullLogger<IplegenceIpIntelligenceProvider>.Instance);
        using var service = new IpEnrichmentService(provider);

        // Google DNS: stable country and ASN in every build of the merged dataset.
        var google = service.Enrich("8.8.8.8");
        Assert.Equal("US", google.CountryCode);
        Assert.Equal(15169, google.AsnNumber);
        Assert.NotNull(google.IsProxyOrVpn);   // a covered row answers the flags, never null

        // Cloudflare's resolver: the merged cloud ranges must classify the edge as CDN.
        var cloudflare = service.Enrich("1.1.1.1");
        Assert.Equal(13335, cloudflare.AsnNumber);
        Assert.Equal(AsnType.Cdn, cloudflare.AsnType);

        // The inferred usage_type has to survive into AsnType — this is the classification
        // the dataset could not express before, and Mobile is what normalizes
        // device_ids_this_ip_hour for carrier-grade NAT. These read live data, so a
        // rebuild could in principle move them; that is exactly what this test is for.
        Assert.Equal(AsnType.Residential, service.Enrich("73.162.0.1").AsnType);   // Comcast
        Assert.Equal(AsnType.Mobile, service.Enrich("208.54.4.1").AsnType);        // T-Mobile US
        Assert.Equal(AsnType.Education, service.Enrich("128.32.1.1").AsnType);     // UC Berkeley

        // Residential/mobile addresses must NOT be reported as datacenters.
        Assert.False(service.Enrich("73.162.0.1").IsDatacenter);
        Assert.False(service.Enrich("208.54.4.1").IsDatacenter);

        // The timing half is gated like every other perf assertion in this suite
        // (RUN_PERF_TESTS=1): measured alongside 700+ parallel tests it reports CPU
        // contention, not lookup cost.
        Skip.IfNot(Environment.GetEnvironmentVariable("RUN_PERF_TESTS") == "1",
            "Set RUN_PERF_TESTS=1 to also assert the per-lookup latency budget.");

        // Warm the page cache, then measure a mixed v4/v6 workload.
        for (var i = 0; i < 1_000; i++)
        {
            _ = service.Enrich("8.8.8.8");
            _ = service.Enrich("2606:4700::1111");
        }

        var sw = Stopwatch.StartNew();
        const int iterations = 20_000;
        for (var i = 0; i < iterations; i++)
        {
            _ = service.Enrich("8.8.8.8");
            _ = service.Enrich("2606:4700::1111");
        }
        sw.Stop();

        var perLookupUs = sw.Elapsed.TotalMicroseconds / (iterations * 2);
        Assert.True(perLookupUs < 50,
            $"real-database enrichment averaged {perLookupUs:F1} µs/lookup (budget 50 µs — the whole "
            + "scoring path gets 50 ms, so enrichment must stay in the microseconds)");
    }
}
