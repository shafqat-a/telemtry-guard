using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TelemetryGuard.Analytics.Abstractions;
using TelemetryGuard.Core.Tenancy;
using TelemetryGuard.Data;
using TelemetryGuard.Portal;
using TelemetryGuard.Portal.Api;
using TelemetryGuard.Tests.Integration.Sql;

namespace TelemetryGuard.Tests.Integration.Portal;

/// <summary>
/// P2-03: the portal driven end to end against the REAL admin API (real SQL Server
/// + real Redis via SqlServerFixture, ClickHouse deliberately unreachable — D23
/// admin reads never touch it). This is the wire-contract drift guard for every DTO
/// in Api/AdminApiDtos.cs: if a field name in the real API response ever drifts from
/// the portal's local mirror, one of these deserializations breaks here, not in
/// production. No Playwright, no browser, no Node — every assertion is on the
/// rendered HTML string or a direct SQL read through the fixture.
/// </summary>
[Collection("sqlserver")]
[Trait("Category", "Integration")]
public sealed class PortalAdminApiContractTests(SqlServerFixture fx)
{
    private sealed class CapturingLabelSink : ILabelSink
    {
        public readonly List<LabelEvent> Labels = [];

        public ValueTask WriteAsync(LabelEvent label, CancellationToken ct)
        {
            lock (Labels) Labels.Add(label);
            return ValueTask.CompletedTask;
        }
    }

    private static readonly Regex TokenPattern = new(
        """name="__RequestVerificationToken"[^>]*value="([^"]+)""", RegexOptions.Compiled);

    private static string ExtractToken(string html)
    {
        var match = TokenPattern.Match(html);
        if (!match.Success)
            throw new InvalidOperationException("No __RequestVerificationToken found in the rendered HTML.");
        return match.Groups[1].Value;
    }

    private static string FreshIp(string subnet) => $"{subnet}.{Random.Shared.Next(1, 254)}";

    /// <summary>Wires a portal WebApplicationFactory whose admin-API HttpClient talks
    /// directly to the real API's WebApplicationFactory in-process (apiFactory.Server
    /// .CreateHandler()) — the same object graph a real deployment uses, minus the
    /// network hop.</summary>
    private sealed class PortalHarness : IDisposable
    {
        public readonly WebApplicationFactory<Program> ApiFactory;
        public readonly WebApplicationFactory<PortalEntryPoint> PortalFactory;
        public readonly CapturingLabelSink Labels = new();

