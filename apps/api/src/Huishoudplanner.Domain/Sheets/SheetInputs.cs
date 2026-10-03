using Huishoudplanner.Domain.Due;

namespace Huishoudplanner.Domain.Sheets;

/// <summary>A planned occurrence on a sheet; status is deliberately absent, the sheet is a blank checklist. A null assignee means anyone.</summary>
public sealed record SheetLineInput(string Name, string? Room, string? Assignee, int Minutes);

/// <summary>The planned lines of one calendar day, in any order.</summary>
public sealed record SheetDayInput(DateOnly Day, IReadOnlyList<SheetLineInput> Lines);

/// <summary>A generated week, Monday first. <c>WeekNumberInCycle</c> is 1..4; an empty theme means none.</summary>
public sealed record WeekSheetInput(DateOnly From, DateOnly To, int WeekNumberInCycle, string Theme, IReadOnlyList<SheetDayInput> Days);

/// <summary>Options of the week schedule. <c>GeneratedAt</c> is printed in <c>Zone</c>.</summary>
public sealed record ScheduleOptions(
    SheetOrientation Orientation,
    bool Totals,
    SheetLanguage Language,
    DateTimeOffset GeneratedAt,
    TimeZoneInfo Zone);

/// <summary>A task that is due or overdue. <c>Room</c> is null for a task without one; <c>NextDate</c> is the next planned day, if any.</summary>
public sealed record DueRowInput(string Name, string? Room, string Interval, int DaysSince, DueState State, DateOnly? NextDate);

/// <summary>A task for the overview list.</summary>
public sealed record TaskListRowInput(string Name, string Room, string Interval, int DurationMinutes, bool Active);
