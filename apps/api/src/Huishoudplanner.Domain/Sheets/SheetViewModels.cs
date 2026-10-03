namespace Huishoudplanner.Domain.Sheets;

// View models of the four sheets. Every string is final display text and every grouping, order and empty state is
// already decided, so a renderer only draws (plan section 3.10). The builders live in SheetBuilder.

/// <summary>Footer of a page: when the sheet was generated and the one-line note (requirements 6.2, 6.3).</summary>
public sealed record SheetFooter(string GeneratedLabel, string Note);

/// <summary>One checklist line: a task with its room. The checkbox is drawn by the renderer.</summary>
public sealed record SheetLineView(string Name, string? Room);

/// <summary>The lines of a day that belong to one person; <c>Heading</c> is the person and the group's minutes.</summary>
public sealed record PersonGroupView(string Heading, string Person, int Minutes, IReadOnlyList<SheetLineView> Lines);

/// <summary>One row of the day table. <c>TotalLabel</c> is null when no day total is asked for.</summary>
public sealed record DayRowView(
    DateOnly Day,
    string WeekdayLabel,
    string DateLabel,
    IReadOnlyList<PersonGroupView> Groups,
    string? TotalLabel);

/// <summary>A table with days as rows and one task column.</summary>
public sealed record DayTableView(string DayColumnHeader, string TaskColumnHeader, IReadOnlyList<DayRowView> Rows);

/// <summary>A week: its header lines and its day table (requirements 6.2). <c>Theme</c> is null when the plan has none.</summary>
public sealed record WeekView(string Title, string Period, string? Theme, DayTableView Table);

/// <summary>One printed page of the week schedule: one week, or two weeks side by side.</summary>
public sealed record SchedulePage(IReadOnlyList<WeekView> Weeks, SheetFooter Footer);

/// <summary>Week range sheet (requirements 6.1 and 6.2).</summary>
public sealed record WeekScheduleSheet(
    SheetLanguage Language,
    SheetOrientation Orientation,
    string DocumentTitle,
    string FileName,
    IReadOnlyList<SchedulePage> Pages);

/// <summary>Single-day sheet; it always carries the day total.</summary>
public sealed record DaySheet(
    SheetLanguage Language,
    string DocumentTitle,
    string FileName,
    string Heading,
    string Subtitle,
    string? Theme,
    DayTableView Table,
    SheetFooter Footer);

/// <summary>One line of the due list. <c>Emphasised</c> is the overdue state, printed in bold (the label spells it out as well).</summary>
public sealed record DueRowView(string Name, string Room, string Interval, int DaysSince, string Status, bool Emphasised, string Planned);

/// <summary>The headings of the due list columns.</summary>
public sealed record DueColumnHeaders(string Task, string Room, string Interval, string DaysAgo, string Status, string Planned);

/// <summary>The standalone overdue list. <c>EmptyMessage</c> replaces the table when there is nothing to show.</summary>
public sealed record DueListSheet(
    SheetLanguage Language,
    string DocumentTitle,
    string FileName,
    string Heading,
    string Subtitle,
    DueColumnHeaders Headers,
    IReadOnlyList<DueRowView> Rows,
    string? EmptyMessage,
    SheetFooter Footer);

/// <summary>One line of the task list. <c>InactiveLabel</c> is set for inactive tasks, for example <c>(inactief)</c>.</summary>
public sealed record TaskListRowView(string Room, string Name, string? InactiveLabel, string Interval, string Duration);

/// <summary>The headings of the task list columns.</summary>
public sealed record TaskListColumnHeaders(string Room, string Task, string Interval, string Duration);

/// <summary>The overview of all tasks. <c>EmptyMessage</c> replaces the table when there are no tasks.</summary>
public sealed record TaskListSheet(
    SheetLanguage Language,
    string DocumentTitle,
    string FileName,
    string Heading,
    string Summary,
    TaskListColumnHeaders Headers,
    IReadOnlyList<TaskListRowView> Rows,
    string? EmptyMessage,
    SheetFooter Footer);
