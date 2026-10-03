using System.Globalization;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Due;
using Huishoudplanner.Domain.Sheets;

namespace Huishoudplanner.Adapters.Pdf.Tests;

/// <summary>The household of the Node pdf-export tests, as sheet inputs. Anchor Monday 2026-09-14 (2026-W38).</summary>
internal static class SheetSamples
{
    public static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-14T06:00:00Z", CultureInfo.InvariantCulture);
    public static readonly TimeZoneInfo Zone = DayKeys.FindZone(DayKeys.AppTimezone);

    public static ScheduleOptions Options(SheetOrientation orientation = SheetOrientation.Portrait, bool totals = false, SheetLanguage language = SheetLanguage.Nl) =>
        new(orientation, totals, language, Now, Zone);

    public static SheetLineInput Line(string name, string? room = null, string? assignee = null, int minutes = 10) => new(name, room, assignee, minutes);

    public static WeekSheetInput Week(string monday, int weekInCycle = 1, string theme = "", params (int Offset, SheetLineInput[] Lines)[] filled)
    {
        var from = DateOnly.Parse(monday, CultureInfo.InvariantCulture);
        var days = Enumerable.Range(0, 7)
            .Select(offset => new SheetDayInput(from.AddDays(offset), filled.Where(f => f.Offset == offset).SelectMany(f => f.Lines).ToList()))
            .ToList();
        return new WeekSheetInput(from, from.AddDays(6), weekInCycle, theme, days);
    }

    /// <summary>Weeks W38..W45 of the Node tests: weekly bathroom on Monday, wastafel on Wednesday, windows dragged to Saturday of W39.</summary>
    public static WeekSheetInput Cycle(int weekIndex)
    {
        var monday = DateOnly.Parse("2026-09-14", CultureInfo.InvariantCulture).AddDays(7 * weekIndex).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var filled = new List<(int, SheetLineInput[])>
        {
            (0, [Line("Badkamer schoonmaken", "Badkamer", "Persoon 1", 30)]),
            (2, [Line("Wastafel", "Badkamer", null, 10)]),
        };
        if (weekIndex == 1)
        {
            filled.Add((5, [Line("Ramen lappen", "Keuken", "Persoon 2", 45)]));
        }

        return Week(monday, weekIndex % 4 + 1, weekIndex == 1 ? "Keuken" : "", [.. filled]);
    }

    public static WeekScheduleSheet Schedule(int weeks, SheetOrientation orientation = SheetOrientation.Portrait, bool totals = false, int firstWeekIndex = 0, SheetLanguage language = SheetLanguage.Nl) =>
        SheetBuilder.Schedule([.. Enumerable.Range(firstWeekIndex, weeks).Select(Cycle)], Options(orientation, totals, language));

    public static DaySheet Day() =>
        SheetBuilder.Day(Cycle(0), new DateOnly(2026, 9, 16), SheetLanguage.Nl, Now, Zone);

    public static DueListSheet Due(params DueRowInput[] rows) =>
        SheetBuilder.DueList(rows, new DateOnly(2026, 9, 14), SheetLanguage.Nl, Now, Zone);

    public static TaskListSheet Tasks(SheetLanguage language, params TaskListRowInput[] rows) =>
        SheetBuilder.TaskList(rows, language, Now, Zone);

    public static readonly TaskListRowInput[] HouseholdTasks =
    [
        new("Badkamer schoonmaken", "Badkamer", "1x per week", 30, true),
        new("Ramen lappen", "Keuken", "1x per 4 weken", 45, true),
        new("Wastafel", "Badkamer", "1x per 2 weken", 10, false),
    ];
}