        public PortalHarness(SqlServerFixture fixture)
        {
            ApiFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                var overrides = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Main"] = fixture.ConnectionString,
                    ["ConnectionStrings:Redis"] = fixture.RedisConnectionString,
                    // Unreachable on purpose: D23 — admin reads never touch ClickHouse.
                    ["Analytics:ClickHouse:ConnectionString"] = "Host=localhost;Port=1;Database=telemetry_guard",
                };
                foreach (var (k, v) in overrides)
                    b.UseSetting(k, v);
                b.ConfigureTestServices(services =>
                {
                    services.RemoveAll<ILabelSink>();
                    services.AddSingleton<ILabelSink>(Labels);
                });
            });

            PortalFactory = new WebApplicationFactory<PortalEntryPoint>().WithWebHostBuilder(b =>
            {
                b.UseSolutionRelativeContentRoot("TelemetryGuard.Portal");
                b.UseSetting("Portal:ApiBaseUrl", "http://api.local");
                b.ConfigureTestServices(services =>
                {
                    services.AddHttpClient<IAdminApiClient, PortalApiClient>(c => c.BaseAddress = new Uri("http://api.local"))
                        .ConfigurePrimaryHttpMessageHandler(() => ApiFactory.Server.CreateHandler());
                });
            });
        }

        public HttpClient Client() =>
            PortalFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        public async Task<HttpClient> SignedInClientAsync(string apiKey)
        {
            var client = Client();
            var getResp = await client.GetAsync("/signin");
            var token = ExtractToken(await getResp.Content.ReadAsStringAsync());

            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["ApiKey"] = apiKey,
                ["returnUrl"] = "",
            });
            var post = await client.PostAsync("/signin", form);
            if (post.StatusCode != HttpStatusCode.Redirect)
            {
                throw new InvalidOperationException(
                    $"Sign-in did not redirect (status {post.StatusCode}): {await post.Content.ReadAsStringAsync()}");
            }

            return client;
        }

        public void Dispose()
        {
            PortalFactory.Dispose();
            ApiFactory.Dispose();
        }
    }

    // ================================================== wire-contract drift guard =

    [Fact]
    public async Task AllFourScreens_RenderOk_AgainstTheRealApi_ProvingEveryDtoDeserializes()
    {
        using var app = new PortalHarness(fx);
        using var client = await app.SignedInClientAsync(SqlServerFixture.ApiKeyA);

        foreach (var path in new[] { "/", "/review", "/enforcement", "/whitelist" })
        {
            var resp = await client.GetAsync(path);
            var html = await resp.Content.ReadAsStringAsync();
            Assert.True(HttpStatusCode.OK == resp.StatusCode, $"{path} returned {resp.StatusCode}: {html}");
        }
    }

    [Fact]
    public async Task Dashboard_ListsTheSeededCampaign_AndTheSeededSiteAtJsIntegration()
    {
        using var app = new PortalHarness(fx);
        using var client = await app.SignedInClientAsync(SqlServerFixture.ApiKeyA);

        var html = await (await client.GetAsync("/")).Content.ReadAsStringAsync();

        Assert.Contains(SqlServerFixture.CampaignA1.ToString("D"), html);
        Assert.Contains(SqlServerFixture.SiteKeyA, html);
        Assert.Contains("a.example.com", html);
    }

    // ============================================================= D19 override =

    [Fact]
    public async Task ReviewOverride_WithSessionId_WritesRealWhitelistRow_AndExactlyOneLabel()
    {
        using var app = new PortalHarness(fx);
        using var client = await app.SignedInClientAsync(SqlServerFixture.ApiKeyA);

        var value = FreshIp("203.0.113");
        var sessionId = $"sess{Guid.NewGuid():N}"[..32];

        var getResp = await client.GetAsync("/review");
        var token = ExtractToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["sourceType"] = "ip",
            ["value"] = value,
            ["sessionId"] = sessionId,
            ["reason"] = "integration test override",
        });
        var resp = await client.PostAsync("/review?handler=Override", form);
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);

        await using (var conn = await fx.OpenAsync(SqlServerFixture.TenantA))
        {
            var row = await conn.QuerySingleAsync<(string Source, string Value)>(
                "SELECT Source, Value FROM dbo.WhitelistEntries WHERE TenantId = @TenantId AND SourceType = 'ip' AND Value = @Value",
                new { TenantId = SqlServerFixture.TenantA, Value = value });
            Assert.Equal("review_screen", row.Source);
        }

        var label = Assert.Single(app.Labels.Labels);
        Assert.Equal(LabelValues.Legit, label.Label);
        Assert.Equal(LabelSources.ReviewScreen, label.LabelSource);
        Assert.Equal(sessionId, label.SessionId);
        Assert.Equal(new TenantId(SqlServerFixture.TenantA), label.TenantId);
    }

    [Fact]
    public async Task ReviewOverride_WithoutSessionId_WritesManualRow_AndNoLabel()
    {
        using var app = new PortalHarness(fx);
        using var client = await app.SignedInClientAsync(SqlServerFixture.ApiKeyA);

        var value = FreshIp("198.51.100");

        var getResp = await client.GetAsync("/review");
        var token = ExtractToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["sourceType"] = "ip",
            ["value"] = value,
            ["sessionId"] = "",
        });
        var resp = await client.PostAsync("/review?handler=Override", form);
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);

        await using (var conn = await fx.OpenAsync(SqlServerFixture.TenantA))
        {
            var source = await conn.QuerySingleAsync<string>(
                "SELECT Source FROM dbo.WhitelistEntries WHERE TenantId = @TenantId AND SourceType = 'ip' AND Value = @Value",
                new { TenantId = SqlServerFixture.TenantA, Value = value });
            Assert.Equal("manual", source);
        }

        Assert.Empty(app.Labels.Labels);
    }

    // =========================================================== cross-tenant =

    [Fact]
    public async Task TenantA_Portal_NeverShowsTenantBsWhitelistOrEnforcementRows()
    {
        using var app = new PortalHarness(fx);

        var bWhitelistValue = FreshIp("192.0.2");
        var bExclusionValue = FreshIp("192.0.2");

        await using (var sys = await fx.OpenAsync(WellKnownTenants.System))
        {
            await sys.ExecuteAsync(
                "INSERT INTO dbo.WhitelistEntries (TenantId, SourceType, Value, Source) VALUES (@TenantId, 'ip', @Value, 'manual')",
                new { TenantId = SqlServerFixture.TenantB, Value = bWhitelistValue });
            await sys.ExecuteAsync(
                """
                INSERT INTO dbo.ExclusionQueue (TenantId, Platform, SourceType, Value, Reason, Status)
                VALUES (@TenantId, 'google', 'ip', @Value, N'tenant B seed', 'pending')
                """,
                new { TenantId = SqlServerFixture.TenantB, Value = bExclusionValue });
        }

        using var client = await app.SignedInClientAsync(SqlServerFixture.ApiKeyA);

        var whitelistHtml = await (await client.GetAsync("/whitelist")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(bWhitelistValue, whitelistHtml);

        var enforcementHtml = await (await client.GetAsync("/enforcement?status=pending")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(bExclusionValue, enforcementHtml);
    }

    // ============================================================= enforcement =

    [Fact]
    public async Task ApprovingASeededPendingExclusion_FlipsItInSql_AndWritesOneAuditRow_ThenIsIdempotent()
    {
        using var app = new PortalHarness(fx);

        var value = FreshIp("203.0.113");
        long exclusionId;
        await using (var sys = await fx.OpenAsync(WellKnownTenants.System))
        {
            exclusionId = await sys.QuerySingleAsync<long>(
                """
                INSERT INTO dbo.ExclusionQueue (TenantId, Platform, SourceType, Value, Reason, Status)
                OUTPUT INSERTED.Id
                VALUES (@TenantId, 'google', 'ip', @Value, N'integration test seed', 'pending')
                """,
                new { TenantId = SqlServerFixture.TenantA, Value = value });
        }

        using var client = await app.SignedInClientAsync(SqlServerFixture.ApiKeyA);
        var getResp = await client.GetAsync("/enforcement?status=pending");
        var token = ExtractToken(await getResp.Content.ReadAsStringAsync());

        var content = new StringContent(
            $"__RequestVerificationToken={Uri.EscapeDataString(token)}&ids={exclusionId}",
            System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
        var resp = await client.PostAsync("/enforcement?handler=Approve", content);
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);

        await using (var conn = await fx.OpenAsync(SqlServerFixture.TenantA))
        {
            var status = await conn.QuerySingleAsync<string>(
                "SELECT Status FROM dbo.ExclusionQueue WHERE TenantId = @TenantId AND Id = @Id",
                new { TenantId = SqlServerFixture.TenantA, Id = exclusionId });
            Assert.Equal("approved", status);

            var auditCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.EnforcementAudit WHERE TenantId = @TenantId AND ExclusionQueueId = @Id",
                new { TenantId = SqlServerFixture.TenantA, Id = exclusionId });
            Assert.Equal(1, auditCount);
        }

        // Re-approving the same (now non-pending) id is a safe no-op: 0 of 1.
        var getResp2 = await client.GetAsync("/enforcement?status=pending");
        var token2 = ExtractToken(await getResp2.Content.ReadAsStringAsync());
        var content2 = new StringContent(
            $"__RequestVerificationToken={Uri.EscapeDataString(token2)}&ids={exclusionId}",
            System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
        var resp2 = await client.PostAsync("/enforcement?handler=Approve", content2);
        var follow = await client.GetAsync(resp2.Headers.Location);
        var html = await follow.Content.ReadAsStringAsync();
        Assert.Contains("0 of 1 approved", html);
    }
}
