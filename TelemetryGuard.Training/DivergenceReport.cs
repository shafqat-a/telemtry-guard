using ClickHouse.Client.ADO;
using ClickHouse.Client.Utility;

namespace TelemetryGuard.Training;

/// <summary>One comparable session: the ENFORCED score (heuristic + T1 floor already
/// folded in) vs the shadow model's RAW score, plus its resolved label when known.
/// Only rows with NO rule hits (never floored) are comparable — see
/// <see cref="DivergenceReport"/>'s class doc.</summary>
public sealed record DivergenceRow(string SessionId, int EnforcedScore, int ShadowScore, bool? Fraud);

public sealed record DivergenceSummary(
    string ShadowScorerVersion,
    int Sessions,                 // comparable sessions (no rule floor applied — see below)
    int RuleFlooredSessions,      // excluded from the band comparison, reported for honesty
    int LabeledSessions,
    double BandAgreementRate,
    IReadOnlyList<int> BandMatrix, // 9 counts, row = enforced band, col = shadow band (allow/challenge/block)
    double MeanAbsDelta,
    int ShadowWouldBlockMore,
    int ShadowWouldBlockFewer,
    double EnforcedAuc,           // NaN when the labeled subset has only one class — never 0
    double ShadowAuc);

/// <summary>
/// P2-02 (D18 listen-only): compares a shadow model's raw scores against the
/// heuristic's ENFORCED scores on real tg_events rows. `tg_events.score` already has
/// the T1 floor folded in (Math.Max, §6.3 "rules only raise"), while `shadow_score` is
/// the raw model output with no floor — so rows carrying ANY rule hit are excluded
/// from the comparison (there is no way to recover "what the heuristic alone would
/// have scored" once the floor is folded in) and reported separately as
/// <see cref="DivergenceSummary.RuleFlooredSessions"/> instead of silently dropped.
/// This is a listen-only REPORT: it changes nothing about enforcement, the band, or
/// the exclusion queue (D18 "listen-only means listen-only").
/// </summary>
public static class DivergenceReport
{
    /// <summary>Pure aggregation over already-loaded rows — no I/O. Used directly by
    /// unit tests; <see cref="RunAsync"/> is the DB-backed orchestration the CLI uses.</summary>
    public static DivergenceSummary Compute(
        string shadowScorerVersion, IReadOnlyList<DivergenceRow> rows, int ruleFlooredSessions,
        int allowMax, int challengeMax)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var bandMatrix = new int[9];
        var agreement = 0;
        double deltaSum = 0;
        var shadowMore = 0;
        var shadowFewer = 0;

        foreach (var row in rows)
        {
            var enforcedBand = BandOf(row.EnforcedScore, allowMax, challengeMax);
            var shadowBand = BandOf(row.ShadowScore, allowMax, challengeMax);
            bandMatrix[(enforcedBand * 3) + shadowBand]++;
            if (enforcedBand == shadowBand) agreement++;
            deltaSum += Math.Abs(row.ShadowScore - row.EnforcedScore);
            if (shadowBand > enforcedBand) shadowMore++;
            else if (shadowBand < enforcedBand) shadowFewer++;
        }

        var labeled = rows.Where(r => r.Fraud is not null).ToList();
        var enforcedAuc = RankAuc(labeled.Select(r => ((double)r.EnforcedScore, r.Fraud!.Value)).ToList());
        var shadowAuc = RankAuc(labeled.Select(r => ((double)r.ShadowScore, r.Fraud!.Value)).ToList());

