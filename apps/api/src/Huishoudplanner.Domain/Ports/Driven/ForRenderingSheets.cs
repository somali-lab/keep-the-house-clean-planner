using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Sheets;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>
/// Draws the four printable sheets (requirements section 6) from their view models into PDF bytes. The view model
/// already holds every decision (order, grouping, texts, empty states), so an adapter only lays it out. A failure of
/// the rendering engine comes back as a <see cref="PortError"/>.
/// </summary>
public interface ForRenderingSheets
{
    Task<OneOf<RenderedSheet, PortError>> RenderWeekScheduleAsync(WeekScheduleSheet sheet, CancellationToken cancellationToken);

    Task<OneOf<RenderedSheet, PortError>> RenderDayAsync(DaySheet sheet, CancellationToken cancellationToken);

    Task<OneOf<RenderedSheet, PortError>> RenderDueListAsync(DueListSheet sheet, CancellationToken cancellationToken);

    Task<OneOf<RenderedSheet, PortError>> RenderTaskListAsync(TaskListSheet sheet, CancellationToken cancellationToken);
}
