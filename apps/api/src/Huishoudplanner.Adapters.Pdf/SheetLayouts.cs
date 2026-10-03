using Huishoudplanner.Domain.Sheets;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Huishoudplanner.Adapters.Pdf;

/// <summary>
/// Draws the view models. Nothing here decides content: the text, order, grouping and empty states come finished
/// from <c>SheetBuilder</c>; this class only places them on A4 pages (requirements section 6.2).
/// </summary>
internal static class SheetLayouts
{
    private const float Gap = 6;
    private const float PersonColumnGap = 4;

    /// <summary>
    /// A page of the week schedule shows seven day rows. The minimum row heights make the table fill the page like the
    /// 230 mm table of the HTML sheets (a landscape page is 190 mm high, so its rows are shorter); a row grows when it holds more.
    /// </summary>
    private const float PortraitRowMm = 31;

    private const float LandscapeRowMm = 20;

    public static void WeekSchedule(IDocumentContainer document, WeekScheduleSheet sheet)
    {
        var size = sheet.Orientation == SheetOrientation.Landscape ? PageSizes.A4.Landscape() : PageSizes.A4;
        foreach (var page in sheet.Pages)
        {
            document.Page(p =>
            {
                Configure(p, size);
                p.Footer().Element(c => Footer(c, page.Footer));
                p.Content().Row(row =>
                {
                    row.Spacing(Gap, Unit.Millimetre);
                    foreach (var week in page.Weeks)
                    {
                        row.RelativeItem().Column(column =>
                        {
                            column.Item().Element(c => WeekHeader(c, week));
                            column.Item().PaddingTop(2, Unit.Millimetre).Element(c => DayTable(c, week.Table, sheet.Orientation == SheetOrientation.Landscape ? LandscapeRowMm : PortraitRowMm));
                        });
                    }
                });
            });
        }
    }

    public static void Day(IDocumentContainer document, DaySheet sheet)
    {
        document.Page(p =>
        {
            Configure(p, PageSizes.A4);
            p.Footer().Element(c => Footer(c, sheet.Footer));
            p.Content().Column(column =>
            {
                column.Item().Element(c => Header(c, sheet.Heading, sheet.Subtitle, sheet.Theme));
                column.Item().PaddingTop(2, Unit.Millimetre).Element(c => DayTable(c, sheet.Table, minRowMm: 0));
            });
        });
    }

