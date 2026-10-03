using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Due;
using Huishoudplanner.Domain.Sheets;

namespace Huishoudplanner.Domain.Tests.Sheets;

public sealed class SheetBuilderTests
{
    // Anchor Monday 2026-09-14 (2026-W38), as in the Node pdf-export tests.
    private static readonly DateTimeOffset GeneratedAt = DateTimeOffset.Parse("2026-09-14T06:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly TimeZoneInfo Amsterdam = DayKeys.FindZone(DayKeys.AppTimezone);

    private static ScheduleOptions Options(SheetOrientation orientation = SheetOrientation.Portrait, bool totals = false, SheetLanguage language = SheetLanguage.Nl) =>
        new(orientation, totals, language, GeneratedAt, Amsterdam);

    private static WeekSheetInput Week(string monday, int weekInCycle = 1, string theme = "", params (int Offset, SheetLineInput[] Lines)[] filled)
    {
        var from = DateOnly.Parse(monday, System.Globalization.CultureInfo.InvariantCulture);
        var days = Enumerable.Range(0, 7)
            .Select(offset => new SheetDayInput(from.AddDays(offset), filled.FirstOrDefault(f => f.Offset == offset).Lines ?? []))
            .ToList();
        return new WeekSheetInput(from, from.AddDays(6), weekInCycle, theme, days);
    }

    private static SheetLineInput Line(string name, string? room = null, string? assignee = null, int minutes = 10) => new(name, room, assignee, minutes);

    private static DayRowView Row(WeekView week, string weekday) => week.Table.Rows.Single(r => r.WeekdayLabel == weekday);

    [Theory]
    [InlineData(1, SheetOrientation.Portrait, 1)]
    [InlineData(2, SheetOrientation.Portrait, 2)]
    [InlineData(4, SheetOrientation.Portrait, 4)]
    [InlineData(2, SheetOrientation.Landscape, 1)]
    [InlineData(1, SheetOrientation.Landscape, 1)]
    [InlineData(4, SheetOrientation.Landscape, 4)]
    public void Schedule_weeksAndOrientation_decideThePages(int weeks, SheetOrientation orientation, int pages)
    {
        var inputs = Enumerable.Range(0, weeks).Select(i => Week(DateOnly.Parse("2026-09-14", System.Globalization.CultureInfo.InvariantCulture).AddDays(7 * i).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), i + 1)).ToList();

        var sheet = SheetBuilder.Schedule(inputs, Options(orientation));

        sheet.Pages.Should().HaveCount(pages);
        sheet.Pages.SelectMany(p => p.Weeks).Should().HaveCount(weeks);
        if (orientation == SheetOrientation.Landscape && weeks == 2)
        {
            sheet.Pages.Single().Weeks.Should().HaveCount(2);
        }
    }

