using System.Globalization;
using Huishoudplanner.Domain.Settings;

namespace Huishoudplanner.Domain.Bonuses;

/// <summary>The evaluation of one person's set for one period (TS <c>SetEvaluation</c>).</summary>
/// <param name="Total">Occurrences in the set, recorded work included.</param>
/// <param name="Planned">Occurrences that were planned; recorded work does not count here.</param>
/// <param name="Done">Done occurrences, recorded work included.</param>
/// <param name="Open">Planned occurrences still open.</param>
/// <param name="Skipped">Planned occurrences that were skipped.</param>
/// <param name="Eligible">At least one planned occurrence is needed to earn anything.</param>
/// <param name="AllDone">Every occurrence in the set is done, whenever it was completed.</param>
/// <param name="AllOnTime">
/// Everything is done and every completion lies before the cut-off; planned done work without a completion instant is late.
/// Recorded work is always on time.
/// </param>
public sealed record SetEvaluation(int Total, int Planned, int Done, int Open, int Skipped, bool Eligible, bool AllDone, bool AllOnTime);

/// <summary>One bonus that must exist in the ledger.</summary>
/// <param name="Key"><c>&lt;ledger kind&gt;:&lt;personId&gt;:&lt;periodStart&gt;</c>; a cycle is keyed by its first day, not its index.</param>
/// <param name="Kind">The bonus kind.</param>
/// <param name="PersonId">The person who earns it.</param>
/// <param name="Amount">Points, above 0.</param>
/// <param name="PeriodStart">The first day of the period.</param>
/// <param name="PeriodEnd">The last day of the period; the bonus is dated then.</param>
public sealed record ExpectedBonusEntry(string Key, BonusKind Kind, string PersonId, int Amount, DateOnly PeriodStart, DateOnly PeriodEnd);

/// <summary>What the bonus rules need besides the occurrences.</summary>
/// <param name="Anchor">The cycle anchor (a Monday).</param>
/// <param name="Timezone">The household timezone; it decides the on-time cut-off.</param>
/// <param name="Today">Today in the household timezone.</param>
/// <param name="Schedule">The bonus amounts over time.</param>
/// <param name="Floor">
/// The boundary of the last statistics reset: a period that starts before this day is never evaluated, because its
/// history was (partly) purged and what remains cannot be trusted.
/// </param>
public sealed record BonusContext(DateOnly Anchor, TimeZoneInfo Timezone, DateOnly Today, IReadOnlyList<BonusScheduleRow> Schedule, DateOnly? Floor = null);

/// <summary>Port of the period, set and entry rules of <c>packages/shared/src/bonuses.ts</c> (ADR-0012). The schedule rules are <see cref="BonusSchedule"/>.</summary>
public static class BonusCalculator
{
    /// <summary><c>&lt;ledger kind&gt;:&lt;personId&gt;:&lt;periodStart&gt;</c> (TS <c>bonusKey</c>).</summary>
    public static string BonusKey(BonusKind kind, string personId, DateOnly periodStart) =>
        $"{kind.ToLedgerKind()}:{personId}:{Format(periodStart)}";

    /// <summary>
    /// Where an occurrence counts (TS <c>placementsOf</c>). Recorded work belongs to the credited person and never blocks.
    /// Planned work belongs to its period owner. Open and skipped work is the owner's open item; unassigned work is in
    /// nobody's set. Done work is the owner's done item when the owner is the credited person. When somebody else did it,
    /// it is not done for the owner (it still blocks), and for the person who did it it is non-blocking, like recorded
    /// work: never needed, never late.
    /// </summary>
    public static IReadOnlyList<Placement> PlacementsOf(BonusOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        var credited = occurrence.CreditedId;
        if (occurrence.RecordedDone)
        {
            return credited is null ? [] : [new Placement(credited, occurrence)];
        }

        var owner = occurrence.PeriodOwnerId;
        if (occurrence.Status != OccurrenceStatus.Done)
        {
            return owner is null ? [] : [new Placement(owner, occurrence)];
        }

        if (owner is not null && string.Equals(credited, owner, StringComparison.Ordinal))
        {
            return [new Placement(owner, occurrence)];
        }

        var placements = new List<Placement>();
        if (owner is not null)
        {
            placements.Add(new Placement(owner, occurrence with { Status = OccurrenceStatus.Open, CompletedAt = null, CompletedBy = null }));
        }

        if (credited is not null)
        {
            placements.Add(new Placement(credited, occurrence with { RecordedDone = true, Date = occurrence.PlannedDate }));
        }

        return placements;
    }

