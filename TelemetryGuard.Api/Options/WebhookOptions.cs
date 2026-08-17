namespace TelemetryGuard.Api.Options;

public sealed class WebhookOptions
{
    public const string SectionName = "Webhooks";
    public int MaxAttempts { get; set; } = 10;
    public List<WebhookSubscriber> Subscribers { get; set; } = [];
}

public sealed class WebhookSubscriber
{
    public string Name { get; set; } = "";
    public Guid TenantId { get; set; }
    public string Url { get; set; } = "";
    public string Secret { get; set; } = "";
    public string[] Events { get; set; } = ["rollup.completed"];
    public bool Enabled { get; set; } = true;
}
