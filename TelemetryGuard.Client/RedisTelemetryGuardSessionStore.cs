using System.Text.Json;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace TelemetryGuard.Client;

/// <summary>Shared session aggregation in the host's Redis deployment.</summary>
public sealed class RedisTelemetryGuardSessionStore :
    ITelemetryGuardSessionStore, ITelemetryGuardVisitQueue, IAsyncDisposable
{
    private readonly IConnectionMultiplexer _redis;
    private readonly string _prefix;

    public RedisTelemetryGuardSessionStore(IOptions<TelemetryGuardClientOptions> options)
    {
        var value = options.Value;
        if (string.IsNullOrWhiteSpace(value.Redis.ConnectionString))
            throw new InvalidOperationException(
                "TelemetryGuard:Redis:ConnectionString is required when native collection is enabled.");

        var config = ConfigurationOptions.Parse(value.Redis.ConnectionString);
        config.AbortOnConnectFail = false;
        _redis = ConnectionMultiplexer.Connect(config);
        _prefix = value.Redis.KeyPrefix;
    }

    public async Task<TelemetryGuardSessionState> UpdateAsync(
        Guid tenantId, string sessionId, TelemetryGuardObservation o, TimeSpan ttl,
        CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var key = SessionKey(tenantId, sessionId);

        // One short optimistic transaction keeps replicas from replacing each other's counts.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var oldValue = await db.StringGetAsync(key).ConfigureAwait(false);
            var old = oldValue.HasValue
                ? JsonSerializer.Deserialize<TelemetryGuardSessionState>((string)oldValue!)
                : null;
            var next = Merge(old, o);
            var transaction = db.CreateTransaction();
            transaction.AddCondition(oldValue.HasValue
                ? Condition.StringEqual(key, oldValue)
                : Condition.KeyNotExists(key));
            _ = transaction.StringSetAsync(key, JsonSerializer.Serialize(next), ttl);
            if (await transaction.ExecuteAsync().ConfigureAwait(false)) return next;
        }

        throw new InvalidOperationException("TelemetryGuard Redis session update was contended.");
    }

    public Task StoreNonceAsync(Guid tenantId, string sessionId, string nonce, TimeSpan ttl,
        CancellationToken ct = default)
        => _redis.GetDatabase().StringSetAsync(NonceKey(tenantId, sessionId), nonce, ttl);

    public async Task<bool> ValidateNonceAsync(Guid tenantId, string sessionId, string nonce,
        CancellationToken ct = default)
        => !string.IsNullOrEmpty(nonce)
           && await _redis.GetDatabase().StringGetAsync(NonceKey(tenantId, sessionId)) == nonce;

    public async Task ScheduleAsync(TelemetryGuardSubmission submission, DateTimeOffset due,
        TimeSpan ttl, CancellationToken ct = default)
    {
        var token = PendingToken(submission);
        var db = _redis.GetDatabase();
        var transaction = db.CreateTransaction();
        _ = transaction.StringSetAsync(PendingPayloadKey(token),
            JsonSerializer.Serialize(submission), ttl);
        _ = transaction.SortedSetAddAsync(PendingSetKey(), token, due.ToUnixTimeMilliseconds());
        if (!await transaction.ExecuteAsync().ConfigureAwait(false))
            throw new InvalidOperationException("TelemetryGuard visit scheduling failed.");
    }

    public async Task<IReadOnlyList<TelemetryGuardPendingVisit>> ClaimDueAsync(
        int max, TimeSpan lease, CancellationToken ct = default)
    {
        const string script = """
            local members=redis.call('ZRANGEBYSCORE',KEYS[1],'-inf',ARGV[1],'LIMIT',0,ARGV[2])
            for _,member in ipairs(members) do
              redis.call('ZADD',KEYS[1],ARGV[3],member)
            end
            return members
            """;
        var now = DateTimeOffset.UtcNow;
        var result = (RedisResult[]?)await _redis.GetDatabase().ScriptEvaluateAsync(
            script, [PendingSetKey()],
            [now.ToUnixTimeMilliseconds(), max, now.Add(lease).ToUnixTimeMilliseconds()])
            .ConfigureAwait(false) ?? [];
        if (result.Length == 0) return [];

        var tokens = result.Select(x => x.ToString()).Where(x => x.Length > 0).ToArray();
        var values = await _redis.GetDatabase().StringGetAsync(
            tokens.Select(PendingPayloadKey).ToArray()).ConfigureAwait(false);
        var visits = new List<TelemetryGuardPendingVisit>(tokens.Length);
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!values[i].HasValue) continue;
            var submission = JsonSerializer.Deserialize<TelemetryGuardSubmission>((string)values[i]!);
            visits.Add(new(tokens[i], submission));
        }
        return visits;
    }

    public async Task CompleteAsync(TelemetryGuardPendingVisit visit, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var transaction = db.CreateTransaction();
        _ = transaction.SortedSetRemoveAsync(PendingSetKey(), visit.Token);
        _ = transaction.KeyDeleteAsync(PendingPayloadKey(visit.Token));
        await transaction.ExecuteAsync().ConfigureAwait(false);
    }

    public Task RetryAsync(TelemetryGuardPendingVisit visit, DateTimeOffset due,
        CancellationToken ct = default)
        => _redis.GetDatabase().SortedSetAddAsync(
            PendingSetKey(), visit.Token, due.ToUnixTimeMilliseconds());

    private static TelemetryGuardSessionState Merge(
        TelemetryGuardSessionState? s, TelemetryGuardObservation o) => new()
    {
        FirstSeenUnixMs = s?.FirstSeenUnixMs is > 0 ? s.FirstSeenUnixMs : o.SeenUnixMs,
        LastSeenUnixMs = Math.Max(s?.LastSeenUnixMs ?? 0, o.SeenUnixMs),
        MouseEvents = (s?.MouseEvents ?? 0) + o.MouseEvents,
        TouchEvents = (s?.TouchEvents ?? 0) + o.TouchEvents,
        ScrollEvents = (s?.ScrollEvents ?? 0) + o.ScrollEvents,
        Keystrokes = (s?.Keystrokes ?? 0) + o.Keystrokes,
        PagesViewed = Math.Max(1, s?.PagesViewed ?? 0),
        WebDriver = (s?.WebDriver ?? false) || o.WebDriver,
        Headless = (s?.Headless ?? false) || o.Headless,
        HoneypotFieldFilled = (s?.HoneypotFieldFilled ?? false) || o.HoneypotFieldFilled,
        HoneypotLinkClicked = (s?.HoneypotLinkClicked ?? false) || o.HoneypotLinkClicked,
        FormSubmitted = (s?.FormSubmitted ?? false) || o.FormSubmitted,
        PasteInIdentityField = (s?.PasteInIdentityField ?? false) || o.PasteInIdentityField,
    };

    private RedisKey SessionKey(Guid tenantId, string sid) => $"{_prefix}{tenantId:D}:session:{sid}";
    private RedisKey NonceKey(Guid tenantId, string sid) => $"{_prefix}{tenantId:D}:nonce:{sid}";
    private RedisKey PendingSetKey() => $"{_prefix}pending-visits";
    private RedisKey PendingPayloadKey(string token) => $"{_prefix}pending:{token}";
    private static string PendingToken(TelemetryGuardSubmission submission)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(
                $"{submission.TenantId:D}|{submission.SiteKey}|{submission.EventId}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public async ValueTask DisposeAsync() => await _redis.DisposeAsync().ConfigureAwait(false);
}
