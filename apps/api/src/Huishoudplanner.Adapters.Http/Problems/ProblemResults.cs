using Huishoudplanner.Domain.Errors;
using Microsoft.AspNetCore.Mvc;

namespace Huishoudplanner.Adapters.Http.Problems;

/// <summary>
/// Maps the error values of the ports to RFC 9457 Problem Details. The <c>traceId</c> is added by the
/// pipeline (see <see cref="HttpAdapterExtensions.AddHttpAdapter"/>), so every problem carries one.
/// </summary>
public static class ProblemResults
{
    public const string UnexpectedErrorDetail = "An unexpected error occurred.";

    public static IResult From(NotFound _) =>
        Problem(StatusCodes.Status404NotFound, ProblemTypes.NotFound, "The requested resource was not found.");

    public static IResult From(ConflictError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return Problem(StatusCodes.Status409Conflict, error.Code, error.Detail);
    }

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

    /// <summary>The message of a port error is for logs; the client only learns that something went wrong.</summary>
    public static IResult From(PortError _) =>
        Problem(StatusCodes.Status500InternalServerError, ProblemTypes.InternalError, UnexpectedErrorDetail);

    public static IResult Problem(int status, string code, string detail) =>
        Results.Problem(new ProblemDetails
        {
            Status = status,
            Type = ProblemTypes.UrnFor(code),
            Detail = detail,
        });
}
