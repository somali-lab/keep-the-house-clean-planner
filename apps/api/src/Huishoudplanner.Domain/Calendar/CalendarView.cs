namespace Huishoudplanner.Domain.Calendar;

/// <summary>The days of a range, with the household timezone the day keys are read in.</summary>
public sealed record CalendarView(string Timezone, IReadOnlyList<CalendarDay> Days);
