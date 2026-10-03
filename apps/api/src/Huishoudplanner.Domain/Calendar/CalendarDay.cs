namespace Huishoudplanner.Domain.Calendar;

/// <summary>
/// What the calendar knows about one day. <c>Weekday</c> is 0=Sunday..6=Saturday (the weekday of the plan slots and of
/// <see cref="DayKeys.WeekdaySun0"/>); <c>WeekIndex</c> is 0..3 inside the cycle; <c>WeekStart</c> is the Monday of the day's week.
/// </summary>
public sealed record CalendarDay(
    DateOnly DayKey,
    int Weekday,
    int CycleIndex,
    int WeekIndex,
    string IsoWeek,
    DateOnly WeekStart)
{
    /// <summary>Projects a day through the ported day key and cycle functions; there is no date math of its own here.</summary>
    public static CalendarDay Of(DateOnly day, DateOnly anchor) => new(
        day,
        DayKeys.WeekdaySun0(day),
        Cycles.CycleIndexFor(day, anchor),
        Cycles.WeekIndexFor(day, anchor),
        DayKeys.IsoWeekLabel(day),
        DayKeys.MondayOf(day));
}
