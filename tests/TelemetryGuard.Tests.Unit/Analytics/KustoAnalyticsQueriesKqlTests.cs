using System.Reflection;
using TelemetryGuard.Analytics.Kusto;

namespace TelemetryGuard.Tests.Unit.Analytics;

/// <summary>
/// Textual invariants over every KQL string KustoAnalyticsQueries owns, exposed
/// as `internal const string` fields precisely so they are assertable without a
/// cluster (step 11c). Seven today, including the two MarketIQ evidence reads.
/// D11 must be provable from the TEXT, not just behaviorally: every query filters
/// tenant_id, and the placement join filters it on BOTH sides.
/// </summary>
public sealed class KustoAnalyticsQueriesKqlTests
{
    private static readonly IReadOnlyDictionary<string, string> KqlConstants = typeof(KustoAnalyticsQueries)
        .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.EndsWith("Kql", StringComparison.Ordinal))
        .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);

    [Fact]
    public void ExactlySevenKqlConstants_Exist_AtThisBranchPoint()
    {
        // Three ANA-01 methods + GetTopPlacementsDailyAsync + GetSiteDailyCountsAsync
        // (P2-01 interlock) — count what's actually there rather than hardcoding an
        // assumption that would silently stop catching a missing query.
        Assert.Equal(7, KqlConstants.Count);
    }

    [Fact]
    public void EveryKqlString_StartsWithDeclareQueryParameters()
    {
        Assert.NotEmpty(KqlConstants);
        foreach (var (name, kql) in KqlConstants)
            Assert.StartsWith("declare query_parameters(", kql.TrimStart(), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryKqlString_FiltersTenantId()
    {
        foreach (var (name, kql) in KqlConstants)
            Assert.Contains("tenant_id == tenantId", kql, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryKqlString_HasNoStringFormatOrInterpolationMarkers()
    {
        foreach (var (name, kql) in KqlConstants)
        {
            Assert.DoesNotContain("string.Format", kql, StringComparison.Ordinal);
            // A C# interpolated string embedded verbatim would contain "{" immediately
            // preceded by no KQL syntax reason to have one outside declare's parens —
            // the real signal is the literal `${` that `$"..."` interpolation leaves
            // behind if a KQL block were ever accidentally authored as interpolated.
            Assert.DoesNotContain("${", kql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void IpVelocityKql_GuardsAllThreeCountDistinctCalls_WithIsNotEmpty()
    {
        var kql = KqlConstants[nameof(KustoAnalyticsQueries.IpVelocityKql)];

        Assert.Contains("count_distinctif(session_id, isnotempty(session_id))", kql, StringComparison.Ordinal);
        Assert.Contains("count_distinctif(user_agent, isnotempty(user_agent))", kql, StringComparison.Ordinal);
        Assert.Contains(
            "count_distinctif(fingerprint_visitor_id, isnotempty(fingerprint_visitor_id))",
            kql, StringComparison.Ordinal);
    }

    [Fact]
    public void TopPlacementsDailyKql_FiltersTenantId_OnBothSidesOfTheJoin()
    {
        var kql = KqlConstants[nameof(KustoAnalyticsQueries.TopPlacementsDailyKql)];

        var occurrences = kql.Split("tenant_id == tenantId").Length - 1;
        Assert.True(occurrences >= 2,
            $"expected tenant_id == tenantId on both the capture (let placements=) side " +
            $"and the verdict side of the join — found {occurrences} occurrence(s)");
    }

    [Fact]
    public void TopFlaggedSourcesKql_And_TopPlacementsDailyKql_BindLimitAsDeclaredParameter()
    {
        // Step-0 Q4 came back "yes": `take`/`partition by (top lim by ...)` both
        // accept a declared long parameter on the emulator, so `lim` is bound —
        // never interpolated as a raw integer (the one narrow exception the task
        // allows is for a Q4-"no" answer, which does not apply here).
        var flagged = KqlConstants[nameof(KustoAnalyticsQueries.TopFlaggedSourcesKql)];
        var placements = KqlConstants[nameof(KustoAnalyticsQueries.TopPlacementsDailyKql)];

        Assert.Contains("lim: long", flagged, StringComparison.Ordinal);
        Assert.Contains("| take lim", flagged, StringComparison.Ordinal);
        Assert.Contains("lim: long", placements, StringComparison.Ordinal);
        Assert.Contains("top lim by scored_events desc", placements, StringComparison.Ordinal);
    }
}
