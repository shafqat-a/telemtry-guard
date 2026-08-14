namespace TelemetryGuard.Integrations.GoogleAds;

/// <summary>One negative campaign criterion to create: an IP block ("ip") or a
/// negative placement ("placement") on the given Google campaign. Structural
/// (record) equality lets callers dedupe fan-out adds and use instances as
/// dictionary keys.</summary>
public sealed record CriterionAdd(string GoogleCampaignId, string SourceType, string SourceValue);

/// <summary>Result of one batched MutateCampaignCriteria call: which adds Google
/// accepted (with the created criterion's resource name) and which it rejected
/// (with the platform's error message) — partial-failure, not all-or-nothing.</summary>
public sealed record MutateOutcome(
    IReadOnlyList<(CriterionAdd Add, string ResourceName)> Created,
    IReadOnlyList<(CriterionAdd Add, string Error)> Failures);

/// <summary>The only type that talks to Google. One instance per process; every call
/// addresses a specific tenant's Google Ads customer id.</summary>
public interface IGoogleAdsGateway
{
    /// <summary>Batched MutateCampaignCriteria with PartialFailure=true.
    /// <paramref name="removes"/> are criterion resource names to delete (LRU eviction).
    /// One bad criterion never sinks the rest of the batch; a request-level failure
    /// (auth/quota — the call throws) fails every add in <paramref name="adds"/>.</summary>
    Task<MutateOutcome> MutateAsync(
        string customerId, IReadOnlyList<CriterionAdd> adds,
        IReadOnlyList<string> removes, CancellationToken ct);
}
