using Huishoudplanner.Adapters.Http.Problems;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Sheets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Microsoft.OpenApi;
using OneOf;

namespace Huishoudplanner.Adapters.Http.Export;

/// <summary>
/// The four printable sheets, <c>GET /api/v2/export/pdf/*</c> (requirements 6 and 8; <c>routes/export.ts</c> of the Node server). Open like in the
/// Node server: no profile needed. Each answers the PDF as <c>application/pdf</c> with <c>Content-Disposition: attachment</c> and a file name that
/// names the period it covers. Query values are bound as strings so that a malformed value is a field-keyed <c>validation_error</c>.
/// </summary>
public static class ExportEndpoints
{
    public const string ExportTag = "Export";

    private const string Path = "/api/v2/export/pdf";

    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet(Path + "/schedule", async (
                [FromQuery] string? fromWeek,
                [FromQuery] string? weeks,
                [FromQuery] string? orientation,
                [FromQuery] string? totals,
                [FromQuery] string? language,
                HttpContext http,
                IExportService export,
                ILoggerFactory loggers,
                CancellationToken cancellationToken) =>
                Answer(http, await export.ExportScheduleAsync(fromWeek, weeks, orientation, totals, language, cancellationToken), loggers))
            .WithName("exportSchedulePdf")
            .WithTags(ExportTag)
            .WithSummary("Downloads the week schedule of one, two or four weeks as a PDF.")
            .WithDescription("Needs no profile. Query: fromWeek (required, an ISO week such as 2026-W38), weeks (required: 1, 2 or 4), orientation (portrait, the default, or landscape; only a landscape sheet of exactly two weeks puts both weeks on one page), totals (true or false, the default: the minutes total per day) and language (nl, the default, or en). The sheet is a blank checklist built from the generated occurrences, so rescheduled and one-off tasks sit where they are and no status is printed. Only generated weeks can be exported: any other week answers 409 weeks_not_generated with the missing ISO weeks in the extension weeks. The file is named after the period, for example huishoudschema-2026-w38-w39.pdf.")
            .Produces<byte[]>(StatusCodes.Status200OK, "application/pdf")
            .AddOpenApiOperationTransformer(PdfAsBinary)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapGet(Path + "/day", async (
                [FromQuery] string? date,
                [FromQuery] string? language,
                HttpContext http,
                IExportService export,
                ILoggerFactory loggers,
                CancellationToken cancellationToken) =>
                Answer(http, await export.ExportDayAsync(date, language, cancellationToken), loggers))
            .WithName("exportDayPdf")
            .WithTags(ExportTag)
            .WithSummary("Downloads the sheet of a single day as a PDF.")
            .WithDescription("Needs no profile. Query: date (required, YYYY-MM-DD) and language (nl, the default, or en). The sheet is the week of that day reduced to it and always carries the day total. A week that has not been generated answers 409 weeks_not_generated. The file is named huishoudschema-YYYY-MM-DD.pdf.")
            .Produces<byte[]>(StatusCodes.Status200OK, "application/pdf")
            .AddOpenApiOperationTransformer(PdfAsBinary)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapGet(Path + "/due", async (
                [FromQuery] string? language,
                HttpContext http,
                IExportService export,
                ILoggerFactory loggers,
                CancellationToken cancellationToken) =>
                Answer(http, await export.ExportDueListAsync(language, cancellationToken), loggers))
            .WithName("exportDueListPdf")
            .WithTags(ExportTag)
            .WithSummary("Downloads the list of tasks that are due or overdue as a PDF.")
            .WithDescription("Needs no profile. Query: language (nl, the default, or en). Overdue tasks come first and the state is spelled out in words; an empty list prints a message instead of a table. The file is named achterstand-YYYY-MM-DD.pdf after today in the household timezone.")
            .Produces<byte[]>(StatusCodes.Status200OK, "application/pdf")
            .AddOpenApiOperationTransformer(PdfAsBinary)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapGet(Path + "/tasks", async (
                [FromQuery] string? language,
                HttpContext http,
                IExportService export,
                ILoggerFactory loggers,
                CancellationToken cancellationToken) =>
                Answer(http, await export.ExportTaskListAsync(language, cancellationToken), loggers))
            .WithName("exportTaskListPdf")
            .WithTags(ExportTag)
            .WithSummary("Downloads the list of all tasks, grouped by room, as a PDF.")
            .WithDescription("Needs no profile. Query: language (nl, the default, or en). Inactive tasks are listed and marked; a task whose room no longer exists is printed under an unknown-room label. One-off tasks are not tasks and are not listed. The file is named huishoudtaken.pdf.")
            .Produces<byte[]>(StatusCodes.Status200OK, "application/pdf")
            .AddOpenApiOperationTransformer(PdfAsBinary)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return routes;
    }

    /// <summary>A byte array is documented as base64 text (<c>format: byte</c>); a download is raw bytes, so the PDF content says <c>binary</c>.</summary>
    private static Task PdfAsBinary(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (operation.Responses is { } responses
            && responses.TryGetValue("200", out var ok)
            && ok.Content is { } content
            && content.TryGetValue("application/pdf", out var pdf))
        {
            pdf.Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" };
        }

        return Task.CompletedTask;
    }

    private static IResult Answer(
        HttpContext http,
        OneOf<RenderedSheet, ValidationErrors, ConflictError, SettingsMissing, PortError> result,
        ILoggerFactory loggers) =>
        result.Match(
            sheet =>
            {
                http.Response.Headers[HeaderNames.ContentDisposition] = AttachmentHeader(sheet.FileName);
                return Results.File(sheet.Content, sheet.ContentType);
            },
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, loggers.CreateLogger("Huishoudplanner.Adapters.Http.Export")));

    /// <summary>RFC 6266: <c>attachment; filename="name"</c> with the quoted-string escapes. The file names are ASCII by construction; anything else is dropped to a placeholder rather than sent raw.</summary>
    internal static string AttachmentHeader(string fileName)
    {
        var safe = new string([.. fileName.Select(c => c is >= ' ' and <= '~' ? c : '_')]);
        return $"attachment; filename=\"{safe.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }
}
