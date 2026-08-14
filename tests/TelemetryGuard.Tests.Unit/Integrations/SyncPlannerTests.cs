using TelemetryGuard.Integrations.GoogleAds;

namespace TelemetryGuard.Tests.Unit.Integrations;

/// <summary>
/// INT-03: pure-function coverage of SyncPlanner's LRU eviction / cap-exceeded /
/// already-live-idempotency / fan-out logic. No SQL, no Google, no worker — just
/// the planning algorithm the worker delegates to.
/// </summary>
public sealed class SyncPlannerTests
{
    private static LivePushedCriterion Live(string campaign, string resourceName, string sourceType, string value, DateTime pushedUtc)
        => new(campaign, resourceName, sourceType, value, pushedUtc);

    private static readonly DateTime Base = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Cap500_490LiveIps_30NewAdds_Evicts20Oldest_AddsAll30()
    {
        var live = Enumerable.Range(0, 490)
            .Select(i => Live("c1", $"rn-{i}", "ip", $"10.0.0.{i}", Base.AddMinutes(i)))
            .ToList();
        var adds = Enumerable.Range(0, 30)
            .Select(i => new CriterionAdd("c1", "ip", $"20.0.0.{i}"))
            .ToList();

        var plans = SyncPlanner.Plan(adds, live, maxIpPerCampaign: 500);

        var plan = Assert.Single(plans);
        Assert.Equal("c1", plan.GoogleCampaignId);
        Assert.Equal(30, plan.Adds.Count);
        Assert.Empty(plan.CapExceeded);
        Assert.Equal(20, plan.EvictResourceNames.Count);
        // Oldest 20 (indices 0..19, PushedUtc ascending) are the ones evicted.
        var expectedEvicted = Enumerable.Range(0, 20).Select(i => $"rn-{i}").ToHashSet();
        Assert.Equal(expectedEvicted, plan.EvictResourceNames.ToHashSet());
        foreach (var add in adds) Assert.Contains(add, plan.Adds);
    }

    [Fact]
    public void Cap5_0Live_8Adds_Newest5Added_3ReportedAsCapExceeded()
    {
        // Oldest-first input order (mirrors the worker's ORDER BY CreatedUtc): index 0
        // is oldest, index 7 is newest.
        var adds = Enumerable.Range(0, 8)
            .Select(i => new CriterionAdd("c1", "ip", $"30.0.0.{i}"))
            .ToList();

        var plans = SyncPlanner.Plan(adds, live: [], maxIpPerCampaign: 5);

        var plan = Assert.Single(plans);
        Assert.Empty(plan.EvictResourceNames); // nothing live to evict
        Assert.Equal(5, plan.Adds.Count);
        Assert.Equal(3, plan.CapExceeded.Count);

        // Newest 5 (indices 3..7) accepted; oldest 3 (indices 0..2) cap-exceeded.
        var acceptedValues = plan.Adds.Select(a => a.SourceValue).ToHashSet();
        var rejectedValues = plan.CapExceeded.Select(a => a.SourceValue).ToHashSet();
        Assert.Equal(new HashSet<string> { "30.0.0.3", "30.0.0.4", "30.0.0.5", "30.0.0.6", "30.0.0.7" }, acceptedValues);
        Assert.Equal(new HashSet<string> { "30.0.0.0", "30.0.0.1", "30.0.0.2" }, rejectedValues);
    }

    [Fact]
    public void Placements_AreNeverCappedOrEvicted_EvenFarBeyondTheIpCap()
    {
        var live = Enumerable.Range(0, 10)
            .Select(i => Live("c1", $"rn-p-{i}", "placement", $"site{i}.example.com/ad", Base.AddMinutes(i)))
            .ToList();
        var adds = Enumerable.Range(0, 50) // way more than a typical ip cap would allow
            .Select(i => new CriterionAdd("c1", "placement", $"new-site{i}.example.com/ad"))
            .ToList();

        var plans = SyncPlanner.Plan(adds, live, maxIpPerCampaign: 5); // tiny ip cap, irrelevant here

        var plan = Assert.Single(plans);
        Assert.Empty(plan.EvictResourceNames);
        Assert.Empty(plan.CapExceeded);
        Assert.Equal(50, plan.Adds.Count);
    }

