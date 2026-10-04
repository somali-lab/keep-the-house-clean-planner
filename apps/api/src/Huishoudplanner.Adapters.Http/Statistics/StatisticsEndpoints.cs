using Huishoudplanner.Adapters.Http.Concurrency;
using System.Globalization;
using Huishoudplanner.Adapters.Http.Identity;
using Huishoudplanner.Adapters.Http.Problems;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Statistics;
using Microsoft.Extensions.Logging;
using OneOf;

namespace Huishoudplanner.Adapters.Http.Statistics;

/// <summary>
/// <c>/api/v2/stats</c> (requirements 4.7, 8; <c>routes/stats.ts</c>). The four reports are open like in the Node server; the reset needs an
/// administrator (<c>requireAdmin</c> there, <see cref="AuthorizationPolicies.AdminPolicy"/> here). Query values are bound as strings so that a
/// malformed value is a field-keyed <c>400 validation_error</c>, not a framework binding failure.
/// </summary>
public static class StatisticsEndpoints
{
    public const string StatisticsTag = "Statistics";

    private const string Path = "/api/v2/stats";

    public static IEndpointRouteBuilder MapStatisticsEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet(Path + "/workload", WorkloadAsync)
            .WithName("getWorkloadStatistics")
            .WithTags(StatisticsTag)
            .WithSummary("Planned and done minutes per person for every cycle and week of the period.")
            .WithDescription("The period is the last cycles cycles (1 to 26, default 4) or, with weeks, the last 1 to 3 calendar weeks including the current one. Minutes come from the snapshots on the occurrences: planned minutes belong to the assignee, done minutes to the person who completed the work. Cycles are listed oldest first.")
            .Produces<WorkloadReport>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapGet(Path + "/completion", CompletionAsync)
            .WithName("getCompletionStatistics")
            .WithTags(StatisticsTag)
            .WithSummary("Done, skipped and missed work per task, room or person, worst completion rate first.")
            .WithDescription("groupBy is required: task, room or user. Missed is work still open on a day before today. All one-off tasks share one row without a key when grouped by task; per room they count under the room recorded on the occurrence. The period is as for the workload report.")
            .Produces<CompletionReport>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapGet(Path + "/intervals", IntervalsAsync)
            .WithName("getIntervalStatistics")
            .WithTags(StatisticsTag)
            .WithSummary("The configured interval of each task against the days between its completions.")
            .WithDescription("Most deviating first. averageDays is null with fewer than two completions; one-off tasks have no interval and are left out. The period is as for the workload report.")
            .Produces<IntervalReport>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapGet(Path + "/deviations", DeviationsAsync)
            .WithName("getDeviationStatistics")
            .WithTags(StatisticsTag)
            .WithSummary("Plan changes separated from early or late completion, per task.")
            .WithDescription("The planning shift is the number of days between the original and the current planned day, the completion delay the days between the current planned day and the completion. One-off tasks and recorded extra work have no planned slot and are left out. The period is as for the workload report.")
            .Produces<DeviationReport>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapDelete(Path, ResetAsync)
            .RequireAdmin()
            .WithoutIfMatch("A bulk administrative reset or purge of the history; there is no single entity to version.")
            .WithName("resetStatistics")
            .WithTags(StatisticsTag)
            .WithSummary("Starts the statistics over, or purges the history before a day (administrators).")
            .WithDescription("Without before every occurrence resets to open and recorded extra work is deleted; with before (a day key, not after today) only occurrences, cycles, ledger entries and redemptions strictly older than that day are purged. People, rooms, tasks and cycle plans are never touched. One audit entry records the counts, which the response repeats; a reset always moves the bonus floor forward and is audited then, also when every count is zero; only a reset that removes nothing and leaves the floor where it is changes nothing and writes no audit entry, and the response still shows the zero counts. A before after today is 400 before_in_future.")
            .Produces<StatisticsResetResult>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return routes;
    }

    private static async Task<IResult> WorkloadAsync(
        IStatisticsService statistics,
        ILogger<IStatisticsService> logger,
        CancellationToken cancellationToken,
        string? cycles = null,
        string? weeks = null)
    {
        if (ParsePeriod(cycles, weeks).TryPickT1(out var invalid, out var period))
        {
            return ProblemResults.From(invalid);
        }

        var result = await statistics.WorkloadAsync(period.Cycles, period.Weeks, cancellationToken);
        return result.Match(report => Results.Ok(report), ProblemResults.From, ProblemResults.From, error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> CompletionAsync(
        IStatisticsService statistics,
        ILogger<IStatisticsService> logger,
        CancellationToken cancellationToken,
        string? cycles = null,
        string? weeks = null,
        string? groupBy = null)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var parsed = ParsePeriod(cycles, weeks);
        if (parsed.TryPickT1(out var periodErrors, out var period))
        {
            foreach (var (field, messages) in periodErrors.Errors)
            {
                errors[field] = messages;
            }
        }

        StatsGroupBy group = default;
        if (groupBy is null)
        {
            errors["groupBy"] = ["required"];
        }
        else if (!StatsGroupByNames.TryParse(groupBy, out group))
        {
            errors["groupBy"] = ["must be one of: task, room, user"];
        }

        if (errors.Count > 0)
        {
            return ProblemResults.From(new ValidationErrors(errors));
        }

        var result = await statistics.CompletionAsync(period.Cycles, period.Weeks, group, cancellationToken);
        return result.Match(report => Results.Ok(report), ProblemResults.From, ProblemResults.From, error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> IntervalsAsync(
        IStatisticsService statistics,
        ILogger<IStatisticsService> logger,
        CancellationToken cancellationToken,
        string? cycles = null,
        string? weeks = null)
    {
        if (ParsePeriod(cycles, weeks).TryPickT1(out var invalid, out var period))
        {
            return ProblemResults.From(invalid);
        }

        var result = await statistics.IntervalsAsync(period.Cycles, period.Weeks, cancellationToken);
        return result.Match(report => Results.Ok(report), ProblemResults.From, ProblemResults.From, error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> DeviationsAsync(
        IStatisticsService statistics,
        ILogger<IStatisticsService> logger,
        CancellationToken cancellationToken,
        string? cycles = null,
        string? weeks = null)
    {
        if (ParsePeriod(cycles, weeks).TryPickT1(out var invalid, out var period))
        {
            return ProblemResults.From(invalid);
        }

        var result = await statistics.DeviationsAsync(period.Cycles, period.Weeks, cancellationToken);
        return result.Match(report => Results.Ok(report), ProblemResults.From, ProblemResults.From, error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> ResetAsync(
        HttpContext http,
        IStatisticsService statistics,
        ILogger<IStatisticsService> logger,
        CancellationToken cancellationToken,
        string? before = null)
    {
        DateOnly? boundary = null;
        if (before is not null)
        {
            if (!DayKeys.IsDayKey(before))
            {
                return ProblemResults.From(ValidationErrors.For("before", "invalid_day_key"));
            }

            boundary = DayKeys.Parse(before);
        }

        var result = await statistics.ResetAsync(await ActorOf(http), boundary, cancellationToken);
        return result.Match(
            counts => Results.Ok(counts),
            _ => ProblemResults.Problem(StatusCodes.Status400BadRequest, "before_in_future", "before must not be after today."),
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static OneOf<(int? Cycles, int? Weeks), ValidationErrors> ParsePeriod(string? cycles, string? weeks)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var cycleCount = Integer("cycles", cycles, errors);
        var weekCount = Integer("weeks", weeks, errors);
        return errors.Count > 0 ? new ValidationErrors(errors) : (cycleCount, weekCount);
    }

    private static int? Integer(string field, string? value, Dictionary<string, string[]> errors)
    {
        if (value is null)
        {
            return null;
        }

        if (int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        errors[field] = ["Must be an integer."];
        return null;
    }

    /// <summary>The policy guarantees an actor on every write; a missing one is a programming error and becomes a 500.</summary>
    private static async Task<Actor> ActorOf(HttpContext http) =>
        await http.GetActorAsync() ?? throw new InvalidOperationException("A statistics reset ran without an actor; the endpoint must require authorization.");
}
