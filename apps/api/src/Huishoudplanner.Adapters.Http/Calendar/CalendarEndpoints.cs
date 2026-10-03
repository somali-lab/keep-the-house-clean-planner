using Huishoudplanner.Adapters.Http.OpenApi;
using Huishoudplanner.Adapters.Http.Problems;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Ports.Driving;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Huishoudplanner.Adapters.Http.Calendar;

public static class CalendarEndpoints
{
    public static IEndpointRouteBuilder MapCalendarEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        // Open like the Node reads: no policy, no actor needed.
        routes.MapGet("/api/v2/calendar", async (
                [FromQuery] string? from,
                [FromQuery] string? to,
                ICalendarService calendar,
                ILoggerFactory loggers,
                CancellationToken cancellationToken) =>
            {
                var result = await calendar.GetDaysAsync(from, to, cancellationToken);
                return result.Match(
                    view => Results.Ok(view),
                    errors => ProblemResults.From(errors),
                    missing => ProblemResults.From(missing),
                    error => ProblemResults.From(error, loggers.CreateLogger("Huishoudplanner.Adapters.Http.Calendar")));
            })
            .WithName("getCalendar")
            .WithTags(OpenApiSetup.CalendarTag)
            .WithSummary("Returns the cycle index, week index, ISO week and week start of every day in a range.")
            .WithDescription("Both ends are included. The range is at most 371 days (limits.calendar.maxRangeDays). Day keys are calendar days of the household timezone, which the response names.")
            .Produces<CalendarView>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status500InternalServerError);
        return routes;
    }
}
