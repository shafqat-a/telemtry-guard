namespace TelemetryGuard.Integrations.Meta;

/// <summary>One publisher block list, as returned by the Marketing API.</summary>
public sealed record MetaBlockList(string Id, string Name);

/// <summary>Graph error envelope, mapped. Retryable: transient HTTP (5xx/timeout) and
/// rate limiting (code 17, or 4 with error_subcode 2446079). Everything else is definitive.</summary>
public sealed class MetaApiException(
    string message, string? type, int? code, int? errorSubcode, string? fbtraceId, bool retryable)
    : Exception(message)
{
    public string? ErrorType { get; } = type;
    public int? Code { get; } = code;
    public int? ErrorSubcode { get; } = errorSubcode;
    public string? FbTraceId { get; } = fbtraceId;
    public bool Retryable { get; } = retryable;
}

/// <summary>Thin, deliberately small Marketing API surface (spec D15). Never grow this
/// beyond the three calls below — no campaign management, no targeting mutation, no
/// insights/reporting endpoints, no general request builder.
///
/// HONESTY: Meta provides NO IP-exclusion API — this client cannot and will not
/// offer one; ip exclusions are handled as Status=Unsupported by the sync worker
/// (TelemetryGuard.Api.Workers.MetaExclusionSyncService), never by pretending an
/// IP was blocked. Only 'placement' (publisher) exclusions are supported here, via
/// business-level publisher block lists.
/// </summary>
public interface IMetaMarketingClient
{
    /// <summary>GET /{businessId}/publisher_block_lists — find our list by exact name; null when absent.</summary>
    Task<MetaBlockList?> FindBlockListAsync(string businessId, string name, CancellationToken ct);

    /// <summary>POST /{businessId}/block_list_drafts — create the list with initial URLs; returns its id.</summary>
    Task<string> CreateBlockListAsync(string businessId, string name,
        IReadOnlyList<string> publisherUrls, CancellationToken ct);

    /// <summary>POST /{blockListId} — append publisher URLs to an existing list. Idempotent
    /// (Meta ignores duplicates within a list).</summary>
    Task AddPublisherUrlsAsync(string blockListId,
        IReadOnlyList<string> publisherUrls, CancellationToken ct);
}
