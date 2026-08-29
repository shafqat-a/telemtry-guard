using TelemetryGuard.Client;

namespace TelemetryGuard.Tests.Unit.Client;

public sealed class TelemetryGuardClientScorerTests
{
    private readonly TelemetryGuardClientScorer _scorer = new();

    [Fact]
    public void Clean_interactive_session_is_allowed()
    {
        var result = _scorer.Score(new TelemetryGuardSessionState
        {
            FirstSeenUnixMs = 1_000,
            LastSeenUnixMs = 50_000,
            MouseEvents = 12,
            ScrollEvents = 3,
        });

        Assert.Equal(0, result.Score);
        Assert.Equal("allow", result.Band);
        Assert.Empty(result.RuleHits);
    }

    [Fact]
    public void Honeypot_evidence_caps_score_and_never_returns_an_action()
    {
        var result = _scorer.Score(new TelemetryGuardSessionState
        {
            FirstSeenUnixMs = 1_000,
            LastSeenUnixMs = 50_000,
            WebDriver = true,
            Headless = true,
            HoneypotFieldFilled = true,
        });

        Assert.Equal(100, result.Score);
        Assert.Equal("block", result.Band);
        Assert.Contains("honeypot_field_filled", result.RuleHits);
    }
}
