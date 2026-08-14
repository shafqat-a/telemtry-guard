using System.Net;
using ClickHouse.Client.ADO;
using ClickHouse.Client.Utility;
using Dapper;
using Microsoft.Data.SqlClient;
using TelemetryGuard.Analytics.Abstractions;

namespace TelemetryGuard.Training;

/// <summary>Per-tenant, per-window outcome of one <see cref="LabelBuilder"/> run.</summary>
public sealed record LabelBuildTenantResult(
    Guid TenantId, int T1PositivesInserted, int SyntheticBotCounted, int ReviewScreenBackfilled);

public sealed record LabelBuildResult(IReadOnlyList<LabelBuildTenantResult> PerTenant)
{
    public int TotalT1Positives => PerTenant.Sum(r => r.T1PositivesInserted);
    public int TotalSyntheticBotCounted => PerTenant.Sum(r => r.SyntheticBotCounted);
    public int TotalReviewScreenBackfilled => PerTenant.Sum(r => r.ReviewScreenBackfilled);
}

/// <summary>
/// RSK-08 step 3 (`build-labels` command): writes ANA-02's EXISTING tg_labels shape.
/// Tenancy (D11): iterates tenants explicitly via a SYSTEM-sentinel-stamped SQL
/// Server read (mirrors TelemetryGuard.Data's ISystemConnectionFactory pattern, kept
/// local here to hold Training's dependency surface to RiskEngine.Contracts +
/// Analytics.Abstractions only), then every subsequent SQL statement is stamped for
/// that ONE tenant (RLS, D9/D11) and every ClickHouse query filters WHERE
/// tenant_id = ? explicitly — never an unscoped cross-tenant query surface.
///
/// Sources implemented here:
///  a. Weak positives (backfill): tg_events rows with rule hits, excluding the
///     'whitelisted' pseudo-hit -&gt; INSERT label='fraud', label_source='t1_rule'.
///     API-06 already writes these LIVE too; duplicates are EXPECTED and resolved at
///     training-READ time (LabelResolver), never here.
///  b. Guaranteed positives (synthetic_bot): NOT derived here — already written at
///     ingest by API-04/SDK-06. This step only COUNTS and logs how many fall in the
///     window.
///  c. Negatives (conversion): DEFERRED — no producer exists anywhere in the plan
///     (see API-09, not yet a task). Logged and skipped; never invented.
///  d. Negatives (review_screen backfill): DAT-07 already emits these LIVE via
///     ILabelSink when a review-screen whitelist add carries a SessionId. This step
///     backfills whitelist entries in the window that carried NO session id at write
///     time by joining SQL Server's dbo.WhitelistEntries (Source='review_screen') to
///     ClickHouse tg_events on ip/fingerprint, inserting only sessions that don't
///     already have ANY label row (LEFT ANTI JOIN against tg_labels).
/// </summary>
public sealed class LabelBuilder(string clickHouseConnectionString, string sqlConnectionString, Action<string> log)
{
    /// <summary>SYSTEM sentinel tenant id (DAT-03) — mirrors WellKnownTenants.System.
    /// Allowed by rls.fn_tenantPredicate to enumerate ALL tenants; never a real tenant.</summary>
    private static readonly Guid SystemSentinel = new("00000000-0000-0000-0000-000000000001");

    public async Task<LabelBuildResult> RunAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var tenantIds = await ListActiveTenantIdsAsync(ct).ConfigureAwait(false);
        var perTenant = new List<LabelBuildTenantResult>();

        foreach (var tenantId in tenantIds)
        {
            var t1 = await InsertWeakPositivesAsync(tenantId, from, to, ct).ConfigureAwait(false);
            var synthetic = await CountSyntheticBotAsync(tenantId, from, to, ct).ConfigureAwait(false);
            var review = await BackfillReviewScreenAsync(tenantId, from, to, ct).ConfigureAwait(false);
            perTenant.Add(new LabelBuildTenantResult(tenantId, t1, synthetic, review));
        }

