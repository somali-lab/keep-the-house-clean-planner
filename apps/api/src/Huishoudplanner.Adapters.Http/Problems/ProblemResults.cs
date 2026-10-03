using Huishoudplanner.Domain.Errors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Huishoudplanner.Adapters.Http.Problems;

/// <summary>
/// Maps the error values of the ports to RFC 9457 Problem Details. The <c>traceId</c> is added by the
/// pipeline (see <see cref="HttpAdapterExtensions.AddHttpAdapter"/>), so every problem carries one.
/// </summary>
public static partial class ProblemResults
{
    public const string UnexpectedErrorDetail = "An unexpected error occurred.";

    public static IResult From(NotFound _) =>
        Problem(StatusCodes.Status404NotFound, ProblemTypes.NotFound, "The requested resource was not found.");

    public static IResult From(ConflictError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Extensions is { Count: > 0 } extensions
            ? Problem(StatusCodes.Status409Conflict, error.Code, error.Detail, extensions)
            : Problem(StatusCodes.Status409Conflict, error.Code, error.Detail);
    }

    public static IResult From(SettingsMissing _) =>
        Problem(StatusCodes.Status500InternalServerError, ProblemTypes.SettingsMissing, "The installation has no settings yet.");

    public static IResult From(ValidationErrors error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var problem = new HttpValidationProblemDetails(error.Errors.ToDictionary(e => e.Key, e => e.Value))
        {
            Status = StatusCodes.Status400BadRequest,
            Type = ProblemTypes.UrnFor(ProblemTypes.ValidationError),
            Title = "Bad Request",
            Detail = "One or more fields are invalid.",
        };
        return Results.Problem(problem);
    }

    /// <summary>The message of a port error is logged here (the seam where it becomes a 500); the client only learns that something went wrong.</summary>
    public static IResult From(PortError error, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(logger);
        LogPortError(logger, error.Message);
        return Problem(StatusCodes.Status500InternalServerError, ProblemTypes.InternalError, UnexpectedErrorDetail);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Port error became a 500: {Reason}")]
    private static partial void LogPortError(ILogger logger, string reason);

    public static IResult Problem(int status, string code, string detail) =>
        Results.Problem(new ProblemDetails
        {
            Status = status,
            Type = ProblemTypes.UrnFor(code),
            Detail = detail,
        });

    /// <summary>A problem with named extension members (requirements section 8: <c>keys</c>, <c>count</c>, <c>weeks</c>, ...).</summary>
    public static IResult Problem(int status, string code, string detail, IReadOnlyDictionary<string, object?> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        var problem = new ProblemDetails
        {
            Status = status,
            Type = ProblemTypes.UrnFor(code),
            Detail = detail,
        };
        foreach (var (name, value) in extensions)
        {
            problem.Extensions[name] = value;
        }

        return Results.Problem(problem);
    }
}
