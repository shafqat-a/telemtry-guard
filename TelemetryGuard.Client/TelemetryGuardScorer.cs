namespace TelemetryGuard.Client;

/// <summary>
/// A compact TG-owned score. MarketIQ receives it as evidence but calculates its own score;
/// this package never turns either score into an enforcement action.
/// </summary>
public sealed class TelemetryGuardClientScorer : ITelemetryGuardClientScorer
{
    public TelemetryGuardScore Score(TelemetryGuardVisitState s)
    {
        var score = 0;
        var hits = new List<string>();

        Add(s.WebDriver, 45, "webdriver");
        Add(s.Headless, 40, "headless");
        Add(s.HoneypotFieldFilled, 70, "honeypot_field_filled");
        Add(s.HoneypotLinkClicked, 70, "honeypot_link_clicked");
        Add(s.HoneyIdentifierSeen, 70, "honey_identifier_seen");
        Add(s.DecoyPage, 70, "decoy_page");
        Add(s.IntegrityFailed, 55, "beacon_integrity_failed");

        var seconds = Math.Max(0, (s.LastSeenUnixMs - s.FirstSeenUnixMs) / 1000d);
        if (seconds >= 30 && s.MouseEvents + s.TouchEvents + s.ScrollEvents + s.Keystrokes == 0)
            Add(true, 20, "no_interaction_30s");

        score = Math.Clamp(score, 0, 100);
        var band = score >= 70 ? "block" : score >= 35 ? "challenge" : "allow";
        return new TelemetryGuardScore(score, band, hits);

        void Add(bool condition, int weight, string rule)
        {
            if (!condition) return;
            score += weight;
            hits.Add(rule);
        }
    }
}
