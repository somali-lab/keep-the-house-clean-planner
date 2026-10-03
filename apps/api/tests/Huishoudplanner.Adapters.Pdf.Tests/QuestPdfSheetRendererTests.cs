using Huishoudplanner.Adapters.Pdf;
using Huishoudplanner.Domain.Due;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Sheets;

using OneOf;
using static Huishoudplanner.Adapters.Pdf.Tests.SheetSamples;

namespace Huishoudplanner.Adapters.Pdf.Tests;

/// <summary>
/// The renderer draws the view models; these tests read the rendered PDF back (text per page, page count and size) and
/// assert the layout rules of requirements section 6 on what a person would see. They port the renderer and layout
/// scenarios of <c>apps/server/test/pdf-export.test.ts</c>; routes and data gathering come with slice 6.4b.
/// </summary>
public sealed class QuestPdfSheetRendererTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static readonly string[] Weekdays = ["maandag", "dinsdag", "woensdag", "donderdag", "vrijdag", "zaterdag", "zondag"];

    private static QuestPdfSheetRenderer Renderer() => new(new FixedTimeProvider("2026-09-14T06:00:00Z"));

    private static (RenderedSheet Sheet, PdfProbe Pdf) Ok(OneOf<RenderedSheet, PortError> result)
    {
        result.IsT0.Should().BeTrue(result.IsT1 ? result.AsT1.Message : "rendering succeeds");
        return (result.AsT0, PdfProbe.Read(result.AsT0.Content));
    }

    private static async Task<(RenderedSheet Sheet, PdfProbe Pdf)> RenderWeek(WeekScheduleSheet sheet) =>
        Ok(await Renderer().RenderWeekScheduleAsync(sheet, Ct));

    [Fact]
    public async Task RenderWeekSchedule_returnsAValidPdfWithTheSuggestedName()
    {
        var (sheet, pdf) = await RenderWeek(Schedule(2));

        sheet.ContentType.Should().Be("application/pdf");
        sheet.FileName.Should().Be("huishoudschema-2026-w38-w39.pdf");
        System.Text.Encoding.ASCII.GetString(sheet.Content, 0, 5).Should().Be("%PDF-");
        pdf.Pages.Should().Be(2);
    }

    [Theory]
    [InlineData(1, SheetOrientation.Portrait, 1)]
    [InlineData(2, SheetOrientation.Portrait, 2)]
    [InlineData(4, SheetOrientation.Portrait, 4)]
    [InlineData(2, SheetOrientation.Landscape, 1)]
    [InlineData(1, SheetOrientation.Landscape, 1)]
    [InlineData(4, SheetOrientation.Landscape, 4)]
    public async Task RenderWeekSchedule_weeksAndOrientation_decideThePageCount(int weeks, SheetOrientation orientation, int pages)
    {
        var (_, pdf) = await RenderWeek(Schedule(weeks, orientation, totals: true));

        pdf.Pages.Should().Be(pages);
    }

    [Theory]
    [InlineData(SheetOrientation.Portrait, false)]
    [InlineData(SheetOrientation.Landscape, true)]
    public async Task RenderWeekSchedule_pageIsA4InTheRequestedOrientation(SheetOrientation orientation, bool landscape)
    {
        var (_, pdf) = await RenderWeek(Schedule(1, orientation));

        // A4 is 595 x 842 points.
        Math.Round(Math.Min(pdf.Width, pdf.Height)).Should().Be(595);
        Math.Round(Math.Max(pdf.Width, pdf.Height)).Should().Be(842);
        (pdf.Width > pdf.Height).Should().Be(landscape);
    }

    [Fact]
    public async Task RenderWeekSchedule_twoWeeksLandscape_putsBothWeeksOnTheOnePage()
    {
        var (_, pdf) = await RenderWeek(Schedule(2, SheetOrientation.Landscape));

        pdf.Text.Should().Contain("Week 1 van de cyclus").And.Contain("Week 2 van de cyclus");
        pdf.Text.Should().Contain("2026-W38").And.Contain("2026-W39");
        pdf.Text.Split("Gegenereerd op").Length.Should().Be(2, "one footer for the page");
    }

    [Fact]
    public async Task RenderWeekSchedule_showsRescheduledLinesOnTheirNewDay()
    {
        var (_, pdf) = await RenderWeek(Schedule(1, firstWeekIndex: 1));

        pdf.Between("zaterdag", "zondag").Should().Contain("Ramen lappen");
        pdf.Between("vrijdag", "zaterdag").Should().NotContain("Ramen lappen");
    }

    [Fact]
    public async Task RenderWeekSchedule_isABlankChecklist_withoutAnyDoneMarking()
    {
        var (_, pdf) = await RenderWeek(Schedule(1));

        pdf.Between("maandag", "dinsdag").Replace('\n', ' ').Should().Contain("Badkamer schoonmaken");
        foreach (var marker in new[] { "done", "gedaan", "afgevinkt door", "overgeslagen", "✓", "☑", "✔" })
        {
            pdf.Text.ToLowerInvariant().Should().NotContain(marker);
        }
    }

    [Fact]
    public async Task RenderWeekSchedule_printsHeaderThemeRoomsPeopleFooterAndTheDayTotal()
    {
        var (_, pdf) = await RenderWeek(Schedule(1, totals: true, firstWeekIndex: 1));

        pdf.Text.Should().Contain("Week 2 van de cyclus");
        pdf.Text.Should().Contain("ma 21-09 t/m zo 27-09-2026");
        pdf.Text.Should().Contain("Thema: Keuken");
        pdf.Text.Should().Contain("Keuken"); // room of "Ramen lappen"
        pdf.Text.Should().Contain("Persoon 1");
        System.Text.RegularExpressions.Regex.Count(pdf.Text, "Persoon 1").Should().Be(1);
        pdf.Text.Should().Contain("Wie dan ook");
        pdf.Text.Should().Contain("Gegenereerd op 14-09-2026 08:00");
        pdf.Text.Should().Contain("Afvinken op papier wordt niet automatisch in de app verwerkt.");
        pdf.Text.Should().Contain("45 min");
    }

    [Fact]
    public async Task RenderWeekSchedule_daysAreRowsMondayFirst_andEmptyDaysStayVisible()
    {
        var (_, pdf) = await RenderWeek(Schedule(1));

        var positions = Weekdays
            .Select(day => pdf.Text.IndexOf(day, StringComparison.Ordinal)).ToList();
        positions.Should().OnlyContain(p => p >= 0);
        positions.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task RenderWeekSchedule_englishTranslatesTheChrome_butKeepsUserNames()
    {
        var (_, pdf) = await RenderWeek(Schedule(1, language: SheetLanguage.En));

        pdf.Text.Should().Contain("Week 1 of the cycle").And.Contain("Monday").And.Contain("Generated on 14-09-2026 08:00");
        pdf.Text.Should().Contain("Badkamer schoonmaken");
        pdf.Text.Should().NotContain("maandag").And.NotContain("Gegenereerd");
    }

    [Fact]
    public async Task RenderWeekSchedule_specialCharactersAndMarkupInNames_arePrintedAsTyped()
    {
        var week = Week("2026-09-14", 1, "Thema ëén", (0, [Line("Café ünïcode & <b>Tom</b> 'Jerry' \"x\"", "Wasruimte é", "Zoë", 5)]));
        var sheet = SheetBuilder.Schedule([week], Options());

        var (_, pdf) = await RenderWeek(sheet);

        pdf.Flat.Should().Contain("Café ünïcode & <b>Tom</b> 'Jerry' \"x\"");
        pdf.Flat.Should().Contain("Wasruimte é").And.Contain("Zoë").And.Contain("Thema ëén");
    }

    [Fact]
    public async Task RenderWeekSchedule_longTaskNames_wrapInsteadOfFailing()
    {
        var words = string.Join(' ', Enumerable.Repeat("Grondig", 25));
        var unbroken = new string('x', 120);
        var week = Week("2026-09-14", 1, "", (0, [Line(words, "Keuken", "Persoon 1", 5), Line(unbroken, null, "Persoon 2", 5)]));

        var (_, pdf) = await RenderWeek(SheetBuilder.Schedule([week], Options()));

        pdf.Pages.Should().Be(1);
        pdf.Flat.Should().Contain("Grondig Grondig Grondig");
        System.Text.RegularExpressions.Regex.Count(pdf.Text, "x").Should().Be(120);
    }

    [Fact]
    public async Task RenderWeekSchedule_aBusyDay_growsItsRowAndStaysOnItsPage()
    {
        var lines = Enumerable.Range(1, 12).Select(i => Line($"Taak nummer {i:00}", "Keuken", i % 2 == 0 ? "Persoon 1" : "Persoon 2", 5)).ToArray();
        var week = Week("2026-09-14", 1, "", (0, lines));

        var (_, pdf) = await RenderWeek(SheetBuilder.Schedule([week], Options(totals: true)));

        pdf.Text.Should().Contain("Taak nummer 01").And.Contain("Taak nummer 12");
        pdf.Pages.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task RenderWeekSchedule_aDayWithMoreLinesThanAPage_continuesOnTheNextPage()
    {
        var lines = Enumerable.Range(1, 120).Select(i => Line($"Taak nummer {i:000}", "Keuken", "Persoon 1", 5)).ToArray();

        var (_, pdf) = await RenderWeek(SheetBuilder.Schedule([Week("2026-09-14", 1, "", (0, lines))], Options(totals: true)));

        pdf.Pages.Should().BeGreaterThan(1);
        pdf.Text.Should().Contain("Taak nummer 001").And.Contain("Taak nummer 120");
    }

    [Fact]
    public async Task RenderDay_exportsASingleDayOnOnePage()
    {
        var (sheet, pdf) = Ok(await Renderer().RenderDayAsync(Day(), Ct));

        sheet.FileName.Should().Be("huishoudschema-2026-09-16.pdf");
        pdf.Pages.Should().Be(1);
        pdf.Text.Should().Contain("Dagschema woensdag 16-09-2026");
        pdf.Text.Should().Contain("Wastafel");
        pdf.Text.Should().NotContain("Badkamer schoonmaken");
        pdf.Text.Should().Contain("10 min");
        pdf.Text.Should().Contain("Gegenereerd op 14-09-2026 08:00");
    }

    [Fact]
    public async Task RenderDueList_nothingDue_printsTheEmptyState()
    {
        var (sheet, pdf) = Ok(await Renderer().RenderDueListAsync(Due(), Ct));

        sheet.FileName.Should().Be("achterstand-2026-09-14.pdf");
        pdf.Pages.Should().Be(1);
        pdf.Text.Should().Contain("Achterstand").And.Contain("Geen achterstand.");
        pdf.Text.Should().Contain("Stand van maandag 14-09-2026");
    }

    [Fact]
    public async Task RenderDueList_listsOverdueFirstWithTheStateInWords()
    {
        var (_, pdf) = Ok(await Renderer().RenderDueListAsync(
            Due(
                new DueRowInput("Wastafel", "Badkamer", "1x per week", 8, DueState.Due, new DateOnly(2026, 9, 16)),
                new DueRowInput("Ramen lappen", null, "1x per 4 weken", 50, DueState.Overdue, null)),
            Ct));

        pdf.Pages.Should().Be(1);
        pdf.Text.Should().NotContain("Geen achterstand.");
        pdf.Text.Should().Contain("Flink achter").And.Contain("Aan de beurt").And.Contain("wo 16-09");
        pdf.Text.Should().Contain("Dagen geleden");
        pdf.Text.IndexOf("Ramen lappen", StringComparison.Ordinal).Should().BeLessThan(pdf.Text.IndexOf("Wastafel", StringComparison.Ordinal));
        pdf.Text.Should().Contain("—", "a task without a next date shows a dash");
    }

    [Fact]
    public async Task RenderTaskList_printsRoomIntervalAndDurationOnOnePage()
    {
        var (sheet, pdf) = Ok(await Renderer().RenderTaskListAsync(Tasks(SheetLanguage.Nl, HouseholdTasks), Ct));

        sheet.FileName.Should().Be("huishoudtaken.pdf");
        pdf.Pages.Should().Be(1);
        pdf.Text.Should().Contain("Alle huishoudtaken").And.Contain("3 taken");
        pdf.Text.Should().Contain("Badkamer schoonmaken").And.Contain("Badkamer").And.Contain("1x per week").And.Contain("30 min");
        pdf.Text.Should().Contain("Ramen lappen").And.Contain("Keuken").And.Contain("1x per 4 weken").And.Contain("45 min");
        pdf.Text.Should().Contain("(inactief)");
    }

    [Fact]
    public async Task RenderTaskList_english_translatesTheChromeButKeepsUserNames()
    {
        var (_, pdf) = Ok(await Renderer().RenderTaskListAsync(Tasks(SheetLanguage.En, HouseholdTasks), Ct));

        pdf.Text.Should().Contain("All household tasks").And.Contain("Room").And.Contain("Duration").And.Contain("(inactive)");
        pdf.Text.Should().Contain("Badkamer schoonmaken").And.Contain("Badkamer");
        pdf.Text.Should().NotContain("Alle huishoudtaken");
    }

    [Fact]
    public async Task RenderTaskList_noTasks_printsTheEmptyState()
    {
        var (_, pdf) = Ok(await Renderer().RenderTaskListAsync(Tasks(SheetLanguage.Nl), Ct));

        pdf.Pages.Should().Be(1);
        pdf.Text.Should().Contain("Geen taken.");
    }

    [Fact]
    public async Task RenderTaskList_manyTasks_continueOnFurtherPages_withTheHeaderRepeated()
    {
        var tasks = Enumerable.Range(1, 150)
            .Select(i => new TaskListRowInput($"Taak {i:000}", $"Ruimte {i % 7}", "1x per week", 10 + i, true))
            .ToArray();

        var (_, pdf) = Ok(await Renderer().RenderTaskListAsync(Tasks(SheetLanguage.Nl, tasks), Ct));

        pdf.Pages.Should().BeGreaterThan(1);
        pdf.PageTexts.Should().OnlyContain(text => text.Contains("Interval"), "every page repeats the table header");
        pdf.Text.Should().Contain("Taak 001").And.Contain("Taak 150");
        pdf.Text.Should().Contain("150 taken");
    }

    [Fact]
    public async Task RenderDueList_manyRows_continueOnFurtherPages()
    {
        var rows = Enumerable.Range(1, 120)
            .Select(i => new DueRowInput($"Taak {i:000}", "Keuken", "1x per week", i, i % 2 == 0 ? DueState.Overdue : DueState.Due, null))
            .ToArray();

        var (_, pdf) = Ok(await Renderer().RenderDueListAsync(Due(rows), Ct));

        pdf.Pages.Should().BeGreaterThan(1);
        pdf.Text.Should().Contain("Taak 001").And.Contain("Taak 120");
    }

    [Fact]
    public async Task Render_twice_givesByteIdenticalFiles()
    {
        var renderer = Renderer();
        var weekSheet = Schedule(2, SheetOrientation.Landscape, totals: true);

        var week = (await renderer.RenderWeekScheduleAsync(weekSheet, Ct), await renderer.RenderWeekScheduleAsync(weekSheet, Ct));
        var day = (await renderer.RenderDayAsync(Day(), Ct), await renderer.RenderDayAsync(Day(), Ct));
        var due = (await renderer.RenderDueListAsync(Due(new DueRowInput("A", "B", "C", 3, DueState.Due, null)), Ct), await renderer.RenderDueListAsync(Due(new DueRowInput("A", "B", "C", 3, DueState.Due, null)), Ct));
        var tasks = (await renderer.RenderTaskListAsync(Tasks(SheetLanguage.Nl, HouseholdTasks), Ct), await renderer.RenderTaskListAsync(Tasks(SheetLanguage.Nl, HouseholdTasks), Ct));

        foreach (var (first, second) in new[] { week, day, due, tasks })
        {
            second.AsT0.Content.Should().Equal(first.AsT0.Content);
        }
    }

    [Fact]
    public async Task Render_aNewRendererWithTheSameClock_givesTheSameBytes()
    {
        var first = await Renderer().RenderWeekScheduleAsync(Schedule(1), Ct);
        var second = await Renderer().RenderWeekScheduleAsync(Schedule(1), Ct);

        second.AsT0.Content.Should().Equal(first.AsT0.Content);
    }

    [Fact]
    public async Task Render_aDifferentContent_givesDifferentBytes()
    {
        var one = await Renderer().RenderWeekScheduleAsync(Schedule(1), Ct);
        var other = await Renderer().RenderWeekScheduleAsync(Schedule(1, totals: true), Ct);

        other.AsT0.Content.Should().NotEqual(one.AsT0.Content);
    }

    [Fact]
    public async Task Render_aCancelledToken_isNotSwallowedAsAPortError()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => Renderer().RenderDayAsync(Day(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Render_eachSheetToATempFile_producesAPdfThatOpensAgain()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"kthc-pdf-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var renderer = Renderer();
            var results = new[]
            {
                await renderer.RenderWeekScheduleAsync(Schedule(4, totals: true), Ct),
                await renderer.RenderDayAsync(Day(), Ct),
                await renderer.RenderDueListAsync(Due(new DueRowInput("Wastafel", "Badkamer", "1x per week", 8, DueState.Due, null)), Ct),
                await renderer.RenderTaskListAsync(Tasks(SheetLanguage.Nl, HouseholdTasks), Ct),
            };

            foreach (var result in results)
            {
                var rendered = result.AsT0;
                var path = Path.Combine(folder, rendered.FileName);
                await File.WriteAllBytesAsync(path, rendered.Content, Ct);
                new FileInfo(path).Length.Should().BeGreaterThan(1000);
                PdfProbe.Read(await File.ReadAllBytesAsync(path, Ct)).Pages.Should().BeGreaterThan(0);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