        log("conversion label source deferred — no producer (see API-09)");
        return new LabelBuildResult(perTenant);
    }

    /// <summary>SYSTEM-sentinel-stamped read (background-job pattern, mirrors
    /// TelemetryGuard.Data's ISystemConnectionFactory.OpenSystemAsync) — the ONLY
    /// unscoped-looking query in this class, and it is explicitly allow-listed by
    /// rls.fn_tenantPredicate for exactly this purpose (tenant enumeration). Internal
    /// (not private): the `train` CLI command reuses it to build its tenant loop
    /// without duplicating the sp_set_session_context dance.</summary>
    internal async Task<IReadOnlyList<Guid>> ListActiveTenantIdsAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(sqlConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(
            "EXEC sp_set_session_context @key = N'TenantId', @value = @tid, @read_only = 1",
            new { tid = SystemSentinel }, cancellationToken: ct)).ConfigureAwait(false);
        var rows = await conn.QueryAsync<Guid>(new CommandDefinition(
            "SELECT TenantId FROM dbo.Tenants WHERE Status = 0;", cancellationToken: ct)).ConfigureAwait(false);
        return rows.ToList();
    }

    /// <summary>Step 3a: weak positives. `length(rule_hits) > 0 AND NOT has(rule_hits,
    /// 'whitelisted')` mirrors API-06's own live-write guard exactly (VerdictFinalizer
    /// step 8) so backfill and live writes agree on what counts as a T1 hit.</summary>
    private async Task<int> InsertWeakPositivesAsync(Guid tenantId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var conn = new ClickHouseConnection(clickHouseConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using (var count = conn.CreateCommand())
        {
            count.CommandText =
                """
                SELECT count()
                FROM tg_events
                WHERE tenant_id = {t:UUID} AND kind = 'verdict'
                  AND timestamp >= {f:DateTime64(3,'UTC')} AND timestamp < {to:DateTime64(3,'UTC')}
                  AND length(rule_hits) > 0 AND NOT has(rule_hits, 'whitelisted')
                """;
            AddWindowParams(count, tenantId, from, to);
            var n = Convert.ToInt32(await count.ExecuteScalarAsync(ct).ConfigureAwait(false));
            if (n == 0) return 0;
        }

        await using var insert = conn.CreateCommand();
        insert.CommandText =
            $$"""
            INSERT INTO tg_labels (tenant_id, session_id, label, label_source, created_at)
            SELECT tenant_id, session_id, '{{LabelValues.Fraud}}', '{{LabelSources.T1Rule}}', timestamp
            FROM tg_events
            WHERE tenant_id = {t:UUID} AND kind = 'verdict'
              AND timestamp >= {f:DateTime64(3,'UTC')} AND timestamp < {to:DateTime64(3,'UTC')}
              AND length(rule_hits) > 0 AND NOT has(rule_hits, 'whitelisted')
            """;
        AddWindowParams(insert, tenantId, from, to);
        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var recount = conn.CreateCommand();
        recount.CommandText =
            """
            SELECT count()
            FROM tg_events
            WHERE tenant_id = {t:UUID} AND kind = 'verdict'
              AND timestamp >= {f:DateTime64(3,'UTC')} AND timestamp < {to:DateTime64(3,'UTC')}
              AND length(rule_hits) > 0 AND NOT has(rule_hits, 'whitelisted')
            """;
        AddWindowParams(recount, tenantId, from, to);
        return Convert.ToInt32(await recount.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Step 3b: counts-only — synthetic_bot rows are written LIVE at ingest
    /// (API-04/API-06's TrackerEndpoints/BeaconEndpoints under Synthetic:Enabled); this
    /// NEVER inserts them.</summary>
    private async Task<int> CountSyntheticBotAsync(Guid tenantId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var conn = new ClickHouseConnection(clickHouseConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $$"""
            SELECT count()
            FROM tg_labels
            WHERE tenant_id = {t:UUID} AND label_source = '{{LabelSources.SyntheticBot}}'
              AND created_at >= {f:DateTime64(3,'UTC')} AND created_at < {to:DateTime64(3,'UTC')}
            """;
        AddWindowParams(cmd, tenantId, from, to);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Step 3d: backfills review-screen whitelist adds that carried no
    /// SessionId at write time (so DAT-07's live ILabelSink emission never fired) by
    /// joining SQL Server whitelist entries to ClickHouse sessions on ip/fingerprint,
    /// inserting only sessions with NO existing tg_labels row at all (any source).</summary>
    private async Task<int> BackfillReviewScreenAsync(Guid tenantId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var (ips, fingerprints) = await ReadReviewScreenEntriesAsync(tenantId, from, to, ct).ConfigureAwait(false);
        if (ips.Count == 0 && fingerprints.Count == 0) return 0;

        await using var conn = new ClickHouseConnection(clickHouseConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var inserted = 0;
        if (ips.Count > 0)
        {
            // tg_events.ip is the native ClickHouse IPv6 type — cast the SQL Server
            // whitelist's textual value with toIPv6() before comparing, never the
            // reverse (comparing a native IPv6 column against raw strings).
            inserted += await InsertReviewScreenMatchesAsync(
                conn, tenantId, from, to, "has(arrayMap(x -> toIPv6(x), {values:Array(String)}), ev.ip)", ips, ct)
                .ConfigureAwait(false);
        }
        if (fingerprints.Count > 0)
        {
            inserted += await InsertReviewScreenMatchesAsync(
                conn, tenantId, from, to, "has({values:Array(String)}, ev.fingerprint_visitor_id)", fingerprints, ct)
                .ConfigureAwait(false);
        }
        return inserted;
    }

    private async Task<(List<string> Ips, List<string> Fingerprints)> ReadReviewScreenEntriesAsync(
        Guid tenantId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var conn = new SqlConnection(sqlConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(
            "EXEC sp_set_session_context @key = N'TenantId', @value = @tid, @read_only = 1",
            new { tid = tenantId }, cancellationToken: ct)).ConfigureAwait(false);

        var rows = await conn.QueryAsync<(string SourceType, string Value)>(new CommandDefinition(
            """
            SELECT SourceType, Value FROM dbo.WhitelistEntries
            WHERE TenantId = @TenantId AND Source = 'review_screen'
              AND CreatedUtc >= @From AND CreatedUtc < @To;
            """,
            new
            {
                TenantId = tenantId,
                From = from.ToDateTime(TimeOnly.MinValue),
                To = to.ToDateTime(TimeOnly.MinValue),
            },
            cancellationToken: ct)).ConfigureAwait(false);

        var ips = new List<string>();
        var fingerprints = new List<string>();
        foreach (var (sourceType, value) in rows)
        {
            if (sourceType == "ip" && IPAddress.TryParse(value, out _))
            {
                ips.Add(value); // kept as text — toIPv6() casts it at query time
            }
            else if (sourceType is "fingerprint" or "device_id")
            {
                fingerprints.Add(value);
            }
        }
        return (ips, fingerprints);
    }

    private static async Task<int> InsertReviewScreenMatchesAsync(
        ClickHouseConnection conn, Guid tenantId, DateOnly from, DateOnly to,
        string matchPredicate, IReadOnlyCollection<string> values, CancellationToken ct)
    {
        // LEFT ANTI JOIN: only sessions with NO existing tg_labels row at all (any
        // source) get backfilled — a session already labeled (live t1_rule, live
        // review_screen, or an earlier backfill run) is left untouched.
        await using var insert = conn.CreateCommand();
        insert.CommandText =
            $$"""
            INSERT INTO tg_labels (tenant_id, session_id, label, label_source, created_at)
            SELECT ev.tenant_id, ev.session_id, '{{LabelValues.Legit}}', '{{LabelSources.ReviewScreen}}', ev.timestamp
            FROM tg_events ev
            LEFT ANTI JOIN tg_labels lb
                ON lb.tenant_id = ev.tenant_id AND lb.session_id = ev.session_id
            WHERE ev.tenant_id = {t:UUID} AND ev.kind = 'verdict'
              AND ev.timestamp >= {f:DateTime64(3,'UTC')} AND ev.timestamp < {to:DateTime64(3,'UTC')}
              AND {{matchPredicate}}
            """;
        insert.AddParameter("t", tenantId);
        insert.AddParameter("f", from.ToDateTime(TimeOnly.MinValue));
        insert.AddParameter("to", to.ToDateTime(TimeOnly.MinValue));
        insert.AddParameter("values", values.ToArray());
        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var count = conn.CreateCommand();
        count.CommandText =
            $$"""
            SELECT count()
            FROM tg_events ev
            INNER JOIN tg_labels lb
                ON lb.tenant_id = ev.tenant_id AND lb.session_id = ev.session_id
                AND lb.label_source = '{{LabelSources.ReviewScreen}}'
            WHERE ev.tenant_id = {t:UUID} AND ev.kind = 'verdict'
              AND ev.timestamp >= {f:DateTime64(3,'UTC')} AND ev.timestamp < {to:DateTime64(3,'UTC')}
              AND {{matchPredicate}}
            """;
        count.AddParameter("t", tenantId);
        count.AddParameter("f", from.ToDateTime(TimeOnly.MinValue));
        count.AddParameter("to", to.ToDateTime(TimeOnly.MinValue));
        count.AddParameter("values", values.ToArray());
        return Convert.ToInt32(await count.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    private static void AddWindowParams(ClickHouseCommand cmd, Guid tenantId, DateOnly from, DateOnly to)
    {
        cmd.AddParameter("t", tenantId);
        cmd.AddParameter("f", from.ToDateTime(TimeOnly.MinValue));
        cmd.AddParameter("to", to.ToDateTime(TimeOnly.MinValue));
    }
}
