using Huishoudplanner.Domain.Calendar;

namespace Huishoudplanner.Domain.Bonuses;

public enum PeriodUnit
{
    Week,
    Cycle,
}

/// <summary>A calendar week (Monday to Sunday) or a cycle (28 days from a Monday); identified by its first day.</summary>
public sealed record Period(PeriodUnit Unit, DateOnly Start, DateOnly End)
{
    /// <summary>The calendar week of a day (TS <c>weekOf</c>).</summary>
    public static Period WeekOf(DateOnly day)
    {
        var start = DayKeys.MondayOf(day);
        return new Period(PeriodUnit.Week, start, DayKeys.AddDays(start, 6));
    }

    /// <summary>The cycle of a day, for any day (also before the anchor, where indexes are negative; TS <c>cycleOf</c>).</summary>
    public static Period CycleOf(DateOnly day, DateOnly anchor)
    {
        var index = Cycles.CycleIndexFor(day, anchor);
        return new Period(PeriodUnit.Cycle, Cycles.CycleStart(index, anchor), Cycles.CycleEnd(index, anchor));
    }

    /// <summary>A period has ended when its last day is before today; a period ending today has not ended (TS <c>periodEnded</c>).</summary>
    public bool HasEnded(DateOnly today) => End < today;

    /// <summary>
    /// The on-time cut-off: local midnight after the last day, in the household timezone. A DST week is therefore 167 or
    /// 169 hours long and Sunday 23:59 local time is always on time (TS <c>onTimeCutoff</c>).
    /// </summary>
    public DateTimeOffset OnTimeCutoff(TimeZoneInfo timezone) => DayKeys.FromDayKey(DayKeys.AddDays(End, 1), timezone);
}
