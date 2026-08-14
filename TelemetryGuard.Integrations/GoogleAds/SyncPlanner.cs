namespace TelemetryGuard.Integrations.GoogleAds;

/// <summary>One currently-pushed criterion, as read from dbo.GoogleAdsPushedExclusions.
/// <see cref="SourceValue"/> is required (not just the resource name) so
/// <see cref="SyncPlanner.Plan"/> can recognize an incoming add that duplicates
/// something already live and drop it — the idempotency check lives here, not in
/// the caller, so "already live" is a property of the plan itself and is
/// unit-testable without a database.</summary>
public sealed record LivePushedCriterion(
    string GoogleCampaignId, string ResourceName, string SourceType, string SourceValue, DateTime PushedUtc);

/// <summary>One campaign's worth of work for a sync cycle.</summary>
public sealed record CampaignPlan(
    string GoogleCampaignId,
    IReadOnlyList<CriterionAdd> Adds,
    IReadOnlyList<string> EvictResourceNames, // LRU evictions, ip only
    IReadOnlyList<CriterionAdd> CapExceeded);  // ip adds rejected because even the cap
                                                // couldn't fit them after eviction (never
                                                // sent to Google — the caller reports these
                                                // as failed queue rows directly)

/// <summary>
/// Pure, side-effect-free planning of one tenant's Google Ads sync cycle. Never
/// calls Google, never touches SQL — the worker (GoogleAdsExclusionSyncService)
/// is the only caller and does all I/O around this.
/// </summary>
public static class SyncPlanner
{
    /// <summary>
    /// LRU EVICTION STRATEGY (spec-mandated because Google caps IP exclusions at
    /// ~<paramref name="maxIpPerCampaign"/> per campaign): per campaign, live =
    /// currently pushed 'ip' criteria (<paramref name="live"/>, ordered by
    /// PushedUtc ascending = oldest first once grouped here). If live + newAdds
    /// exceeds the cap, evict (live + newAdds - cap) OLDEST live ip criteria to
    /// make room — newest fraud evidence always wins; an evicted IP that
    /// reoffends will be re-queued by a future verdict and pushed again.
    /// Placements are NEVER capped or evicted. If newAdds alone exceed the cap
    /// (even with every live criterion evicted), push only the newest
    /// (cap - remaining-live) adds and report the rest in
    /// <see cref="CampaignPlan.CapExceeded"/> ("ip exclusion cap exceeded") —
    /// they are never sent to Google.
    ///
    /// Idempotency: any <paramref name="approvedAdds"/> entry that exactly
    /// matches a <paramref name="live"/> entry's (campaign, source type, value)
    /// is dropped before any of the above runs — it is already live and needs no
    /// Google call. Order matters for "newest": <paramref name="approvedAdds"/>
    /// must arrive oldest-first (the worker selects queue rows
    /// <c>ORDER BY CreatedUtc</c>) so "the newest cap adds" means the tail of the
    /// per-campaign, per-source-type sublist.
    /// </summary>
    public static IReadOnlyList<CampaignPlan> Plan(
        IReadOnlyList<CriterionAdd> approvedAdds,
        IReadOnlyList<LivePushedCriterion> live,
        int maxIpPerCampaign)
    {
        ArgumentNullException.ThrowIfNull(approvedAdds);
        ArgumentNullException.ThrowIfNull(live);

        var liveByCampaign = live
            .GroupBy(l => l.GoogleCampaignId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var plans = new List<CampaignPlan>();

        foreach (var campaignGroup in approvedAdds.GroupBy(a => a.GoogleCampaignId, StringComparer.Ordinal))
        {
            var campaignId = campaignGroup.Key;
            var liveForCampaign = liveByCampaign.TryGetValue(campaignId, out var l)
                ? l : new List<LivePushedCriterion>();

            var liveIpValues = liveForCampaign
                .Where(x => x.SourceType == "ip")
                .Select(x => x.SourceValue)
                .ToHashSet(StringComparer.Ordinal);
            var livePlacementValues = liveForCampaign
                .Where(x => x.SourceType == "placement")
                .Select(x => x.SourceValue)
                .ToHashSet(StringComparer.Ordinal);

            // Dedup identical (sourceType, value) pairs within this campaign (a
            // duplicated fan-out add), then drop anything already live.
            var newAdds = campaignGroup
                .GroupBy(a => (a.SourceType, a.SourceValue)) // ValueTuple<string,string> equality is ordinal
                .Select(g => g.First())
                .Where(a => a.SourceType == "ip"
                    ? !liveIpValues.Contains(a.SourceValue)
                    : !livePlacementValues.Contains(a.SourceValue))
                .ToList();

            // Oldest-first live ip criteria (eviction candidates, in eviction order).
            var liveIpsOldestFirst = liveForCampaign
                .Where(x => x.SourceType == "ip")
                .OrderBy(x => x.PushedUtc)
                .ToList();

            var ipAdds = newAdds.Where(a => a.SourceType == "ip").ToList();
            var placementAdds = newAdds.Where(a => a.SourceType == "placement").ToList();

            var evictions = new List<string>();
            var acceptedIpAdds = ipAdds;
            var capExceeded = new List<CriterionAdd>();

            if (ipAdds.Count > 0)
            {
                var liveCount = liveIpsOldestFirst.Count;
                var total = liveCount + ipAdds.Count;
                if (total > maxIpPerCampaign)
                {
                    var over = total - maxIpPerCampaign;
                    var evictCount = Math.Min(over, liveCount);
                    evictions.AddRange(liveIpsOldestFirst.Take(evictCount).Select(x => x.ResourceName));

                    var remainingCapacity = Math.Max(0, maxIpPerCampaign - (liveCount - evictCount));
                    if (ipAdds.Count > remainingCapacity)
                    {
                        // Newest = tail of the (oldest-first) input list.
                        acceptedIpAdds = ipAdds.Skip(ipAdds.Count - remainingCapacity).ToList();
                        capExceeded = ipAdds.Take(ipAdds.Count - remainingCapacity).ToList();
                    }
                }
            }

            var finalAdds = new List<CriterionAdd>(acceptedIpAdds.Count + placementAdds.Count);
            finalAdds.AddRange(acceptedIpAdds);
            finalAdds.AddRange(placementAdds);

            if (finalAdds.Count > 0 || evictions.Count > 0 || capExceeded.Count > 0)
                plans.Add(new CampaignPlan(campaignId, finalAdds, evictions, capExceeded));
        }

        return plans;
    }
}