    [Fact]
    public void EvictionOrder_IsStrictlyPushedUtcAscending_RegardlessOfInputOrder()
    {
        // Deliberately scrambled input order.
        var live = new List<LivePushedCriterion>
        {
            Live("c1", "rn-mid",    "ip", "10.0.0.2", Base.AddMinutes(5)),
            Live("c1", "rn-newest", "ip", "10.0.0.3", Base.AddMinutes(9)),
            Live("c1", "rn-oldest", "ip", "10.0.0.1", Base.AddMinutes(1)),
        };
        var adds = Enumerable.Range(0, 3)
            .Select(i => new CriterionAdd("c1", "ip", $"40.0.0.{i}"))
            .ToList(); // cap forces exactly 2 evictions: 3 live + 3 new = 6, cap 4 -> over=2

        var plans = SyncPlanner.Plan(adds, live, maxIpPerCampaign: 4);

        var plan = Assert.Single(plans);
        Assert.Equal(["rn-oldest", "rn-mid"], plan.EvictResourceNames);
    }

    [Fact]
    public void AddsAlreadyLive_AreAbsentFromThePlan_AndRequireNoGoogleCall()
    {
        var live = new List<LivePushedCriterion>
        {
            Live("c1", "rn-existing", "ip", "50.0.0.1", Base),
        };
        var alreadyLive = new CriterionAdd("c1", "ip", "50.0.0.1");
        var brandNew = new CriterionAdd("c1", "ip", "50.0.0.2");

        var plans = SyncPlanner.Plan([alreadyLive, brandNew], live, maxIpPerCampaign: 500);

        var plan = Assert.Single(plans);
        Assert.DoesNotContain(alreadyLive, plan.Adds);
        Assert.Contains(brandNew, plan.Adds);
        Assert.Single(plan.Adds);
    }

    [Fact]
    public void ApprovedAdds_AllAlreadyLive_ProducesNoPlanForThatCampaign()
    {
        var live = new List<LivePushedCriterion> { Live("c1", "rn-existing", "ip", "60.0.0.1", Base) };
        var adds = new[] { new CriterionAdd("c1", "ip", "60.0.0.1") };

        var plans = SyncPlanner.Plan(adds, live, maxIpPerCampaign: 500);

        Assert.Empty(plans); // nothing to add, evict, or reject -> no plan entry at all
    }

    [Fact]
    public void FanOut_MultipleCampaigns_EachPlannedIndependently()
    {
        // Same IP fanned out to two campaigns; c1 is already at cap, c2 has room.
        var live = Enumerable.Range(0, 500)
            .Select(i => Live("c1", $"rn-{i}", "ip", $"70.0.0.{i}", Base.AddMinutes(i)))
            .ToList();
        var adds = new[]
        {
            new CriterionAdd("c1", "ip", "80.0.0.1"),
            new CriterionAdd("c2", "ip", "80.0.0.1"),
        };

        var plans = SyncPlanner.Plan(adds, live, maxIpPerCampaign: 500);

        Assert.Equal(2, plans.Count);
        var c1 = plans.Single(p => p.GoogleCampaignId == "c1");
        var c2 = plans.Single(p => p.GoogleCampaignId == "c2");

        // c1: at cap already (500 live), adding 1 more evicts exactly 1 oldest.
        Assert.Single(c1.Adds);
        Assert.Single(c1.EvictResourceNames);
        Assert.Equal("rn-0", c1.EvictResourceNames[0]);

        // c2: no live criteria at all, plenty of room, no eviction needed.
        Assert.Single(c2.Adds);
        Assert.Empty(c2.EvictResourceNames);
        Assert.Empty(c2.CapExceeded);
    }

    [Fact]
    public void EmptyInputs_ReturnEmptyPlan()
    {
        Assert.Empty(SyncPlanner.Plan([], [], 500));
    }

    [Fact]
    public void DuplicateAddsForTheSameCampaignValue_AreDedupedToOneOperation()
    {
        var adds = new[]
        {
            new CriterionAdd("c1", "ip", "90.0.0.1"),
            new CriterionAdd("c1", "ip", "90.0.0.1"), // duplicate fan-out add
        };

        var plans = SyncPlanner.Plan(adds, live: [], maxIpPerCampaign: 500);

        var plan = Assert.Single(plans);
        Assert.Single(plan.Adds);
    }
}
