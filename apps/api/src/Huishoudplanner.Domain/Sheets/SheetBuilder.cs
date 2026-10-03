using System.Globalization;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Due;

namespace Huishoudplanner.Domain.Sheets;

/// <summary>
/// The layout rules of requirements section 6 as pure code (port of the view-model part of
/// <c>apps/server/src/domain/pdf/sheets.ts</c> and the decisions inside <c>html.ts</c>): which lines go where, in what
/// order, under which person, with which texts and which empty states. Black-and-white safe: no state is carried
/// by colour, the due list spells the state out.
/// </summary>
public static class SheetBuilder
{
    /// <summary>
    /// The week schedule: one page per week, except that a landscape sheet of exactly two weeks puts both side by
    /// side on one page (requirements 6.2). The file name names the period, for example <c>huishoudschema-2026-w38-w39.pdf</c>.
    /// </summary>
    public static WeekScheduleSheet Schedule(IReadOnlyList<WeekSheetInput> weeks, ScheduleOptions options)
    {
        ArgumentNullException.ThrowIfNull(weeks);
        ArgumentNullException.ThrowIfNull(options);
        if (weeks.Count == 0)
        {
            throw new ArgumentException("A schedule needs at least one week.", nameof(weeks));
        }

        var text = SheetText.For(options.Language);
        var footer = Footer(text, options.GeneratedAt, options.Zone, text.PaperNote);
        var views = weeks.Select(week => WeekBlock(week, text, options.Language, options.Totals)).ToList();
        IReadOnlyList<SchedulePage> pages = options.Orientation == SheetOrientation.Landscape && views.Count == 2
            ? [new SchedulePage(views, footer)]
            : [.. views.Select(view => new SchedulePage([view], footer))];

        return new WeekScheduleSheet(
            options.Language,
            options.Orientation,
            text.DocumentTitle,
            ScheduleFileName(weeks.Select(w => DayKeys.IsoWeekLabel(w.From)).ToList()),
            pages);
    }

    /// <summary>A single-day sheet: the week the day sits in, reduced to that day. It always carries the day total.</summary>
    public static DaySheet Day(WeekSheetInput week, DateOnly day, SheetLanguage language, DateTimeOffset generatedAt, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(week);
        var text = SheetText.For(language);
        var input = week.Days.FirstOrDefault(d => d.Day == day)
            ?? throw new ArgumentException($"Day {day:yyyy-MM-dd} is not part of the week.", nameof(day));
        var table = Table(text, language, [input], totals: true);
        var title = $"{text.WeekdaysLong[(int)day.DayOfWeek]} {SheetText.DayMonth(day, withYear: true)}";
        return new DaySheet(
            language,
            text.DocumentTitle,
            $"huishoudschema-{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.pdf",
            $"{text.DaySchedule} {title}",
            $"{text.CycleWeek(week.WeekNumberInCycle)} · {DayKeys.IsoWeekLabel(week.From)}",
            ThemeLine(text, week.Theme),
            table,
            Footer(text, generatedAt, zone, text.PaperNote));
    }

    /// <summary>
    /// The standalone overdue list: only tasks that are due or overdue, overdue first (then in the given order, which is
    /// the ranking of the due engine), the state spelled out in words.
    /// </summary>
    public static DueListSheet DueList(IReadOnlyList<DueRowInput> items, DateOnly today, SheetLanguage language, DateTimeOffset generatedAt, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(items);
        var text = SheetText.For(language);
        var rows = items
            .Where(item => item.State != DueState.Ok)
            .OrderByDescending(item => item.State == DueState.Overdue)
            .Select(item => new DueRowView(
                item.Name,
                item.Room ?? string.Empty,
                item.Interval,
                item.DaysSince,
                item.State == DueState.Overdue ? text.FarBehind : text.Due,
                item.State == DueState.Overdue,
                item.NextDate is { } next ? text.ShortDate(next) : "—"))
            .ToList();
        return new DueListSheet(
            language,
            text.DocumentTitle,
            $"achterstand-{today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.pdf",
            text.Backlog,
            $"{text.StatusOn} {text.WeekdaysLong[(int)today.DayOfWeek]} {SheetText.DayMonth(today, withYear: true)}",
            new DueColumnHeaders(text.Task, text.Room, text.Interval, text.DaysAgo, text.Status, text.Planned),
            rows,
            rows.Count == 0 ? text.NoBacklog : null,
            Footer(text, generatedAt, zone, text.PaperNote));
    }