    [Fact]
    public void Schedule_noWeeks_isRefused()
    {
        var act = () => SheetBuilder.Schedule([], Options());

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Schedule_header_namesCycleWeekDatesIsoWeekAndTheme()
    {
        var sheet = SheetBuilder.Schedule([Week("2026-09-21", 2, "Keuken")], Options());

        var week = sheet.Pages.Single().Weeks.Single();
        week.Title.Should().Be("Week 2 van de cyclus");
        week.Period.Should().Be("ma 21-09 t/m zo 27-09-2026 · 2026-W39");
        week.Theme.Should().Be("Thema: Keuken");
    }

    [Fact]
    public void Schedule_withoutTheme_hasNoThemeLine()
    {
        var sheet = SheetBuilder.Schedule([Week("2026-09-14")], Options());

        sheet.Pages.Single().Weeks.Single().Theme.Should().BeNull();
    }

    [Fact]
    public void Schedule_daysAreRowsMondayFirst_andEmptyDaysStay()
    {
        var sheet = SheetBuilder.Schedule([Week("2026-09-14", 1, "", (0, [Line("Badkamer schoonmaken", "Badkamer")]))], Options());

        var table = sheet.Pages.Single().Weeks.Single().Table;
        table.Rows.Select(r => r.WeekdayLabel).Should().Equal("maandag", "dinsdag", "woensdag", "donderdag", "vrijdag", "zaterdag", "zondag");
        table.Rows.Select(r => r.DateLabel).Should().StartWith("14-09").And.EndWith("20-09");
        table.Rows.Skip(1).Should().OnlyContain(r => r.Groups.Count == 0);
        table.DayColumnHeader.Should().Be("Dag");
        table.TaskColumnHeader.Should().Be("Taak");
    }

    [Fact]
    public void Schedule_rescheduledLine_sitsOnlyOnItsNewDay()
    {
        // The data layer reads generated occurrences, so a dragged item arrives on Saturday (offset 5), not Friday.
        var sheet = SheetBuilder.Schedule([Week("2026-09-21", 2, "", (5, [Line("Ramen lappen", "Keuken", "Persoon 2", 45)]))], Options());

        var week = sheet.Pages.Single().Weeks.Single();
        Row(week, "zaterdag").Groups.SelectMany(g => g.Lines).Select(l => l.Name).Should().Equal("Ramen lappen");
        Row(week, "vrijdag").Groups.Should().BeEmpty();
    }

    [Fact]
    public void Schedule_groupsLinesUnderPeople_sortedAlphabetically_withMinutesInTheHeading()
    {
        var lines = new[]
        {
            Line("Wastafel", "Badkamer", null, 10),
            Line("Stofzuigen", "Woonkamer", "Persoon 2", 20),
            Line("Badkamer schoonmaken", "Badkamer", "Persoon 1", 30),
            Line("Afwassen", "Keuken", "Persoon 1", 15),
        };

        var sheet = SheetBuilder.Schedule([Week("2026-09-14", 1, "", (0, lines))], Options());

        var groups = Row(sheet.Pages.Single().Weeks.Single(), "maandag").Groups;
        groups.Select(g => g.Heading).Should().Equal("Persoon 1 · 45 min", "Persoon 2 · 20 min", "Wie dan ook · 10 min");
        groups[0].Lines.Select(l => l.Name).Should().Equal("Afwassen", "Badkamer schoonmaken");
        groups[0].Lines[1].Room.Should().Be("Badkamer");
    }

    [Fact]
    public void Schedule_oneOffTaskWithoutRoom_hasNoRoom()
    {
        var sheet = SheetBuilder.Schedule([Week("2026-09-14", 1, "", (3, [Line("Kast ophalen", null, null, 25), Line("Magnetron ontkalken", "Keuken", "Persoon 2", 15)]))], Options());

        var lines = Row(sheet.Pages.Single().Weeks.Single(), "donderdag").Groups.SelectMany(g => g.Lines).ToList();
        lines.Single(l => l.Name == "Kast ophalen").Room.Should().BeNull();
        lines.Single(l => l.Name == "Magnetron ontkalken").Room.Should().Be("Keuken");
    }

    [Fact]
    public void Schedule_totalsOption_decidesTheDayTotal()
    {
        var week = Week("2026-09-14", 1, "", (4, [Line("A", null, "P", 30), Line("B", null, "Q", 15)]));

        var without = SheetBuilder.Schedule([week], Options(totals: false));
        var with = SheetBuilder.Schedule([week], Options(totals: true));

        without.Pages.Single().Weeks.Single().Table.Rows.Should().OnlyContain(r => r.TotalLabel == null);
        var rows = with.Pages.Single().Weeks.Single().Table.Rows;
        rows[4].TotalLabel.Should().Be("45 min");
        rows[0].TotalLabel.Should().Be("0 min");
    }

    [Fact]
    public void Schedule_isABlankChecklist_withNoStatusWordsAnywhere()
    {
        var sheet = SheetBuilder.Schedule([Week("2026-09-14", 1, "", (0, [Line("Badkamer schoonmaken", "Badkamer", "Persoon 1", 30)]))], Options(totals: true));

        var text = string.Join('\n', Flatten(sheet)).ToLowerInvariant();
        text.Should().Contain("badkamer schoonmaken");
        foreach (var marker in new[] { "done", "gedaan", "afgevinkt door", "overgeslagen", "✓", "☑", "✔" })
        {
            text.Should().NotContain(marker);
        }
    }

    [Fact]
    public void Schedule_footer_carriesGenerationDateInHouseholdTimeAndThePaperNote()
    {
        var sheet = SheetBuilder.Schedule([Week("2026-09-14"), Week("2026-09-21", 2)], Options());

        sheet.Pages.Should().OnlyContain(p =>
            p.Footer.GeneratedLabel == "Gegenereerd op 14-09-2026 08:00"
            && p.Footer.Note == "Afvinken op papier wordt niet automatisch in de app verwerkt.");
    }

    [Fact]
    public void Schedule_english_translatesTheChromeButNotUserText()
    {
        var sheet = SheetBuilder.Schedule([Week("2026-09-21", 2, "Keuken", (0, [Line("Badkamer schoonmaken", "Badkamer", null, 30)]))], Options(language: SheetLanguage.En));

        var week = sheet.Pages.Single().Weeks.Single();
        week.Title.Should().Be("Week 2 of the cycle");
        week.Period.Should().Be("Mon 21-09 to Sun 27-09-2026 · 2026-W39");
        week.Theme.Should().Be("Theme: Keuken");
        week.Table.Rows[0].WeekdayLabel.Should().Be("Monday");
        week.Table.Rows[0].Groups.Single().Heading.Should().Be("Anyone · 30 min");
        week.Table.Rows[0].Groups.Single().Lines.Single().Name.Should().Be("Badkamer schoonmaken");
        sheet.Pages.Single().Footer.GeneratedLabel.Should().Be("Generated on 14-09-2026 08:00");
    }

    [Theory]
    [InlineData("2026-09-14", "huishoudschema-2026-w38.pdf")]
    public void Schedule_fileName_namesThePeriod(string monday, string expected)
    {
        SheetBuilder.Schedule([Week(monday)], Options()).FileName.Should().Be(expected);
    }

    [Fact]
    public void ScheduleFileName_buildsNames_alsoAcrossAYearBoundary()
    {
        SheetBuilder.ScheduleFileName(["2026-W38"]).Should().Be("huishoudschema-2026-w38.pdf");
        SheetBuilder.ScheduleFileName(["2026-W38", "2026-W41"]).Should().Be("huishoudschema-2026-w38-w41.pdf");
        SheetBuilder.ScheduleFileName(["2026-W53", "2027-W01"]).Should().Be("huishoudschema-2026-w53-2027-w01.pdf");
    }

    [Fact]
    public void Schedule_twoWeeksFileName_listsBothWeeks()
    {
        var sheet = SheetBuilder.Schedule([Week("2026-09-14"), Week("2026-09-21", 2)], Options());

        sheet.FileName.Should().Be("huishoudschema-2026-w38-w39.pdf");
    }

    [Fact]
    public void Day_reducesTheWeekToOneDay_withTotalAndTitle()
    {
        var week = Week("2026-09-14", 1, "Keuken", (2, [Line("Wastafel", "Badkamer", null, 10)]), (0, [Line("Badkamer schoonmaken", "Badkamer", "Persoon 1", 30)]));

        var sheet = SheetBuilder.Day(week, new DateOnly(2026, 9, 16), SheetLanguage.Nl, GeneratedAt, Amsterdam);

        sheet.Heading.Should().Be("Dagschema woensdag 16-09-2026");
        sheet.Subtitle.Should().Be("Week 1 van de cyclus · 2026-W38");
        sheet.Theme.Should().Be("Thema: Keuken");
        sheet.FileName.Should().Be("huishoudschema-2026-09-16.pdf");
        sheet.Table.Rows.Should().ContainSingle();
        sheet.Table.Rows[0].TotalLabel.Should().Be("10 min");
        sheet.Table.Rows[0].Groups.SelectMany(g => g.Lines).Select(l => l.Name).Should().Equal("Wastafel");
        sheet.Footer.GeneratedLabel.Should().Be("Gegenereerd op 14-09-2026 08:00");
    }

    [Fact]
    public void Day_outsideTheWeek_isRefused()
    {
        var act = () => SheetBuilder.Day(Week("2026-09-14"), new DateOnly(2026, 9, 21), SheetLanguage.Nl, GeneratedAt, Amsterdam);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DueList_nothingDue_showsTheEmptyState()
    {
        var sheet = SheetBuilder.DueList([new DueRowInput("Wastafel", "Badkamer", "1x per week", 2, DueState.Ok, null)], new DateOnly(2026, 9, 14), SheetLanguage.Nl, GeneratedAt, Amsterdam);

        sheet.Heading.Should().Be("Achterstand");
        sheet.Subtitle.Should().Be("Stand van maandag 14-09-2026");
        sheet.FileName.Should().Be("achterstand-2026-09-14.pdf");
        sheet.Rows.Should().BeEmpty();
        sheet.EmptyMessage.Should().Be("Geen achterstand.");
    }

    [Fact]
    public void DueList_listsOverdueFirst_withTheStateSpelledOut()
    {
        var items = new[]
        {
            new DueRowInput("Wastafel", "Badkamer", "1x per week", 8, DueState.Due, new DateOnly(2026, 9, 16)),
            new DueRowInput("Ramen lappen", null, "1x per 4 weken", 50, DueState.Overdue, null),
            new DueRowInput("Fijn", "Keuken", "Dagelijks", 1, DueState.Ok, null),
        };

        var sheet = SheetBuilder.DueList(items, new DateOnly(2026, 9, 14), SheetLanguage.Nl, GeneratedAt, Amsterdam);

        sheet.EmptyMessage.Should().BeNull();
        sheet.Rows.Select(r => r.Name).Should().Equal("Ramen lappen", "Wastafel");
        sheet.Rows[0].Should().Be(new DueRowView("Ramen lappen", "", "1x per 4 weken", 50, "Flink achter", true, "—"));
        sheet.Rows[1].Should().Be(new DueRowView("Wastafel", "Badkamer", "1x per week", 8, "Aan de beurt", false, "wo 16-09"));
        sheet.Headers.DaysAgo.Should().Be("Dagen geleden");
    }

    [Fact]
    public void DueList_english_translatesStatesAndHeadings()
    {
        var sheet = SheetBuilder.DueList([new DueRowInput("A", null, "Daily", 3, DueState.Overdue, null)], new DateOnly(2026, 9, 14), SheetLanguage.En, GeneratedAt, Amsterdam);

        sheet.Heading.Should().Be("Overdue tasks");
        sheet.Subtitle.Should().Be("Status on Monday 14-09-2026");
        sheet.Rows.Single().Status.Should().Be("Significantly overdue");
    }

    [Fact]
    public void TaskList_sortsByRoomThenName_marksInactive_andSummarises()
    {
        var tasks = new[]
        {
            new TaskListRowInput("Ramen lappen", "Keuken", "1x per 4 weken", 45, true),
            new TaskListRowInput("Wastafel", "Badkamer", "1x per 2 weken", 10, false),
            new TaskListRowInput("Badkamer schoonmaken", "Badkamer", "1x per week", 30, true),
        };

        var sheet = SheetBuilder.TaskList(tasks, SheetLanguage.Nl, GeneratedAt, Amsterdam);

        sheet.Heading.Should().Be("Alle huishoudtaken");
        sheet.Summary.Should().Be("3 taken · ruimte, interval en geschatte duur");
        sheet.FileName.Should().Be("huishoudtaken.pdf");
        sheet.Rows.Select(r => r.Name).Should().Equal("Badkamer schoonmaken", "Wastafel", "Ramen lappen");
        sheet.Rows[0].Should().Be(new TaskListRowView("Badkamer", "Badkamer schoonmaken", null, "1x per week", "30 min"));
        sheet.Rows[1].InactiveLabel.Should().Be("(inactief)");
        sheet.Footer.Note.Should().Be("Overzicht uit Keep the House Clean");
    }

    [Fact]
    public void TaskList_sortingIsAccentAndCaseInsensitive()
    {
        var tasks = new[]
        {
            new TaskListRowInput("zeep", "Eetkamer", "x", 1, true),
            new TaskListRowInput("Étagere", "Eetkamer", "x", 1, true),
            new TaskListRowInput("Eten", "Eetkamer", "x", 1, true),
        };

        var sheet = SheetBuilder.TaskList(tasks, SheetLanguage.Nl, GeneratedAt, Amsterdam);

        sheet.Rows.Select(r => r.Name).Should().Equal("Étagere", "Eten", "zeep");
    }

    [Fact]
    public void TaskList_singleTask_usesTheSingular_andEmptyShowsTheEmptyState()
    {
        SheetBuilder.TaskList([new TaskListRowInput("A", "B", "C", 5, true)], SheetLanguage.Nl, GeneratedAt, Amsterdam).Summary.Should().StartWith("1 taak ·");
        var empty = SheetBuilder.TaskList([], SheetLanguage.Nl, GeneratedAt, Amsterdam);
        empty.EmptyMessage.Should().Be("Geen taken.");
        empty.Summary.Should().StartWith("0 taken");
    }

    [Fact]
    public void TaskList_english_translatesChromeButKeepsUserNames()
    {
        var sheet = SheetBuilder.TaskList([new TaskListRowInput("Badkamer schoonmaken", "Badkamer", "1x per week", 30, false)], SheetLanguage.En, GeneratedAt, Amsterdam);

        sheet.Heading.Should().Be("All household tasks");
        sheet.Headers.Room.Should().Be("Room");
        sheet.Headers.Duration.Should().Be("Duration");
        sheet.Rows.Single().InactiveLabel.Should().Be("(inactive)");
        sheet.Rows.Single().Name.Should().Be("Badkamer schoonmaken");
    }

    private static IEnumerable<string> Flatten(WeekScheduleSheet sheet)
    {
        foreach (var page in sheet.Pages)
        {
            yield return page.Footer.GeneratedLabel;
            yield return page.Footer.Note;
            foreach (var week in page.Weeks)
            {
                yield return week.Title;
                yield return week.Period;
                foreach (var row in week.Table.Rows)
                {
                    yield return row.WeekdayLabel;
                    yield return row.TotalLabel ?? string.Empty;
                    foreach (var group in row.Groups)
                    {
                        yield return group.Heading;
                        foreach (var line in group.Lines)
                        {
                            yield return line.Name;
                            yield return line.Room ?? string.Empty;
                        }
                    }
                }
            }
        }
    }
}
