using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TelemetryGuard.Api.Options;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Data.Repositories;

namespace TelemetryGuard.Api.Services;

public interface IWebhookPublisher
{
    Task PublishAsync(string eventType, string logicalId, object payload, CancellationToken ct);
}

public sealed class WebhookPublisher(
    IOptions<WebhookOptions> options,
    ITenantContext tenant,
    IWebhookOutboxRepository outbox) : IWebhookPublisher
{
    public async Task PublishAsync(string eventType, string logicalId, object payload, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var subscriber in options.Value.Subscribers.Where(x => x.Enabled
                     && x.TenantId == tenant.TenantId.Value
                     && x.Events.Contains(eventType, StringComparer.Ordinal)))
        {
            _ = await WebhookDestinationValidator.ValidateAsync(subscriber.Url, ct);
            await outbox.EnqueueAsync(new WebhookOutboxInsert(
                DeterministicId($"{tenant.TenantId.Value:D}|{subscriber.Name}|{eventType}|{logicalId}"),
                eventType, subscriber.Url, subscriber.Name, body, DateTime.UtcNow), ct);
        }
    }

    private static Guid DeterministicId(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }
}

internal static class WebhookDestinationValidator
{
    public static async Task<IReadOnlyList<IPAddress>> ValidateAsync(string value, CancellationToken ct)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Webhook destination must be an HTTPS URL without user info.");
        var addresses = await Dns.GetHostAddressesAsync(uri.Host, ct);
        if (addresses.Length == 0 || addresses.Any(IsPrivate))
            throw new InvalidOperationException("Webhook destination resolves to a non-public address.");
        return addresses;
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
            return true;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] is 0 or 10 or 127 || b[0] == 169 && b[1] == 254
                || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168
                || b[0] >= 224;
        }
        return ip.IsIPv6UniqueLocal;
    }
}
