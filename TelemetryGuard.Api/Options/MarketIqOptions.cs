namespace TelemetryGuard.Api.Options;

public sealed class MarketIqOptions
{
    public const string SectionName = "MarketIq";
    public bool Enabled { get; set; }
    public string StreamKey { get; set; } = "tg:marketiq:dispatch";
    public string ConsumerGroup { get; set; } = "tg-marketiq-workers";
    public int ClaimBatchSize { get; set; } = 50;
    public int MaxParallelism { get; set; } = 10;
    public int MaxAttempts { get; set; } = 12;
    public int PollSeconds { get; set; } = 10;
    public Dictionary<string,string> HealthTokens { get; set; } = new(StringComparer.Ordinal);
}
