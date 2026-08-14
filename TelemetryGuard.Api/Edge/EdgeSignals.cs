using System.Net;
using Microsoft.Extensions.Options;

namespace TelemetryGuard.Api.Edge;

/// <summary>INT-05: signals forwarded by the Cloudflare edge worker
/// (<c>infra/cloudflare/tg-edge-worker.js</c>) as X-TG-* headers. All-null when not
/// Cloudflare-fronted, when the plan lacks Bot Management (only ASN survives on
/// Free/Pro/Business — see the runbook's honesty table), or when the direct peer was
/// not Cloudflare — downstream this is NaN/null, never zero (spec §7).</summary>
public sealed record EdgeSignals(string? Ja3, string? Ja4, uint? Asn, int? BotScore)
{
    public static readonly EdgeSignals None = new(null, null, null, null);
}

public interface IEdgeSignalReader
{
    EdgeSignals Read(HttpContext ctx);
}

/// <summary>Two-layer spoof defense (both mandatory, per the runbook): (1) the edge
/// worker deletes any client-supplied X-TG-* headers before setting its own; (2) this
/// reader honors X-TG-* ONLY when <see cref="EdgeOptions.IsCloudflare"/> AND the
/// ORIGINAL direct peer (pre-ForwardedHeaders) was inside a Cloudflare range — an
/// attacker hitting the origin directly (or a non-CF proxy in the chain) must not be
/// able to inject fingerprints. Register singleton — stateless, options-only.</summary>
public sealed class EdgeSignalReader(IOptions<EdgeOptions> options) : IEdgeSignalReader
{
    public EdgeSignals Read(HttpContext ctx)
    {
        if (!options.Value.IsCloudflare)
        {
            return EdgeSignals.None;
        }

        var peer = GetOriginalPeer(ctx);
        if (peer is null || !CloudflareIpRanges.Contains(peer))
        {
            return EdgeSignals.None;
        }

        string? H(string name) =>
            ctx.Request.Headers.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v) ? v.ToString() : null;

        return new EdgeSignals(
            H("X-TG-JA3"), H("X-TG-JA4"),
            uint.TryParse(H("X-TG-ASN"), out var asn) ? asn : null,
            int.TryParse(H("X-TG-Bot-Score"), out var bs) ? bs : null);
    }

    /// <summary>ASP.NET Core's ForwardedHeadersMiddleware, when it swaps
    /// <c>RemoteIpAddress</c> from a trusted proxy's forwarded value, preserves the
    /// pre-swap (i.e. the DIRECT socket peer) address in the request header
    /// "X-Original-For" as "ip:port" ("[ipv6]:port" for v6) — a built-in ASP.NET Core
    /// behavior, not something this task writes. When that header is absent (no
    /// forwarding applied — untrusted or missing X-Forwarded-For), RemoteIpAddress
    /// already IS the direct peer.</summary>
    private static IPAddress? GetOriginalPeer(HttpContext ctx)
    {
        if (ctx.Request.Headers.TryGetValue("X-Original-For", out var values) && values.Count > 0
            && values[0] is { Length: > 0 } raw
            && IPAddress.TryParse(StripPort(raw), out var original))
        {
            return original;
        }

        return ctx.Connection.RemoteIpAddress;
    }

    /// <summary>"203.0.113.9:1234" -> "203.0.113.9"; "[2606:4700::1]:1234" ->
    /// "2606:4700::1"; a bare address (no port) passes through unchanged.</summary>
    private static string StripPort(string value)
    {
        value = value.Trim();
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            return close > 0 ? value[1..close] : value;
        }

        // A single colon means "ipv4:port"; more than one means a bare (portless)
        // IPv6 literal like "::1", which must be left alone.
        var firstColon = value.IndexOf(':');
        var lastColon = value.LastIndexOf(':');
        return firstColon >= 0 && firstColon == lastColon ? value[..lastColon] : value;
    }
}
