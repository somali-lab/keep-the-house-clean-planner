namespace Huishoudplanner.Domain.Ai;

/// <summary>
/// The provider failed, timed out or answered without usable content. Maps to <c>502 ai_provider_error</c>. <paramref name="Message"/>
/// never contains a key, an endpoint or a response body.
/// </summary>
public sealed record AiProviderFailure(string Message);

/// <summary>
/// The answer was not usable for the use case (no JSON, or JSON of the wrong shape). Maps to <c>422 ai_invalid_response</c>;
/// <paramref name="Errors"/> says what was wrong (empty when the answer was no JSON at all).
/// </summary>
public sealed record AiInvalidResponse(string Message, IReadOnlyList<string> Errors);

/// <summary>The proposal still failed validation after the one re-prompt. Maps to <c>422 ai_invalid_plan</c>; nothing was stored.</summary>
public sealed record AiInvalidPlan(IReadOnlyList<string> Errors)
{
    public const string Message = "The AI proposal did not pass validation";
}

/// <summary>The codes of the AI problems (requirements section 8).</summary>
public static class AiProblemCodes
{
    public const string Disabled = "ai_disabled";

    public const string Misconfigured = "ai_misconfigured";

    public const string ProviderError = "ai_provider_error";

    public const string InvalidResponse = "ai_invalid_response";

    public const string InvalidPlan = "ai_invalid_plan";
}
