using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Sheets;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

/// <summary>
/// The four printable sheets (requirements section 6): the use case gathers the data, lays the sheet out with <see cref="SheetBuilder"/> and has it
/// drawn. Every parameter is the raw text of the query string (null when absent), so a malformed value becomes one field-keyed
/// <see cref="ValidationErrors"/> answer with all problems at once. Needs no actor; nothing is written. A week without a generated cycle is a
/// <see cref="ConflictError"/> <c>weeks_not_generated</c> carrying the missing ISO weeks in <c>weeks</c>.
/// </summary>
public interface IExportService
{
    /// <param name="fromWeek">An ISO week label such as <c>2026-W38</c>; required.</param>
    /// <param name="weeks"><c>1</c>, <c>2</c> or <c>4</c>; required.</param>
    /// <param name="orientation"><c>portrait</c> (default) or <c>landscape</c>.</param>
    /// <param name="totals"><c>true</c> or <c>false</c> (default): the minutes total per day.</param>
    /// <param name="language"><c>nl</c> (default) or <c>en</c>.</param>
    Task<OneOf<RenderedSheet, ValidationErrors, ConflictError, SettingsMissing, PortError>> ExportScheduleAsync(
        string? fromWeek, string? weeks, string? orientation, string? totals, string? language, CancellationToken cancellationToken);

    /// <param name="day">A <c>YYYY-MM-DD</c> day key; required. The sheet is the week of that day reduced to it.</param>
    Task<OneOf<RenderedSheet, ValidationErrors, ConflictError, SettingsMissing, PortError>> ExportDayAsync(
        string? day, string? language, CancellationToken cancellationToken);

    /// <summary>The tasks that are due or overdue today.</summary>
    Task<OneOf<RenderedSheet, ValidationErrors, ConflictError, SettingsMissing, PortError>> ExportDueListAsync(
        string? language, CancellationToken cancellationToken);

    /// <summary>Every task, active or not, grouped by room.</summary>
    Task<OneOf<RenderedSheet, ValidationErrors, ConflictError, SettingsMissing, PortError>> ExportTaskListAsync(
        string? language, CancellationToken cancellationToken);
}
