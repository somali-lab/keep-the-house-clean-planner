using Huishoudplanner.Domain.Bonuses;
using Huishoudplanner.Domain.Calendar;

namespace Huishoudplanner.Domain.Points;

/// <summary>
/// The differences between the stored execution entries and the done occurrences (<see cref="Changes"/>), what they mean as
/// <see cref="Corrections"/>, the done occurrences nobody can be credited for and the ones that could not be read.
/// </summary>
public sealed record ReconciliationPlan(
    PointEntryChanges Changes,
    IReadOnlyList<PointsCorrection> Corrections,
    int Unattributed,
    IReadOnlySet<string> SkippedIds);

/// <summary>The pure part of the reconciliation (ADR-0011, steps 2 and 3 of <c>reconcileNow</c>).</summary>
public static class PointsReconciliation
{
    /// <summary>
    /// Computes the expected entry of every done occurrence and the inserts, updates and deletes that make the stored execution entries match:
    /// it inserts what is missing, updates what differs and deletes the entries without an occurrence. An occurrence that cannot be read is skipped
    /// and counted, and its stored entry stays as it is. An occurrence whose stored entry cannot be read (<paramref name="unreadableKeys"/>) is skipped and counted the same way. Planning again after the result was applied gives an empty plan (idempotent).
    /// </summary>
    public static ReconciliationPlan Plan(IReadOnlyList<PointEntry> stored, IEnumerable<ExecutionSource> done, TimeZoneInfo zone, IReadOnlySet<string>? unreadableKeys = null)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(done);
        ArgumentNullException.ThrowIfNull(zone);
        var expected = new Dictionary<string, ExecutionEntryFields>(StringComparer.Ordinal);
        var unreadable = new HashSet<string>(StringComparer.Ordinal);
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        var unattributed = 0;
        foreach (var source in done)
        {
            var expectation = ExecutionPoints.Expect(source, zone);
            if (expectation.Unreadable)
            {
                unreadable.Add(ExecutionPoints.Key(source.Id));
                skipped.Add(source.Id);
            }
            else if (expectation.Fields is not null && unreadableKeys?.Contains(ExecutionPoints.Key(source.Id)) == true)
            {
                // Its stored entry cannot be read: leave both alone, or the insert would hit the unique key on every run.
                skipped.Add(source.Id);
            }
            else if (expectation.Fields is { } fields)
            {
                expected[ExecutionPoints.Key(source.Id)] = fields;
            }
            else if (expectation.Unattributed)
            {
                unattributed++;
            }
        }

        var storedByKey = stored.ToDictionary(e => e.Key, StringComparer.Ordinal);
        var inserts = new List<PointEntryInsert>();
        var updates = new List<PointEntryUpdate>();
        var deletes = new List<PointEntry>();
        var corrections = new List<PointsCorrection>();
        foreach (var (key, fields) in expected.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (!storedByKey.TryGetValue(key, out var current))
            {
                inserts.Add(new PointEntryInsert(key, fields));
            }
            else if (!current.Matches(fields))
            {
                updates.Add(new PointEntryUpdate(current, fields));
                corrections.Add(new PointsCorrection(key, new PointsHolding(current.PersonId, current.Amount), new PointsHolding(fields.PersonId, fields.Amount)));
            }
        }

        foreach (var current in stored.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (expected.ContainsKey(current.Key) || unreadable.Contains(current.Key) || unreadableKeys?.Contains(current.Key) == true)
            {
                continue;
            }

            deletes.Add(current);
            corrections.Add(new PointsCorrection(current.Key, new PointsHolding(current.PersonId, current.Amount), null));
        }