    public static void DueList(IDocumentContainer document, DueListSheet sheet)
    {
        document.Page(p =>
        {
            Configure(p, PageSizes.A4);
            p.Footer().Element(c => Footer(c, sheet.Footer));
            p.Content().Column(column =>
            {
                column.Item().Element(c => Header(c, sheet.Heading, sheet.Subtitle, theme: null));
                if (sheet.EmptyMessage is not null)
                {
                    column.Item().PaddingTop(3, Unit.Millimetre).Text(sheet.EmptyMessage);
                    return;
                }

                column.Item().PaddingTop(2, Unit.Millimetre).Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.ConstantColumn(8, Unit.Millimetre);
                        c.RelativeColumn(3);
                        c.RelativeColumn(2);
                        c.RelativeColumn(2);
                        c.ConstantColumn(32, Unit.Millimetre);
                        c.RelativeColumn(2);
                        c.ConstantColumn(22, Unit.Millimetre);
                    });
                    table.Header(h =>
                    {
                        h.Cell().Element(SheetStyle.Cell);
                        foreach (var title in new[] { sheet.Headers.Task, sheet.Headers.Room, sheet.Headers.Interval, sheet.Headers.DaysAgo, sheet.Headers.Status, sheet.Headers.Planned })
                        {
                            h.Cell().Element(SheetStyle.Cell).Text(title).Bold();
                        }
                    });
                    foreach (var row in sheet.Rows)
                    {
                        table.Cell().Element(SheetStyle.Cell).AlignCenter().AlignMiddle().Element(SheetStyle.Checkbox);
                        table.Cell().Element(SheetStyle.Cell).Text(row.Name).Bold();
                        table.Cell().Element(SheetStyle.Cell).Text(row.Room);
                        table.Cell().Element(SheetStyle.Cell).Text(row.Interval);
                        table.Cell().Element(SheetStyle.Cell).Text(row.DaysSince.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        var status = table.Cell().Element(SheetStyle.Cell).Text(row.Status);
                        if (row.Emphasised)
                        {
                            status.Bold();
                        }

                        table.Cell().Element(SheetStyle.Cell).Text(row.Planned);
                    }
                });
            });
        });
    }

    public static void TaskList(IDocumentContainer document, TaskListSheet sheet)
    {
        document.Page(p =>
        {
            Configure(p, PageSizes.A4, SheetStyle.SmallPt);
            p.Footer().Element(c => Footer(c, sheet.Footer));
            p.Content().Column(column =>
            {
                column.Item().Element(c => Header(c, sheet.Heading, sheet.Summary, theme: null));
                if (sheet.EmptyMessage is not null)
                {
                    column.Item().PaddingTop(3, Unit.Millimetre).Text(sheet.EmptyMessage);
                    return;
                }

                column.Item().PaddingTop(3, Unit.Millimetre).Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.ConstantColumn(44, Unit.Millimetre);
                        c.ConstantColumn(60, Unit.Millimetre);
                        c.ConstantColumn(44, Unit.Millimetre);
                        c.ConstantColumn(22, Unit.Millimetre);
                    });
                    table.Header(h =>
                    {
                        h.Cell().Element(Compact).Text(sheet.Headers.Room).Bold();
                        h.Cell().Element(Compact).Text(sheet.Headers.Task).Bold();
                        h.Cell().Element(Compact).Text(sheet.Headers.Interval).Bold();
                        h.Cell().Element(Compact).AlignRight().Text(sheet.Headers.Duration).Bold();
                    });
                    foreach (var row in sheet.Rows)
                    {
                        table.Cell().Element(Compact).Text(row.Room);
                        table.Cell().Element(Compact).Text(t =>
                        {
                            t.Span(row.Name).Bold();
                            if (row.InactiveLabel is not null)
                            {
                                t.Span($" {row.InactiveLabel}").Italic();
                            }
                        });
                        table.Cell().Element(Compact).Text(row.Interval);
                        table.Cell().Element(Compact).AlignRight().Text(row.Duration);
                    }
                });
            });
        });
    }

    private static IContainer Compact(IContainer container) =>
        container.Border(SheetStyle.TableBorderMm, Unit.Millimetre).BorderColor(Colors.Black)
            .PaddingVertical(0.8f, Unit.Millimetre).PaddingHorizontal(1.2f, Unit.Millimetre).AlignMiddle();

    private static void Configure(PageDescriptor page, PageSize size, float bodyPt = SheetStyle.BodyPt)
    {
        page.Size(size);
        page.Margin(SheetStyle.PageMarginMm, Unit.Millimetre);
        page.DefaultTextStyle(SheetStyle.Body(bodyPt));
        page.PageColor(Colors.White);
    }

    private static void Header(IContainer container, string heading, string subtitle, string? theme) =>
        container.Column(column =>
        {
            column.Item().PaddingBottom(1, Unit.Millimetre).Text(heading).FontSize(SheetStyle.TitlePt).Bold();
            column.Item().PaddingBottom(1, Unit.Millimetre).Text(subtitle);
            if (theme is not null)
            {
                column.Item().Text(theme).Bold();
            }
        });

    private static void WeekHeader(IContainer container, WeekView week) => Header(container, week.Title, week.Period, week.Theme);

    private static void Footer(IContainer container, SheetFooter footer) =>
        container.PaddingTop(3, Unit.Millimetre).Row(row =>
        {
            row.Spacing(4, Unit.Millimetre);
            row.AutoItem().Text(footer.GeneratedLabel).FontSize(SheetStyle.SmallPt);
            row.RelativeItem().AlignRight().Text(footer.Note).FontSize(SheetStyle.SmallPt);
        });

    /// <summary>Days as rows: a day column of 18 percent and one task column of 82 percent.</summary>
    private static void DayTable(IContainer container, DayTableView view, float minRowMm) =>
        container.Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.RelativeColumn(18);
                c.RelativeColumn(82);
            });
            table.Header(h =>
            {
                h.Cell().Element(SheetStyle.Cell).Text(view.DayColumnHeader).Bold();
                h.Cell().Element(SheetStyle.Cell).Text(view.TaskColumnHeader).Bold();
            });
            foreach (var row in view.Rows)
            {
                table.Cell().Element(SheetStyle.Cell).MinHeight(minRowMm, Unit.Millimetre).Column(c =>
                {
                    c.Item().Text(row.WeekdayLabel).Bold();
                    c.Item().Text(row.DateLabel).Bold();
                });
                table.Cell().Element(SheetStyle.Cell).MinHeight(minRowMm, Unit.Millimetre).Element(c => DayCell(c, row));
            }
        });

    /// <summary>Person groups in two columns (a single group takes the full width), then the optional day total.</summary>
    private static void DayCell(IContainer container, DayRowView row) =>
        container.Column(column =>
        {
            column.Spacing(1.5f, Unit.Millimetre);
            if (row.Groups.Count == 1)
            {
                column.Item().Element(c => PersonGroup(c, row.Groups[0]));
            }
            else
            {
                for (var i = 0; i < row.Groups.Count; i += 2)
                {
                    var pair = row.Groups.Skip(i).Take(2).ToList();
                    column.Item().Row(r =>
                    {
                        r.Spacing(PersonColumnGap, Unit.Millimetre);
                        r.RelativeItem().Element(c => PersonGroup(c, pair[0]));
                        if (pair.Count == 2)
                        {
                            r.RelativeItem().BorderLeft(0.2f, Unit.Millimetre).BorderColor(Colors.Grey.Medium)
                                .PaddingLeft(3, Unit.Millimetre).Element(c => PersonGroup(c, pair[1]));
                        }
                        else
                        {
                            r.RelativeItem();
                        }
                    });
                }
            }

            if (row.TotalLabel is not null)
            {
                column.Item().PaddingTop(1, Unit.Millimetre).BorderTop(0.2f, Unit.Millimetre).BorderColor(Colors.Black)
                    .PaddingTop(0.5f, Unit.Millimetre).Text(row.TotalLabel).FontSize(SheetStyle.SmallPt);
            }
        });

    private static void PersonGroup(IContainer container, PersonGroupView group) =>
        container.Column(column =>
        {
            column.Item().PaddingBottom(1, Unit.Millimetre).Text(group.Heading).FontSize(SheetStyle.SmallPt).Bold();
            foreach (var line in group.Lines)
            {
                column.Item().PaddingBottom(1, Unit.Millimetre).Row(r =>
                {
                    r.Spacing(1.5f, Unit.Millimetre);
                    r.AutoItem().Element(SheetStyle.Checkbox);
                    r.RelativeItem().Text(t =>
                    {
                        t.Span(line.Name).Bold();
                        if (line.Room is not null)
                        {
                            t.Span($" {line.Room}").Italic();
                        }
                    });
                });
            }
        });
}
