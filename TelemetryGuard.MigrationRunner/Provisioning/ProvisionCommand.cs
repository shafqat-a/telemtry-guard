using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TelemetryGuard.Data;

namespace TelemetryGuard.MigrationRunner.Provisioning;

/// <summary>
/// DAT-09 tenant provisioning verbs. Every write goes through DAT-03's
/// <see cref="ISystemConnectionFactory.OpenForTenantAsync"/> with an explicit
/// TenantId value in the SQL — per-tenant stamping keeps the RLS BLOCK predicate
/// meaningful (row value must equal session value), and stamping does not require
/// the tenant to exist, which is exactly how create-tenant works. The SYSTEM
/// sentinel is never stamped here and never written as data.
/// </summary>
public static partial class ProvisionCommand
{
    private const string DefaultScopes = "admin ingest report";

    [GeneratedRegex("^tg_ak_[0-9A-Za-z]{43}$")]
    private static partial Regex ApiKeyShape();

    [GeneratedRegex("^tg_sk_[0-9A-Za-z]{22}$")]
    private static partial Regex SiteKeyShape();

    private static ServiceProvider BuildProvider(string connectionString)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Main"] = connectionString
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(cfg);
        services.AddTelemetryGuardData();   // DAT-03: registers ISystemConnectionFactory
        return services.BuildServiceProvider();
    }

    /// <summary>CLI entry: args exclude the leading "provision". Returns process exit code
    /// (0 ok/skip, 1 database failure, 2 usage/validation error).</summary>
    public static async Task<int> RunAsync(string[] args, string connectionString)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Missing provision subcommand.");
            PrintUsage();
            return 2;
        }

        var subcommand = args[0].ToLowerInvariant();
        try
        {
            var flags = ParseFlags(args[1..]);
            switch (subcommand)
            {
                case "create-tenant":
                    RequireFlags(flags,
                        required: ["name"],
                        optional: ["retention-days", "enforcement-mode", "tenant-id"]);
                    await CreateTenantAsync(connectionString,
                        flags.TryGetValue("tenant-id", out var ctTid) ? Guid.Parse(ctTid) : null,
                        flags["name"],
                        flags.TryGetValue("retention-days", out var rd)
                            ? int.Parse(rd, CultureInfo.InvariantCulture) : 90,
                        flags.TryGetValue("enforcement-mode", out var em)
                            ? ParseEnforcementMode(em) : (byte)0);
                    return 0;

                case "issue-api-key":
                    RequireFlags(flags,
                        required: ["tenant-id"],
                        optional: ["scopes", "key"]);
                    await IssueApiKeyAsync(connectionString,
                        Guid.Parse(flags["tenant-id"]),
                        flags.GetValueOrDefault("scopes", DefaultScopes),
                        flags.GetValueOrDefault("key"));
                    return 0;

                case "register-site":
                    RequireFlags(flags,
                        required: ["tenant-id", "domain"],
                        optional: ["integration-mode", "site-key"]);
                    await RegisterSiteAsync(connectionString,
                        Guid.Parse(flags["tenant-id"]),
                        flags["domain"],
                        flags.GetValueOrDefault("integration-mode", "js"),
                        flags.GetValueOrDefault("site-key"));
                    return 0;

                case "create-campaign":
                    RequireFlags(flags,
                        required: ["tenant-id", "platform", "landing-url"],
                        optional: ["external-id", "geo-targets", "campaign-id"]);
                    await CreateCampaignAsync(connectionString,
                        Guid.Parse(flags["tenant-id"]),
                        flags["platform"],
                        flags.GetValueOrDefault("external-id"),
                        flags["landing-url"],
                        flags.GetValueOrDefault("geo-targets"),
                        flags.TryGetValue("campaign-id", out var cid) ? Guid.Parse(cid) : null);
                    return 0;

                default:
                    Console.Error.WriteLine($"Unknown provision subcommand '{args[0]}'.");
                    PrintUsage();
                    return 2;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            // Validation/usage errors — guards throw before any database work.
            Console.Error.WriteLine($"Error: {ex.Message}");
            PrintUsage();
            return 2;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    public static async Task<Guid> CreateTenantAsync(string connectionString, Guid? tenantId,
        string name, int retentionDays, byte enforcementMode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
            throw new ArgumentException("name is required, max 200 chars.", nameof(name));
        if (retentionDays is < 30 or > 180)
            throw new ArgumentOutOfRangeException(nameof(retentionDays), "D20: 30..180.");
        if (enforcementMode > 1)
            throw new ArgumentOutOfRangeException(nameof(enforcementMode), "D21: 0=AutoEnforce, 1=ApprovalQueue.");
        var tid = tenantId ?? Guid.NewGuid();
        if (tid == Guid.Empty || tid == WellKnownTenants.System)
            throw new ArgumentOutOfRangeException(nameof(tenantId),
                "TenantId must be a real id — never Guid.Empty or the SYSTEM sentinel.");

        await using var provider = BuildProvider(connectionString);
        var factory = provider.GetRequiredService<ISystemConnectionFactory>();
        await using var conn = await factory.OpenForTenantAsync(tid, ct);

        // Idempotent re-seed path: only reachable when an explicit --tenant-id was given.
        var exists = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Tenants WHERE TenantId = @TenantId", new { TenantId = tid });
        if (tenantId is not null && exists > 0)
        {
            Console.WriteLine($"Tenant {tid} already exists; skipping.");
            return tid;
        }

        await conn.ExecuteAsync(
            """
            INSERT INTO dbo.Tenants (TenantId, Name, RetentionDays, EnforcementMode)
            VALUES (@TenantId, @Name, @RetentionDays, @EnforcementMode)
            """,
            new { TenantId = tid, Name = name, RetentionDays = retentionDays, EnforcementMode = enforcementMode });
        Console.WriteLine($"TenantId: {tid}");
        return tid;
    }

    public static async Task<string> IssueApiKeyAsync(string connectionString, Guid tenantId,
        string scopes, string? rawKey = null, CancellationToken ct = default)
    {
        GuardRealTenantId(tenantId);
        if (string.IsNullOrWhiteSpace(scopes)) scopes = DefaultScopes;
        if (scopes.Length > 400)
            throw new ArgumentException("scopes is limited to 400 chars (space-separated).", nameof(scopes));
        var explicitKey = rawKey is not null;
        if (rawKey is not null && !ApiKeyShape().IsMatch(rawKey))
            throw new ArgumentException(
                $"--key must match ^{KeyGenerator.ApiKeyPrefix}[0-9A-Za-z]{{43}}$.", nameof(rawKey));
        rawKey ??= KeyGenerator.NewApiKey();
        var keyHash = KeyGenerator.Sha256(rawKey);

        await using var provider = BuildProvider(connectionString);
        var factory = provider.GetRequiredService<ISystemConnectionFactory>();
        await using var conn = await factory.OpenForTenantAsync(tenantId, ct);

        await EnsureTenantExistsAsync(conn, tenantId);

        // Idempotent re-seed path: only reachable when an explicit --key was given.
        if (explicitKey)
        {
            var exists = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.ApiKeys WHERE KeyHash = @KeyHash", new { KeyHash = keyHash });
            if (exists > 0)
            {
                Console.WriteLine("ApiKey already exists; skipping.");
                return rawKey;
            }
        }

        await conn.ExecuteAsync(
            "INSERT INTO dbo.ApiKeys (KeyHash, TenantId, Scopes) VALUES (@KeyHash, @TenantId, @Scopes);",
            new { KeyHash = keyHash, TenantId = tenantId, Scopes = scopes });

        // The ONLY sanctioned raw-key output — never stored, never logged elsewhere.
        Console.WriteLine($"ApiKey: {rawKey}   (printed ONCE — store it now; only its SHA-256 hash is kept)");
        return rawKey;
    }

    public static async Task<string> RegisterSiteAsync(string connectionString, Guid tenantId,
        string domain, string integrationMode, string? siteKey = null, CancellationToken ct = default)
    {
        GuardRealTenantId(tenantId);
        if (string.IsNullOrWhiteSpace(domain) || domain.Length > 253)
            throw new ArgumentException("domain is required, max 253 chars.", nameof(domain));
        if (integrationMode is not ("js" or "pixel"))
            throw new ArgumentException("D22: integration-mode must be 'js' or 'pixel'.", nameof(integrationMode));
        var explicitKey = siteKey is not null;
        if (siteKey is not null && !SiteKeyShape().IsMatch(siteKey))
            throw new ArgumentException(
                $"--site-key must match ^{KeyGenerator.SiteKeyPrefix}[0-9A-Za-z]{{22}}$.", nameof(siteKey));
        siteKey ??= KeyGenerator.NewSiteKey();

        await using var provider = BuildProvider(connectionString);
        var factory = provider.GetRequiredService<ISystemConnectionFactory>();
        await using var conn = await factory.OpenForTenantAsync(tenantId, ct);

        await EnsureTenantExistsAsync(conn, tenantId);

        // Idempotent re-seed path: only reachable when an explicit --site-key was given.
        if (explicitKey)
        {
            var exists = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.Sites WHERE TenantId = @TenantId AND SiteKey = @SiteKey",
                new { TenantId = tenantId, SiteKey = siteKey });
            if (exists > 0)
            {
                Console.WriteLine($"Site {siteKey} already exists; skipping.");
                return siteKey;
            }
        }

        await conn.ExecuteAsync(
            """
            INSERT INTO dbo.Sites (TenantId, SiteKey, Domain, IntegrationMode)
            VALUES (@TenantId, @SiteKey, @Domain, @IntegrationMode);
            """,
            new { TenantId = tenantId, SiteKey = siteKey, Domain = domain, IntegrationMode = integrationMode });

        Console.WriteLine($"SiteKey: {siteKey}"); // site keys are public identifiers — no secrecy warning
        return siteKey;
    }

    public static async Task<Guid> CreateCampaignAsync(string connectionString, Guid tenantId,
        string platform, string? externalId, string landingUrl, string? geoTargetsJson,
        Guid? campaignId = null, CancellationToken ct = default)
    {
        GuardRealTenantId(tenantId);
        if (platform is not ("google" or "meta" or "tiktok" or "other"))
            throw new ArgumentException("platform must be google|meta|tiktok|other.", nameof(platform));
        if (!Uri.TryCreate(landingUrl, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            throw new ArgumentException("landing-url must be an absolute http/https URL.", nameof(landingUrl));
        if (externalId is { Length: > 200 })
            throw new ArgumentException("external-id is limited to 200 chars.", nameof(externalId));
        if (geoTargetsJson is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(geoTargetsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    throw new ArgumentException("geo-targets must be a JSON array, e.g. [\"US\"].",
                        nameof(geoTargetsJson));
            }
            catch (JsonException ex)
            {
                throw new ArgumentException($"geo-targets is not valid JSON: {ex.Message}", nameof(geoTargetsJson));
            }
        }
        var cid = campaignId ?? Guid.NewGuid();

        await using var provider = BuildProvider(connectionString);
        var factory = provider.GetRequiredService<ISystemConnectionFactory>();
        await using var conn = await factory.OpenForTenantAsync(tenantId, ct);

        await EnsureTenantExistsAsync(conn, tenantId);

        // Idempotent re-seed path: only reachable when an explicit --campaign-id was given.
        if (campaignId is not null)
        {
            var exists = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.Campaigns WHERE TenantId = @TenantId AND CampaignId = @CampaignId",
                new { TenantId = tenantId, CampaignId = cid });
            if (exists > 0)
            {
                Console.WriteLine($"Campaign {cid} already exists; skipping.");
                return cid;
            }
        }

        await conn.ExecuteAsync(
            """
            INSERT INTO dbo.Campaigns (TenantId, CampaignId, Platform, ExternalCampaignId, LandingUrl, GeoTargets, Status)
            VALUES (@TenantId, @CampaignId, @Platform, @ExternalCampaignId, @LandingUrl, @GeoTargets, 0);
            """,
            new
            {
                TenantId = tenantId,
                CampaignId = cid,
                Platform = platform,
                ExternalCampaignId = externalId,
                LandingUrl = landingUrl,
                GeoTargets = geoTargetsJson
            });

        Console.WriteLine($"CampaignId: {cid}");
        return cid;
    }

    /// <summary>Defense-in-depth pre-SQL guard: DAT-03's OpenForTenantAsync also rejects
    /// both values, but we refuse before building a provider at all.</summary>
    private static void GuardRealTenantId(Guid tenantId)
    {
        if (tenantId == Guid.Empty || tenantId == WellKnownTenants.System)
            throw new ArgumentOutOfRangeException(nameof(tenantId),
                "TenantId must be a real id — never Guid.Empty or the SYSTEM sentinel.");
    }

    private static async Task EnsureTenantExistsAsync(SqlConnection conn, Guid tenantId)
    {
        var tenants = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Tenants WHERE TenantId = @TenantId", new { TenantId = tenantId });
        if (tenants == 0)
            throw new InvalidOperationException("Tenant not found (or not visible under its own context).");
    }

    /// <summary>Hand-rolled flag parsing: a token starting "--" is a flag whose value is
    /// the next token. Throws ArgumentException (exit 2) on malformed input.</summary>
    private static Dictionary<string, string> ParseFlags(string[] args)
    {
        var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i += 2)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length <= 2)
                throw new ArgumentException($"Unexpected token '{token}' — expected a --flag.");
            if (i + 1 >= args.Length)
                throw new ArgumentException($"Flag '{token}' is missing a value.");
            flags[token[2..]] = args[i + 1];
        }
        return flags;
    }

    private static void RequireFlags(Dictionary<string, string> flags, string[] required, string[] optional)
    {
        foreach (var name in required)
            if (!flags.ContainsKey(name))
                throw new ArgumentException($"Missing required flag --{name}.");
        foreach (var name in flags.Keys)
            if (!required.Contains(name, StringComparer.OrdinalIgnoreCase)
                && !optional.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"Unknown flag --{name}.");
    }

    private static byte ParseEnforcementMode(string value) => value.ToLowerInvariant() switch
    {
        "autoenforce" or "0" => 0,
        "approvalqueue" or "1" => 1,
        _ => byte.TryParse(value, out var b)
            ? b // out-of-range numerics fall through to CreateTenantAsync's guard
            : throw new ArgumentException(
                "enforcement-mode must be AutoEnforce|ApprovalQueue or 0|1.")
    };

    private static void PrintUsage() => Console.Error.WriteLine(
        $"""
        Usage:
          provision create-tenant   --name <string> [--retention-days 90] [--enforcement-mode AutoEnforce|ApprovalQueue] [--tenant-id <guid>]
          provision issue-api-key   --tenant-id <guid> [--scopes "admin ingest report"] [--key <{KeyGenerator.ApiKeyPrefix}...>]
          provision register-site   --tenant-id <guid> --domain <host> [--integration-mode js|pixel] [--site-key <{KeyGenerator.SiteKeyPrefix}...>]
          provision create-campaign --tenant-id <guid> --platform google|meta|tiktok|other --landing-url <url>
                                    [--external-id <string>] [--geo-targets '["US"]'] [--campaign-id <guid>]
        """);
}
