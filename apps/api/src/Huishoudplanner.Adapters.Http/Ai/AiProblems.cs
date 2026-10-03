using Huishoudplanner.Adapters.Http.Problems;
using Huishoudplanner.Domain.Ai;

namespace Huishoudplanner.Adapters.Http.Ai;

/// <summary>The problems of the AI endpoints (requirements section 8): none of them carries a key, an endpoint or a provider response body.</summary>
internal static class AiProblems
{
    public static IResult From(AiUnavailable unavailable)
    {
        ArgumentNullException.ThrowIfNull(unavailable);
        return unavailable.Reason == AiUnavailableReason.Disabled
            ? ProblemResults.Problem(StatusCodes.Status503ServiceUnavailable, AiProblemCodes.Disabled, unavailable.Detail)
            : ProblemResults.Problem(StatusCodes.Status503ServiceUnavailable, AiProblemCodes.Misconfigured, unavailable.Detail);
    }

    public static IResult From(AiProviderFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return ProblemResults.Problem(StatusCodes.Status502BadGateway, AiProblemCodes.ProviderError, failure.Message);
    }

    /// <summary><c>422 ai_invalid_response</c>; the <c>errors</c> extension lists what was wrong when the answer was JSON of the wrong shape.</summary>
    public static IResult From(AiInvalidResponse invalid)
    {
        ArgumentNullException.ThrowIfNull(invalid);
        return invalid.Errors.Count == 0
            ? ProblemResults.Problem(StatusCodes.Status422UnprocessableEntity, AiProblemCodes.InvalidResponse, invalid.Message)
            : ProblemResults.Problem(
                StatusCodes.Status422UnprocessableEntity,
                AiProblemCodes.InvalidResponse,
                invalid.Message,
                new Dictionary<string, object?> { ["errors"] = invalid.Errors });
    }

    /// <summary><c>422 ai_invalid_plan</c> with the messages of the failed validation in <c>errors</c>.</summary>
    public static IResult From(AiInvalidPlan invalid)
    {
        ArgumentNullException.ThrowIfNull(invalid);
        return ProblemResults.Problem(
            StatusCodes.Status422UnprocessableEntity,
            AiProblemCodes.InvalidPlan,
            AiInvalidPlan.Message,
            new Dictionary<string, object?> { ["errors"] = invalid.Errors });
    }
}
