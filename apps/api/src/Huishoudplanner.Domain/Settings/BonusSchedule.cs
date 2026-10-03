namespace Huishoudplanner.Domain.Settings;

/// <summary>The four amounts of the week and cycle bonuses (ADR-0012): integers from 0 to 1000; 0 disables that kind.</summary>
public sealed record BonusAmounts(int WeekDone, int WeekOnTime, int CycleDone, int CycleOnTime)
{
    public static BonusAmounts None { get; } = new(0, 0, 0, 0);
}

/// <summary>Amounts that apply to every period whose last day is on or after <paramref name="From"/>, until the next row.</summary>
public sealed record BonusScheduleRow(DateOnly From, BonusAmounts Amounts);

/// <summary>Port of the schedule rules of <c>packages/shared/src/bonuses.ts</c> (the period and set rules follow with slice 4.2).</summary>
public static class BonusSchedule
{
    public const int MinAmount = 0;

    public const int MaxAmount = 1000;

    /// <summary>The amounts in force for a period ending on <paramref name="day"/>: the last row on or before it; all 0 before the first row.</summary>
    public static BonusAmounts AmountsOn(IReadOnlyList<BonusScheduleRow> schedule, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        BonusScheduleRow? found = null;
        foreach (var row in schedule)
        {
            if (row.From <= day && (found is null || row.From > found.From))
            {
                found = row;
            }
        }

        return found?.Amounts ?? BonusAmounts.None;
    }

    public static bool SameAmounts(BonusAmounts a, BonusAmounts b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a == b;
    }

    /// <summary>
    /// The schedule after an administrator sets <paramref name="amounts"/> today: unchanged when they equal the row in force
    /// today, otherwise a row from today that replaces a row that already starts today. A change never alters an ended
    /// period, because a period ends after its last day (ADR-0012).
    /// </summary>
    public static IReadOnlyList<BonusScheduleRow> WithAmounts(IReadOnlyList<BonusScheduleRow> schedule, BonusAmounts amounts, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(amounts);
        if (SameAmounts(AmountsOn(schedule, today), amounts))
        {
            return [.. schedule];
        }

        return
        [
            .. schedule.Where(row => row.From != today).Append(new BonusScheduleRow(today, amounts)).OrderBy(row => row.From),
        ];
    }
}