        return new ReconciliationPlan(new PointEntryChanges(inserts, updates, deletes), corrections, unattributed, skipped);
    }

    /// <summary>
    /// Step 4 of the reconciliation (ADR-0012): the week and cycle bonuses. They are a pure function of the occurrences, the anchor, the timezone,
    /// <paramref name="today"/> and the bonus schedule, and are only inserted or deleted, never updated. An occurrence that cannot be read is
    /// skipped and counted in <see cref="BonusPlan.SkippedIds"/>; the bonus entries of every person it could count for are left as they are in this run, because their
    /// sets cannot be evaluated. A stored bonus is kept when its person, amount, date and period still match; otherwise it is deleted and, when
    /// still expected, inserted again. Planning again after the result was applied gives an empty plan (idempotent). The anchor must be a Monday.
    /// </summary>
    public static BonusPlan PlanBonuses(IReadOnlyList<PointEntry> storedBonuses, IEnumerable<BonusSource> sources, BonusSettings settings, TimeZoneInfo zone, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(storedBonuses);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(zone);
        var items = new List<BonusOccurrence>();
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (source.ToOccurrence(zone) is { } occurrence)
            {
                items.Add(occurrence);
            }
            else
            {
                skipped.Add(source.Id);
                blocked.UnionWith(source.People);
            }
        }

        var expected = BonusCalculator
            .ExpectedEntries(items, new BonusContext(settings.Anchor, zone, today, settings.Schedule, settings.Floor))
            .Where(entry => !blocked.Contains(entry.PersonId))
            .ToDictionary(entry => entry.Key, entry => ToInsert(entry, zone), StringComparer.Ordinal);

        var storedByKey = storedBonuses.ToDictionary(e => e.Key, StringComparer.Ordinal);
        var deletes = new List<PointEntry>();
        foreach (var (key, current) in storedByKey)
        {
            if (blocked.Contains(current.PersonId))
            {
                continue;
            }

            if (!expected.TryGetValue(key, out var want) || !SameBonus(current, want))
            {
                deletes.Add(current);
            }
        }

        var inserts = new List<BonusEntryInsert>();
        foreach (var (key, want) in expected)
        {
            if (!storedByKey.TryGetValue(key, out var current) || !SameBonus(current, want))
            {
                inserts.Add(want);
            }
        }

        return new BonusPlan(new BonusEntryChanges([.. inserts.OrderBy(i => i.Key, StringComparer.Ordinal)], [.. deletes.OrderBy(d => d.Key, StringComparer.Ordinal)]), skipped);
    }

    /// <summary>
    /// What a bonus step did: only the writes that happened are counted and listed (a delete that missed its compare-and-set and an insert that hit
    /// a duplicate key are not). The list is the removed ones first, then the created ones, each by key, cut at <see cref="PointsRecomputeResult.MaxCorrections"/>.
    /// </summary>
    public static BonusReport Describe(BonusPlan plan, AppliedBonusChanges applied)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(applied);
        var deletes = plan.Changes.Deletes.ToDictionary(d => d.Key, StringComparer.Ordinal);
        var inserts = plan.Changes.Inserts.ToDictionary(i => i.Key, StringComparer.Ordinal);
        var removed = applied.Removed.Where(deletes.ContainsKey).Order(StringComparer.Ordinal)
            .Select(key => new PointsBonusChange(key, deletes[key].PersonId, deletes[key].Amount, "removed")).ToList();
        var created = applied.Created.Where(inserts.ContainsKey).Order(StringComparer.Ordinal)
            .Select(key => new PointsBonusChange(key, inserts[key].PersonId, inserts[key].Amount, "created")).ToList();
        var log = removed.Concat(created).ToList();
        return new BonusReport(created.Count, removed.Count, [.. log.Take(PointsRecomputeResult.MaxCorrections)], log.Count, log.Count > PointsRecomputeResult.MaxCorrections);
    }

    private static BonusEntryInsert ToInsert(ExpectedBonusEntry entry, TimeZoneInfo zone) => new(
        entry.Key,
        entry.Kind switch
        {
            BonusKind.WeekDone => PointEntryKind.BonusWeekDone,
            BonusKind.WeekOnTime => PointEntryKind.BonusWeekOnTime,
            BonusKind.CycleDone => PointEntryKind.BonusCycleDone,
            _ => PointEntryKind.BonusCycleOnTime,
        },
        entry.PersonId,
        entry.Amount,
        DayKeys.FromDayKey(entry.PeriodEnd, zone),
        DayKeys.FromDayKey(DayKeys.MondayOf(entry.PeriodEnd), zone),
        DayKeys.FromDayKey(entry.PeriodStart, zone));

    private static bool SameBonus(PointEntry stored, BonusEntryInsert want) =>
        stored.PersonId == want.PersonId && stored.Amount == want.Amount && stored.Date == want.Date && stored.PeriodStart == want.PeriodStart;

    /// <summary>The first <see cref="PointsRecomputeResult.MaxCorrections"/> corrections, with the total and whether the list was cut.</summary>
    public static (IReadOnlyList<PointsCorrection> Listed, int Total, bool Truncated) LimitCorrections(IReadOnlyList<PointsCorrection> corrections)
    {
        ArgumentNullException.ThrowIfNull(corrections);
        return (
            [.. corrections.Take(PointsRecomputeResult.MaxCorrections)],
            corrections.Count,
            corrections.Count > PointsRecomputeResult.MaxCorrections);
    }
}