        return new DivergenceSummary(
            ShadowScorerVersion: shadowScorerVersion,
            Sessions: rows.Count,
            RuleFlooredSessions: ruleFlooredSessions,
            LabeledSessions: labeled.Count,
            BandAgreementRate: rows.Count == 0 ? double.NaN : (double)agreement / rows.Count,
            BandMatrix: bandMatrix,
            MeanAbsDelta: rows.Count == 0 ? double.NaN : deltaSum / rows.Count,
            ShadowWouldBlockMore: shadowMore,
            ShadowWouldBlockFewer: shadowFewer,
            EnforcedAuc: enforcedAuc,
            ShadowAuc: shadowAuc);
    }

    /// <summary>Rank (Mann-Whitney) AUC with averaged ranks for ties. Returns NaN when
    /// either class is absent — missing ≠ zero (§7). PUBLIC (not internal, despite the
    /// P2-02 task sketch): TelemetryGuard.Training.csproj deliberately carries no
    /// InternalsVisibleTo (see RetrainRunner's class doc — it would collide with
    /// TelemetryGuard.Api's WebApplicationFactory&lt;Program&gt;, CS0433), so
    /// DivergenceReportTests (Tests.Unit) needs this to be public to exercise it
    /// directly instead of only indirectly through <see cref="Compute"/>.</summary>
    public static double RankAuc(IReadOnlyList<(double Score, bool Positive)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var positives = rows.Count(r => r.Positive);
        var negatives = rows.Count - positives;
        if (positives == 0 || negatives == 0)
        {
            return double.NaN;
        }

        var ordered = rows
            .Select((r, i) => (r.Score, r.Positive, Index: i))
            .OrderBy(r => r.Score)
            .ToList();

        var ranks = new double[rows.Count];
        var i0 = 0;
        while (i0 < ordered.Count)
        {
            var j0 = i0;
            while (j0 + 1 < ordered.Count && ordered[j0 + 1].Score == ordered[i0].Score) j0++;
            // 1-based average rank across the tied group [i0..j0].
            var avgRank = (i0 + 1 + j0 + 1) / 2.0;
            for (var k = i0; k <= j0; k++) ranks[ordered[k].Index] = avgRank;
            i0 = j0 + 1;
        }

        double positiveRankSum = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Positive) positiveRankSum += ranks[i];
        }

        return (positiveRankSum - (positives * (positives + 1) / 2.0)) / ((double)positives * negatives);
    }

    private static int BandOf(int score, int allowMax, int challengeMax)
        => score <= allowMax ? 0 : score <= challengeMax ? 1 : 2;

    /// <summary>
    /// Full listen-only divergence procedure (D18): per tenant (D11), reads tg_events
    /// verdict rows carrying <paramref name="shadowScorerVersion"/> within
    /// [<paramref name="from"/>, <paramref name="to"/>), excludes whitelisted sessions,
    /// and joins the labeled subset via the same tg_labels read +
    /// <see cref="LabelResolver.Resolve"/> the trainer uses — never re-implemented.
    /// Used by both the `divergence` CLI command and `promote --status active`'s
    /// GateJson snapshot.
    /// </summary>
    public static async Task<DivergenceSummary> RunAsync(
        string clickHouseConnectionString,
        IReadOnlyList<Guid> tenantIds,
        string shadowScorerVersion,
        DateOnly from,
        DateOnly to,
        float t1PositiveWeight,
        int allowMax,
        int challengeMax,
        CancellationToken ct = default)
    {
        var comparableRows = new List<DivergenceRow>();
        var ruleFloored = 0;

        foreach (var tenantId in tenantIds)
        {
            var labelRows = await ReadLabelsAsync(clickHouseConnectionString, tenantId, from, to, ct)
                .ConfigureAwait(false);
            var resolved = LabelResolver.Resolve(labelRows, t1PositiveWeight).Resolved
                .ToDictionary(r => r.SessionId, r => r.Fraud, StringComparer.Ordinal);

            var (comparable, floored) = await ReadEventScoresAsync(
                clickHouseConnectionString, tenantId, from, to, shadowScorerVersion, ct).ConfigureAwait(false);
            ruleFloored += floored;

            foreach (var (sessionId, enforcedScore, shadowScore) in comparable)
            {
                bool? fraud = resolved.TryGetValue(sessionId, out var f) ? f : null;
                comparableRows.Add(new DivergenceRow(sessionId, enforcedScore, shadowScore, fraud));
            }
        }

        return Compute(shadowScorerVersion, comparableRows, ruleFloored, allowMax, challengeMax);
    }

    // ---- ClickHouse reads (D11: tenant_id filters every query — never unscoped) ----
    // Mirrors Trainer's private ReadLabelsAsync exactly (same SQL); duplicated rather
    // than shared because Trainer's is private and this ~10-line read is not worth
    // widening Trainer's surface for.

    private static async Task<List<RawLabelRow>> ReadLabelsAsync(
        string connectionString, Guid tenantId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var conn = new ClickHouseConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT session_id, label, label_source, weight, created_at
            FROM tg_labels
            WHERE tenant_id = {t:UUID}
              AND created_at >= {f:DateTime64(3,'UTC')} AND created_at < {to:DateTime64(3,'UTC')}
            """;
        cmd.AddParameter("t", tenantId);
        cmd.AddParameter("f", from.ToDateTime(TimeOnly.MinValue));
        cmd.AddParameter("to", to.ToDateTime(TimeOnly.MinValue));

        var result = new List<RawLabelRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new RawLabelRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetFloat(3), reader.GetDateTime(4)));
        }
        return result;
    }

    /// <summary>tg_events.score is the ENFORCED score (T1 floor already folded in);
    /// shadow_score is the raw model output. `floored` (length(rule_hits)) &gt; 0 rows
    /// are split out and counted, never compared (the floor cannot be un-applied).</summary>
    private static async Task<(List<(string SessionId, int Score, int ShadowScore)> Comparable, int Floored)>
        ReadEventScoresAsync(
            string connectionString, Guid tenantId, DateOnly from, DateOnly to,
            string shadowScorerVersion, CancellationToken ct)
    {
        await using var conn = new ClickHouseConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT session_id, score, shadow_score, length(rule_hits) AS floored
            FROM tg_events
            WHERE tenant_id = {t:UUID}
              AND kind = 'verdict'
              AND timestamp >= {f:DateTime64(3,'UTC')} AND timestamp < {to:DateTime64(3,'UTC')}
              AND shadow_scorer_version = {sv:String}
              AND shadow_score IS NOT NULL AND score IS NOT NULL
              AND NOT has(rule_hits, 'whitelisted')
            """;
        cmd.AddParameter("t", tenantId);
        cmd.AddParameter("f", from.ToDateTime(TimeOnly.MinValue));
        cmd.AddParameter("to", to.ToDateTime(TimeOnly.MinValue));
        cmd.AddParameter("sv", shadowScorerVersion);

        var comparable = new List<(string, int, int)>();
        var floored = 0;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var sessionId = reader.GetString(0);
            var score = Convert.ToInt32(reader.GetValue(1));
            var shadowScore = Convert.ToInt32(reader.GetValue(2));
            var floorCount = Convert.ToInt32(reader.GetValue(3));
            if (floorCount > 0)
            {
                floored++;
                continue;
            }
            comparable.Add((sessionId, score, shadowScore));
        }
        return (comparable, floored);
    }
}