    /// <summary>The overview of all tasks, sorted by room and then name in the sheet language, inactive tasks marked.</summary>
    public static TaskListSheet TaskList(IReadOnlyList<TaskListRowInput> tasks, SheetLanguage language, DateTimeOffset generatedAt, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var text = SheetText.For(language);
        var collation = SheetText.Collation(language);
        var rows = tasks
            .Order(Comparer<TaskListRowInput>.Create((a, b) =>
            {
                var byRoom = collation.Compare(a.Room, b.Room, CompareOptions.None);
                return byRoom != 0 ? byRoom : collation.Compare(a.Name, b.Name, CompareOptions.None);
            }))
            .Select(task => new TaskListRowView(
                task.Room,
                task.Name,
                task.Active ? null : $"({text.Inactive})",
                task.Interval,
                SheetText.Minutes(task.DurationMinutes)))
            .ToList();
        return new TaskListSheet(
            language,
            text.DocumentTitle,
            "huishoudtaken.pdf",
            text.AllTasks,
            $"{rows.Count} {(rows.Count == 1 ? text.OneTask : text.ManyTasks)} · {text.TaskSummary}",
            new TaskListColumnHeaders(text.Room, text.Task, text.Interval, text.Duration),
            rows,
            rows.Count == 0 ? text.NoTasks : null,
            Footer(text, generatedAt, zone, text.TaskOverview));
    }

    /// <summary>What the task list prints for a task whose room no longer exists, in the sheet language.</summary>
    public static string UnknownRoom(SheetLanguage language) => SheetText.For(language).UnknownRoom;

    /// <summary>
    /// <c>huishoudschema-2026-w38.pdf</c>, <c>huishoudschema-2026-w38-w39.pdf</c> and, across a year boundary,
    /// <c>huishoudschema-2026-w52-2027-w01.pdf</c>. The input is ISO week labels like <c>2026-W38</c>.
    /// </summary>
    public static string ScheduleFileName(IReadOnlyList<string> isoWeeks)
    {
        ArgumentNullException.ThrowIfNull(isoWeeks);
        if (isoWeeks.Count == 0)
        {
            throw new ArgumentException("At least one ISO week is needed.", nameof(isoWeeks));
        }

        static (string Year, string Week) Parse(string label)
        {
            var parts = label.Split("-W");
            return (parts[0], $"w{parts[1]}");
        }

        var first = Parse(isoWeeks[0]);
        var last = Parse(isoWeeks[^1]);
        if (isoWeeks.Count == 1)
        {
            return $"huishoudschema-{first.Year}-{first.Week}.pdf";
        }

        return first.Year == last.Year
            ? $"huishoudschema-{first.Year}-{first.Week}-{last.Week}.pdf"
            : $"huishoudschema-{first.Year}-{first.Week}-{last.Year}-{last.Week}.pdf";
    }

    private static WeekView WeekBlock(WeekSheetInput week, SheetText text, SheetLanguage language, bool totals)
    {
        var from = text.ShortDate(week.From);
        var to = text.ShortDate(week.To, withYear: true);
        return new WeekView(
            text.CycleWeek(week.WeekNumberInCycle),
            $"{from} {text.Through} {to} · {DayKeys.IsoWeekLabel(week.From)}",
            ThemeLine(text, week.Theme),
            Table(text, language, week.Days, totals));
    }

    private static string? ThemeLine(SheetText text, string theme) =>
        string.IsNullOrEmpty(theme) ? null : $"{text.Theme}: {theme}";

    private static DayTableView Table(SheetText text, SheetLanguage language, IReadOnlyList<SheetDayInput> days, bool totals) =>
        new(text.Day, text.Task, [.. days.Select(day => Row(text, language, day, totals))]);

    /// <summary>Lines sorted by person then task name; a group per person in that order, empty days stay (a gap is information).</summary>
    private static DayRowView Row(SheetText text, SheetLanguage language, SheetDayInput day, bool totals)
    {
        var collation = SheetText.Collation(language);
        var lines = day.Lines
            .Select(line => (Person: string.IsNullOrWhiteSpace(line.Assignee) ? text.Anyone : line.Assignee, Line: line))
            .OrderBy(entry => entry, Comparer<(string Person, SheetLineInput Line)>.Create((a, b) =>
            {
                var byPerson = collation.Compare(a.Person, b.Person, CompareOptions.None);
                return byPerson != 0 ? byPerson : collation.Compare(a.Line.Name, b.Line.Name, CompareOptions.None);
            }))
            .ToList();

        var groups = lines
            .GroupBy(entry => entry.Person, StringComparer.Ordinal)
            .Select(group =>
            {
                var minutes = group.Sum(entry => entry.Line.Minutes);
                return new PersonGroupView(
                    $"{group.Key} · {SheetText.Minutes(minutes)}",
                    group.Key,
                    minutes,
                    [.. group.Select(entry => new SheetLineView(entry.Line.Name, string.IsNullOrEmpty(entry.Line.Room) ? null : entry.Line.Room))]);
            })
            .ToList();

        return new DayRowView(
            day.Day,
            text.WeekdaysLong[(int)day.Day.DayOfWeek],
            SheetText.DayMonth(day.Day),
            groups,
            totals ? SheetText.Minutes(day.Lines.Sum(line => line.Minutes)) : null);
    }

    private static SheetFooter Footer(SheetText text, DateTimeOffset generatedAt, TimeZoneInfo zone, string note) =>
        new(
            $"{text.Generated} {TimeZoneInfo.ConvertTime(generatedAt, zone).ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture)}",
            note);
}
