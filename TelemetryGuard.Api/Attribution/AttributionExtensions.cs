using TelemetryGuard.Analytics.Abstractions;

namespace TelemetryGuard.Api.Attribution;

public static class AttributionExtensions
{
    /// <summary>Copies an <see cref="AttributionExtractor.Result"/> onto a ClickEvent.
    /// One place, so the three capture paths cannot drift in what they record.</summary>
    public static ClickEvent WithAttribution(this ClickEvent e, AttributionExtractor.Result a) =>
        e with
        {
            Gbraid = a.Gbraid,
            Wbraid = a.Wbraid,
            UtmSource = a.UtmSource,
            UtmMedium = a.UtmMedium,
            UtmCampaign = a.UtmCampaign,
            UtmTerm = a.UtmTerm,
            UtmContent = a.UtmContent,
            UtmId = a.UtmId,
            CookieFbc = a.CookieFbc,
            CookieFbp = a.CookieFbp,
            CookieGclAw = a.CookieGclAw,
            CookieTtp = a.CookieTtp,
            AttributionChannel = a.AttributionChannel,
            DocumentReferrer = a.DocumentReferrer,
            LandingUrl = a.LandingUrl,
            LandingPath = a.LandingPath,
            LandingQueryKeys = a.LandingQueryKeys,
            Headers = a.Headers,
            Cookies = a.Cookies,
        };

    /// <summary>As <see cref="WithAttribution"/>, and also fills the platform click-id
    /// columns from the landing URL. Used by the pixel and beacon paths, where the click
    /// id can only arrive via the landing page's own query string (the tracker extracts
    /// its own and owns click_id_invalid, so it does not use this overload).</summary>
    public static ClickEvent WithAttributionAndClickIds(this ClickEvent e, AttributionExtractor.Result a) =>
        e.WithAttribution(a) with
        {
            Gclid = a.Gclid,
            Fbclid = a.Fbclid,
            Msclkid = a.Msclkid,
            Ttclid = a.Ttclid,
        };
}
