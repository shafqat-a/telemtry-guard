using TelemetryGuard.Analytics.Abstractions;

namespace TelemetryGuard.Training;

/// <summary>One raw tg_labels row, as read for a single tenant/window (step 4).</summary>
public sealed record RawLabelRow(string SessionId, string Label, string LabelSource, float Weight, DateTime CreatedAtUtc);

/// <summary>One session's resolved label, ready to join against tg_events.</summary>
public sealed record ResolvedLabel(string SessionId, bool Fraud, float Weight);

public sealed record LabelResolveResult(IReadOnlyList<ResolvedLabel> Resolved, int DroppedConflicts);

/// <summary>
/// RSK-08 step 4: tg_labels is ANA-02's plain MergeTree — nothing dedupes on disk, and
/// API-06/DAT-07 write LIVE labels while this LabelBuilder ALSO backfills, so
/// duplicates are EXPECTED (task's explicit note). Conflict/duplicate resolution is a
/// READ-TIME concern, done here in pure C# (not SQL) so it stays independently
/// testable: guaranteed sources (synthetic_bot/review_screen/conversion) beat
/// t1_rule; a session with two DIFFERENT guaranteed labels is dropped and counted
/// (contradictory ground truth — never guessed at). Duplicate rows from the SAME
/// source (e.g. a session finalized once by /decide and once by the grace worker,
/// or a t1_rule row written live AND re-derived by a LabelBuilder backfill) collapse
/// to the most-recently-created row for that session.
/// </summary>
public static class LabelResolver
{
    private static readonly HashSet<string> GuaranteedSources = new(StringComparer.Ordinal)
    {
        LabelSources.SyntheticBot, LabelSources.ReviewScreen, LabelSources.Conversion,
    };

    public static LabelResolveResult Resolve(IEnumerable<RawLabelRow> rows, float t1PositiveWeight)
    {
        var resolved = new List<ResolvedLabel>();
        var dropped = 0;

        foreach (var group in rows.GroupBy(r => r.SessionId, StringComparer.Ordinal))
        {
            var guaranteed = group.Where(r => GuaranteedSources.Contains(r.LabelSource)).ToList();
            if (guaranteed.Count > 0)
            {
                var distinctLabels = guaranteed.Select(r => r.Label).Distinct(StringComparer.Ordinal).ToList();
                if (distinctLabels.Count > 1)
                {
                    // Contradictory guaranteed ground truth (e.g. synthetic_bot=fraud AND
                    // review_screen=legit for the same session) — drop, never guess.
                    dropped++;
                    continue;
                }

                var winner = guaranteed.OrderByDescending(r => r.CreatedAtUtc).First();
                resolved.Add(new ResolvedLabel(
                    group.Key, winner.Label == LabelValues.Fraud, winner.Weight));
            }
            else
            {
                // t1_rule-only session: dedupe live-write + backfill duplicates by
                // taking the newest row; effective weight is ALWAYS
                // Training:T1PositiveWeight at read time (the stored `weight` column
                // defaults to 1 for live writes — see 0002_training_and_shadow.sql).
                var winner = group.OrderByDescending(r => r.CreatedAtUtc).First();
                resolved.Add(new ResolvedLabel(
                    group.Key, winner.Label == LabelValues.Fraud, t1PositiveWeight));
            }
        }

        return new LabelResolveResult(resolved, dropped);
    }
}