    /// <summary>Evaluates the set of one person for one period against the on-time cut-off (TS <c>evaluateSet</c>); instants compare in whole milliseconds like JavaScript.</summary>
    public static SetEvaluation EvaluateSet(IReadOnlyList<BonusOccurrence> set, DateTimeOffset cutoff)
    {
        ArgumentNullException.ThrowIfNull(set);
        int planned = 0, done = 0, open = 0, skipped = 0, onTime = 0;
        foreach (var occurrence in set)
        {
            if (occurrence.RecordedDone)
            {
                // Recorded work never blocks and is always on time, whatever its completion instant says
                // (an administrator may have corrected the date to before it).
                done++;
                onTime++;
                continue;
            }

            planned++;
            if (occurrence.Status == OccurrenceStatus.Done)
            {
                done++;
                if (occurrence.CompletedAt is { } completedAt && completedAt.ToUnixTimeMilliseconds() < cutoff.ToUnixTimeMilliseconds())
                {
                    onTime++;
                }
            }
            else if (occurrence.Status == OccurrenceStatus.Skipped)
            {
                skipped++;
            }
            else
            {
                open++;
            }
        }

        var total = set.Count;
        var allDone = total > 0 && done == total;
        return new SetEvaluation(total, planned, done, open, skipped, planned > 0, allDone, allDone && onTime == total);
    }

    /// <summary>
    /// The bonus entries that must exist (TS <c>expectedBonusEntries</c>): for every person and every ended week and cycle
    /// that holds at least one of their occurrences, one entry per kind whose amount is above 0, whose set is eligible and
    /// whose condition holds. Ordered by period end, then key (ordinal), so the result is deterministic. Occurrences
    /// without an owner are in nobody's set.
    /// </summary>
    public static IReadOnlyList<ExpectedBonusEntry> ExpectedEntries(IReadOnlyList<BonusOccurrence> items, BonusContext context)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(context);
        var groups = new Dictionary<string, (string Person, Period Period, List<BonusOccurrence> Set)>(StringComparer.Ordinal);
        void Add(string person, Period period, BonusOccurrence item)
        {
            var id = $"{period.Unit}:{person}:{Format(period.Start)}";
            if (groups.TryGetValue(id, out var group))
            {
                group.Set.Add(item);
            }
            else
            {
                groups[id] = (person, period, [item]);
            }
        }

        foreach (var occurrence in items)
        {
            foreach (var (person, item) in PlacementsOf(occurrence))
            {
                var day = item.PeriodDay;
                Add(person, Period.WeekOf(day), item);
                Add(person, Period.CycleOf(day, context.Anchor), item);
            }
        }

        var entries = new List<ExpectedBonusEntry>();
        foreach (var (person, period, set) in groups.Values)
        {
            if (!period.HasEnded(context.Today))
            {
                continue;
            }

            if (context.Floor is { } floor && period.Start < floor)
            {
                continue;
            }

            var evaluation = EvaluateSet(set, period.OnTimeCutoff(context.Timezone));
            if (!evaluation.Eligible)
            {
                continue;
            }

            var amounts = BonusSchedule.AmountsOn(context.Schedule, period.End);
            BonusKind[] kinds = period.Unit == PeriodUnit.Week
                ? [BonusKind.WeekDone, BonusKind.WeekOnTime]
                : [BonusKind.CycleDone, BonusKind.CycleOnTime];
            foreach (var kind in kinds)
            {
                var amount = amounts.AmountOf(kind);
                var earned = kind.IsOnTimeKind() ? evaluation.AllOnTime : evaluation.AllDone;
                if (amount > 0 && earned)
                {
                    entries.Add(new ExpectedBonusEntry(BonusKey(kind, person, period.Start), kind, person, amount, period.Start, period.End));
                }
            }
        }

        return [.. entries.OrderBy(e => e.PeriodEnd).ThenBy(e => e.Key, StringComparer.Ordinal)];
    }

    private static string Format(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
