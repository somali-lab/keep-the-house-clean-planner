using System.Globalization;

namespace Huishoudplanner.Domain.Sheets;

/// <summary>
/// The fixed texts of the sheets (the <c>COPY</c> table of <c>apps/server/src/domain/pdf/html.ts</c>) and the date
/// formats they use. Weekday arrays are indexed 0=Sunday..6=Saturday like <c>DayKeys.WeekdaySun0</c>.
/// </summary>
internal sealed record SheetText(
    string[] WeekdaysLong,
    string[] WeekdaysShort,
    string Day,
    string Theme,
    string Through,
    string Generated,
    string PaperNote,
    string DocumentTitle,
    string CycleWeekFormat,
    string NoTasks,
    string Room,
    string Task,
    string Interval,
    string Duration,
    string Inactive,
    string AllTasks,
    string OneTask,
    string ManyTasks,
    string TaskSummary,
    string TaskOverview,
    string Backlog,
    string StatusOn,
    string NoBacklog,
    string DaysAgo,
    string Status,
    string Planned,
    string FarBehind,
    string Due,
    string DaySchedule,
    string Anyone)
{
    private static readonly SheetText NlText = new(
        ["zondag", "maandag", "dinsdag", "woensdag", "donderdag", "vrijdag", "zaterdag"],
        ["zo", "ma", "di", "wo", "do", "vr", "za"],
        Day: "Dag",
        Theme: "Thema",
        Through: "t/m",
        Generated: "Gegenereerd op",
        PaperNote: "Afvinken op papier wordt niet automatisch in de app verwerkt.",
        DocumentTitle: "Keep the House Clean",
        CycleWeekFormat: "Week {0} van de cyclus",
        NoTasks: "Geen taken.",
        Room: "Ruimte",
        Task: "Taak",
        Interval: "Interval",
        Duration: "Duur",
        Inactive: "inactief",
        AllTasks: "Alle huishoudtaken",
        OneTask: "taak",
        ManyTasks: "taken",
        TaskSummary: "ruimte, interval en geschatte duur",
        TaskOverview: "Overzicht uit Keep the House Clean",
        Backlog: "Achterstand",
        StatusOn: "Stand van",
        NoBacklog: "Geen achterstand.",
        DaysAgo: "Dagen geleden",
        Status: "Status",
        Planned: "Gepland",
        FarBehind: "Flink achter",
        Due: "Aan de beurt",
        DaySchedule: "Dagschema",
        Anyone: "Wie dan ook");

    private static readonly SheetText EnText = new(
        ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"],
        ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"],
        Day: "Day",
        Theme: "Theme",
        Through: "to",
        Generated: "Generated on",
        PaperNote: "Checking tasks off on paper is not automatically processed in the app.",
        DocumentTitle: "Keep the House Clean",
        CycleWeekFormat: "Week {0} of the cycle",
        NoTasks: "No tasks.",
        Room: "Room",
        Task: "Task",
        Interval: "Interval",
        Duration: "Duration",
        Inactive: "inactive",
        AllTasks: "All household tasks",
        OneTask: "task",
        ManyTasks: "tasks",
        TaskSummary: "room, interval and estimated duration",
        TaskOverview: "Overview from Keep the House Clean",
        Backlog: "Overdue tasks",
        StatusOn: "Status on",
        NoBacklog: "No overdue tasks.",
        DaysAgo: "Days ago",
        Status: "Status",
        Planned: "Scheduled",
        FarBehind: "Significantly overdue",
        Due: "Due",
        DaySchedule: "Daily schedule",
        Anyone: "Anyone");

    public static SheetText For(SheetLanguage language) => language == SheetLanguage.En ? EnText : NlText;

    public string CycleWeek(int week) => string.Format(CultureInfo.InvariantCulture, CycleWeekFormat, week);

    /// <summary>The culture used to sort names: Dutch sheets sort Dutch, English sheets sort English.</summary>
    public static CompareInfo Collation(SheetLanguage language) =>
        CompareInfo.GetCompareInfo(language == SheetLanguage.En ? "en-US" : "nl-NL");

    /// <summary><c>dd-MM</c> or, with the year, <c>dd-MM-yyyy</c>.</summary>
    public static string DayMonth(DateOnly day, bool withYear = false) =>
        day.ToString(withYear ? "dd-MM-yyyy" : "dd-MM", CultureInfo.InvariantCulture);

    public static string Minutes(int minutes) => string.Create(CultureInfo.InvariantCulture, $"{minutes} min");

    /// <summary>Short weekday and day-month, for example <c>ma 21-09</c>.</summary>
    public string ShortDate(DateOnly day, bool withYear = false) =>
        $"{WeekdaysShort[(int)day.DayOfWeek]} {DayMonth(day, withYear)}";
}
