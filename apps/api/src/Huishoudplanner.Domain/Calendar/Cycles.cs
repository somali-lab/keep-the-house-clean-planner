namespace Huishoudplanner.Domain.Calendar;

/// <summary>
/// The 4-week cycle calendar (port of <c>packages/shared/src/cycle.ts</c>). A cycle is 28 days from a Monday
/// anchor; indexes are negative before the anchor.
/// </summary>
public static class Cycles
{
    public const int CycleDays = 28;

    public const int CycleWeeks = 4;

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> unless the anchor is a Monday.</summary>
    public static void AssertValidAnchor(DateOnly anchor)
    {
        if (!DayKeys.IsMonday(anchor))
        {
            throw new ArgumentOutOfRangeException(nameof(anchor), anchor, "Cycle anchor must be a Monday.");
        }
    }

    /// <summary>Cycle index of a day; negative for days before the anchor.</summary>
    public static int CycleIndexFor(DateOnly date, DateOnly anchor)
    {
        AssertValidAnchor(anchor);
        return FloorDiv(DayKeys.DaysBetween(anchor, date), CycleDays);
    }

    /// <summary>First day (a Monday) of the cycle.</summary>
    public static DateOnly CycleStart(int index, DateOnly anchor)
    {
        AssertValidAnchor(anchor);
        return DayKeys.AddDays(anchor, index * CycleDays);
    }

    /// <summary>Last day (a Sunday) of the cycle.</summary>
    public static DateOnly CycleEnd(int index, DateOnly anchor) =>
        DayKeys.AddDays(CycleStart(index, anchor), CycleDays - 1);

    /// <summary>Week 0..3 within the cycle.</summary>
    public static int WeekIndexFor(DateOnly date, DateOnly anchor)
    {
        AssertValidAnchor(anchor);
        var days = DayKeys.DaysBetween(anchor, date);
        return (days - (FloorDiv(days, CycleDays) * CycleDays)) / 7;
    }

    /// <summary>Concrete date of a template slot.</summary>
    /// <param name="cycleStartDate">First day of the cycle.</param>
    /// <param name="weekIndex">0..3.</param>
    /// <param name="weekday">0=Sunday..6=Saturday; Sunday is the last day of the Monday-first week.</param>
    public static DateOnly SlotDate(DateOnly cycleStartDate, int weekIndex, int weekday)
    {
        if (weekIndex is < 0 or >= CycleWeeks)
        {
            throw new ArgumentOutOfRangeException(nameof(weekIndex), weekIndex, "weekIndex out of range.");
        }

        if (weekday is < 0 or > 6)
        {
            throw new ArgumentOutOfRangeException(nameof(weekday), weekday, "weekday out of range.");
        }

        return DayKeys.AddDays(cycleStartDate, (weekIndex * 7) + DayKeys.Sun0ToMon0(weekday));
    }

    private static int FloorDiv(int n, int m) => (int)Math.Floor(n / (double)m);
}
