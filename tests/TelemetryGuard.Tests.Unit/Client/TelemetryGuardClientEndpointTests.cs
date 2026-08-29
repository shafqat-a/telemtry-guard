using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TelemetryGuard.Client;

namespace TelemetryGuard.Tests.Unit.Client;

public sealed class TelemetryGuardClientEndpointTests
{
    [Fact]
    public async Task Native_surface_has_no_decision_or_enforcement_endpoint()
    {
        await using var host = await StartAsync();
        var response = await host.App.GetTestClient().PostAsync("/tg/decide", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Beacon_is_scheduled_for_finalization_not_relayed_inline()
    {
        await using var host = await StartAsync();
        var json = """
            {
              "k":"site-public-key",
              "session_id":"session_12345678",
              "visit_id":"visit_12345678",
              "device_id":"device_12345678",
              "sent_at":1787976000000,
              "u":"https://bu.edu.bd/sports?utm_source=facebook&utm_campaign_id=269",
              "r":"https://facebook.com/",
              "events":[{"e":"pm","t":1000,"s":[[1000,10,20],[1100,20,30]]},{"e":"sc","t":1200,"y":300}]
            }
            """;
        var response = await host.App.GetTestClient().PostAsync("/tg/i",
            new StringContent(json, Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Single(host.Store.Scheduled);
        Assert.Empty(host.Relay.Submissions);
        Assert.Contains("\"companyId\":2", host.Store.Scheduled[0].Body);
        Assert.Contains("\"mouse_events\":2", host.Store.Scheduled[0].Body);
        Assert.Contains("\"scroll_events\":1", host.Store.Scheduled[0].Body);
        Assert.Contains("\"utm_platform\":\"facebook\"", host.Store.Scheduled[0].Body);
    }

    private static async Task<TestHost> StartAsync()
    {
        var settings = new Dictionary<string, string?>
        {
            ["TelemetryGuard:Enabled"] = "true",
            ["TelemetryGuard:Redis:ConnectionString"] = "unused-by-test",
            ["TelemetryGuard:Sites:site-public-key:TenantId"] = "9dd11629-09ec-4d66-afa8-929cf1c00ac4",
            ["TelemetryGuard:Sites:site-public-key:CompanyId"] = "2",
            ["TelemetryGuard:Sites:site-public-key:Domain"] = "bu.edu.bd",
        };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(settings);
        var store = new FakeStore();
        var relay = new FakeRelay();
        builder.Services.AddSingleton<ITelemetryGuardSessionStore>(store);
        builder.Services.AddSingleton<ITelemetryGuardVisitQueue>(store);
        builder.Services.AddSingleton<ITelemetryGuardRelay>(relay);
        builder.Services.AddTelemetryGuardClient(builder.Configuration);
        var app = builder.Build();
        app.MapTelemetryGuardClient();
        await app.StartAsync();
        return new TestHost(app, store, relay);
    }

    private sealed record TestHost(WebApplication App, FakeStore Store, FakeRelay Relay)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    private sealed class FakeRelay : ITelemetryGuardRelay
    {
        public List<TelemetryGuardSubmission> Submissions { get; } = [];
        public Task RelayAsync(TelemetryGuardSubmission submission, CancellationToken ct = default)
        {
            Submissions.Add(submission);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeStore : ITelemetryGuardSessionStore, ITelemetryGuardVisitQueue
    {
        public List<TelemetryGuardSubmission> Scheduled { get; } = [];
        public Task<TelemetryGuardSessionState> UpdateAsync(Guid tenantId, string sessionId,
            TelemetryGuardObservation o, TimeSpan ttl, CancellationToken ct = default)
            => Task.FromResult(new TelemetryGuardSessionState
            {
                FirstSeenUnixMs = o.SeenUnixMs,
                LastSeenUnixMs = o.SeenUnixMs,
                MouseEvents = o.MouseEvents,
                TouchEvents = o.TouchEvents,
                ScrollEvents = o.ScrollEvents,
                Keystrokes = o.Keystrokes,
            });
        public Task StoreNonceAsync(Guid tenantId, string sessionId, string nonce, TimeSpan ttl,
            CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ValidateNonceAsync(Guid tenantId, string sessionId, string nonce,
            CancellationToken ct = default) => Task.FromResult(true);
        public Task ScheduleAsync(TelemetryGuardSubmission submission, DateTimeOffset due,
            TimeSpan ttl, CancellationToken ct = default)
        {
            Scheduled.Add(submission);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<TelemetryGuardPendingVisit>> ClaimDueAsync(int max, TimeSpan lease,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TelemetryGuardPendingVisit>>([]);
        public Task CompleteAsync(TelemetryGuardPendingVisit visit, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task RetryAsync(TelemetryGuardPendingVisit visit, DateTimeOffset due,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
