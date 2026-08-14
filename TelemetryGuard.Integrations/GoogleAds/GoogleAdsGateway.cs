using Google.Ads.Gax.Config;
using Google.Ads.GoogleAds; // Services.V25.CampaignCriterionService (ServiceTemplate registry)
using Google.Ads.GoogleAds.Config;
using Google.Ads.GoogleAds.Lib;
using Google.Ads.GoogleAds.V25.Common;
using Google.Ads.GoogleAds.V25.Errors;
using Google.Ads.GoogleAds.V25.Resources;
using Google.Ads.GoogleAds.V25.Services;
using Microsoft.Extensions.Options;

namespace TelemetryGuard.Integrations.GoogleAds;

/// <summary>
/// The real <see cref="IGoogleAdsGateway"/>: talks to the official
/// <c>Google.Ads.GoogleAds</c> .NET client (installed version exposes API surface
/// V25 — pick whatever is highest in the installed package; the shape below is
/// stable across recent versions). Registered as a singleton
/// (<see cref="GoogleAdsServiceCollectionExtensions"/>); every call is scoped to
/// one tenant's Google Ads customer id via <paramref name="customerId"/> in
/// <see cref="MutateAsync"/> — the OAuth/developer-token credentials in
/// <see cref="GoogleAdsOptions"/> are shared across all tenants (D15: one
/// developer-token integration, per-tenant accounts addressed by customer id).
/// </summary>
/// <remarks>
/// MANUAL SMOKE PROCEDURE (never automated — no live Google Ads calls in CI or
/// any test, per INT-03's guardrails): against a Google Ads *test account*
/// (developer-token test access, https://developers.google.com/google-ads/api/docs/get-started/test-accounts),
/// 1) fill in real DeveloperToken/OAuthClientId/OAuthClientSecret/OAuthRefreshToken
///    in a local, gitignored appsettings override; 2) set GoogleAds:DryRun=false;
/// 3) seed one dbo.Tenants row with that test account's 10-digit customer id in
///    GoogleAdsCustomerId, one dbo.Campaigns row with Platform='google' and a real
///    test-account ExternalCampaignId, and one approved dbo.ExclusionQueue row
///    (Platform='google', SourceType='ip'); 4) run the API host and watch the
///    tg.googleads.pushed/failed logs and the test account's campaign-level IP
///    exclusions in the Google Ads UI. Never point this at a production account.
/// </remarks>
public sealed class GoogleAdsGateway(IOptions<GoogleAdsOptions> options) : IGoogleAdsGateway
{
    private GoogleAdsClient CreateClient()
    {
        var o = options.Value;
        return new GoogleAdsClient(new GoogleAdsConfig
        {
            DeveloperToken = o.DeveloperToken,
            OAuth2Mode = OAuth2Flow.APPLICATION,
            OAuth2ClientId = o.OAuthClientId,
            OAuth2ClientSecret = o.OAuthClientSecret,
            OAuth2RefreshToken = o.OAuthRefreshToken,
            LoginCustomerId = string.IsNullOrEmpty(o.LoginCustomerId) ? null : o.LoginCustomerId,
        });
    }

    public async Task<MutateOutcome> MutateAsync(
        string customerId, IReadOnlyList<CriterionAdd> adds,
        IReadOnlyList<string> removes, CancellationToken ct)
    {
        try
        {
            var service = CreateClient().GetService(Services.V25.CampaignCriterionService);
            var operations = new List<CampaignCriterionOperation>(removes.Count + adds.Count);
            foreach (var rn in removes)
                operations.Add(new CampaignCriterionOperation { Remove = rn });
            foreach (var a in adds)
            {
                var criterion = new CampaignCriterion
                {
                    Campaign = CampaignName.Format(customerId, a.GoogleCampaignId),
                    Negative = true,
                };
                // IP exclusions are negative campaign criteria with IpBlock; placement
                // exclusions are negative campaign criteria with Placement — both exist
                // per campaign, never per account (D15 spec context).
                if (a.SourceType == "ip") criterion.IpBlock = new IpBlockInfo { IpAddress = a.SourceValue };
                else criterion.Placement = new PlacementInfo { Url = a.SourceValue };
                operations.Add(new CampaignCriterionOperation { Create = criterion });
            }

            var request = new MutateCampaignCriteriaRequest
            {
                CustomerId = customerId,
                PartialFailure = true,
                Operations = { operations },
            };
            var response = await service.MutateCampaignCriteriaAsync(request, ct);

            // Partial-failure mapping. Results align 1:1 with operations INCLUDING the
            // removes — a successful remove returns the REMOVED criterion's resource name
            // (we never read Results for removes here, only for the adds tail). Operation
            // index layout: 0..removes.Count-1 are removes; removes.Count + i is adds[i].
            var failure = response.PartialFailure; // null when every operation succeeded
            var failedByIdx = new Dictionary<int, string>();
            if (failure is not null)
            {
                foreach (var e in failure.Errors)
                {
                    var element = e.Location?.FieldPathElements
                        .FirstOrDefault(p => p.FieldName == "operations");
                    if (element is null) continue; // defensive: unexpected error shape, skip rather than crash
                    var opIdx = element.Index;
                    failedByIdx[opIdx] = failedByIdx.TryGetValue(opIdx, out var prior)
                        ? $"{prior}; {e.Message}" : e.Message;
                }
            }

            var created = new List<(CriterionAdd Add, string ResourceName)>();
            var failures = new List<(CriterionAdd Add, string Error)>();
            for (var i = 0; i < adds.Count; i++)
            {
                var opIdx = removes.Count + i;
                if (failedByIdx.TryGetValue(opIdx, out var err)) failures.Add((adds[i], err));
                else created.Add((adds[i], response.Results[opIdx].ResourceName));
            }
            return new MutateOutcome(created, failures);
        }
        catch (GoogleAdsException ex)
        {
            // Request-level failure (auth, quota, malformed request) — nothing in the
            // batch was processed. Fail every add so the caller can mark those queue
            // rows 'failed' instead of leaving them stuck 'approved' forever.
            var message = ex.Failure?.ToString() ?? ex.Message;
            return new MutateOutcome(
                Created: Array.Empty<(CriterionAdd, string)>(),
                Failures: adds.Select(a => (a, message)).ToList());
        }
    }
}
