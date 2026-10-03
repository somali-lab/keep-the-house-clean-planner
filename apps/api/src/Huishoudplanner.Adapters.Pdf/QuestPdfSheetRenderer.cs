using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Sheets;
using Microsoft.Extensions.Logging;
using OneOf;
using QuestPDF.Drawing.Exceptions;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace Huishoudplanner.Adapters.Pdf;

/// <summary>
/// <see cref="ForRenderingSheets"/> over QuestPDF (ADR-0020). Rendering twice gives byte-identical files: the layout
/// has no random or clock-dependent part, the PDF metadata dates come from the injected <see cref="TimeProvider"/>
/// (a fixed provider in tests), and QuestPDF writes no random document id. A render that is already running cannot be
/// cancelled: the token is checked before it starts and while it waits for a thread, not inside QuestPDF.
/// </summary>
public sealed partial class QuestPdfSheetRenderer : ForRenderingSheets
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Rendering the sheet {FileName} failed")]
    private static partial void LogRenderFailed(ILogger logger, Exception exception, string fileName);

    private readonly TimeProvider _clock;
    private readonly ILogger? _logger;

    /// <summary>Applies the global QuestPDF settings. Called inside the guarded render, so a failure becomes a PortError instead of an unusable type.</summary>
    private static void Configure()
    {
        // QuestPDF Community licence. It is free for an individual or an organisation with an annual gross revenue
        // under USD 1,000,000 (checked against the licence of QuestPDF 2026.9.1: LICENSE.md in the package, license
        // guide version 3.0). A household planner qualifies. Public-sector entities and publicly traded companies do
        // not qualify at any revenue, and crossing the threshold starts a 90 day transition; recheck this line, and
        // ADR-0020, when the use of the application changes.
        QuestPDF.Settings.License = LicenseType.Community;

        // Fonts come from the machine (the Docker image installs fonts-dejavu-core) and from the Lato font inside the
        // QuestPDF package; see SheetStyle.FontFamilies. A character that no font has (an emoji in a task name) prints as a placeholder instead of failing the whole sheet.
        QuestPDF.Settings.UseSystemFonts = true;
        QuestPDF.Settings.ThrowOnMissingFontFamilies = false;
        QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;
    }

    public QuestPdfSheetRenderer(TimeProvider clock, ILogger<QuestPdfSheetRenderer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        _logger = logger;
    }

    public Task<OneOf<RenderedSheet, PortError>> RenderWeekScheduleAsync(WeekScheduleSheet sheet, CancellationToken cancellationToken) =>
        RenderAsync(sheet?.Language, sheet?.DocumentTitle, sheet?.FileName, document => SheetLayouts.WeekSchedule(document, sheet!), cancellationToken);

    public Task<OneOf<RenderedSheet, PortError>> RenderDayAsync(DaySheet sheet, CancellationToken cancellationToken) =>
        RenderAsync(sheet?.Language, sheet?.DocumentTitle, sheet?.FileName, document => SheetLayouts.Day(document, sheet!), cancellationToken);

    public Task<OneOf<RenderedSheet, PortError>> RenderDueListAsync(DueListSheet sheet, CancellationToken cancellationToken) =>
        RenderAsync(sheet?.Language, sheet?.DocumentTitle, sheet?.FileName, document => SheetLayouts.DueList(document, sheet!), cancellationToken);

    public Task<OneOf<RenderedSheet, PortError>> RenderTaskListAsync(TaskListSheet sheet, CancellationToken cancellationToken) =>
        RenderAsync(sheet?.Language, sheet?.DocumentTitle, sheet?.FileName, document => SheetLayouts.TaskList(document, sheet!), cancellationToken);

    private async Task<OneOf<RenderedSheet, PortError>> RenderAsync(
        SheetLanguage? language,
        string? title,
        string? fileName,
        Action<IDocumentContainer> compose,
        CancellationToken cancellationToken)
    {
        if (language is null || title is null || fileName is null)
        {
            throw new ArgumentNullException(nameof(language), "A sheet is required.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var created = _clock.GetUtcNow();
        var metadata = new DocumentMetadata
        {
            Title = title,
            Creator = "Keep the House Clean",
            Producer = "Keep the House Clean",
            Language = language == SheetLanguage.En ? "en" : "nl",
            CreationDate = created,
            ModifiedDate = created,
        };

        try
        {
            var bytes = await Task.Run(
                () =>
                {
                    Configure();
                    return Document.Create(compose).WithMetadata(metadata).GeneratePdf();
                },
                cancellationToken).ConfigureAwait(false);
            return new RenderedSheet(bytes, fileName, RenderedSheet.PdfContentType);
        }
        catch (Exception e) when (e is DocumentComposeException or DocumentLayoutException or DocumentDrawingException
            or InitializationException or DllNotFoundException or TypeInitializationException or IOException)
        {
            // The message is value-free (exception messages can carry paths); the exception itself goes to the log.
            if (_logger is not null)
            {
                LogRenderFailed(_logger, e, fileName);
            }
            return new PortError($"pdf.render_failed: the sheet could not be rendered ({e.GetType().Name}).");
        }
    }
}
