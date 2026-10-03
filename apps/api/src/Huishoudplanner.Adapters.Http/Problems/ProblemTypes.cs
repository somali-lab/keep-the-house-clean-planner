namespace Huishoudplanner.Adapters.Http.Problems;

/// <summary>The stable problem types of the API: <c>urn:huishoudplanner:problem:&lt;code&gt;</c> (requirements section 8).</summary>
public static class ProblemTypes
{
    public const string Prefix = "urn:huishoudplanner:problem:";

    public const string NotFound = "not_found";
    public const string ValidationError = "validation_error";
    public const string InternalError = "internal_error";

    public static string UrnFor(string code) => Prefix + code;

    /// <summary>The code for a status that no port error chose a code for (framework generated responses).</summary>
    public static string CodeForStatus(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "bad_request",
        StatusCodes.Status401Unauthorized => "unauthorized",
        StatusCodes.Status403Forbidden => "permission_denied",
        StatusCodes.Status404NotFound => NotFound,
        StatusCodes.Status405MethodNotAllowed => "method_not_allowed",
        StatusCodes.Status409Conflict => "conflict",
        StatusCodes.Status415UnsupportedMediaType => "unsupported_media_type",
        StatusCodes.Status422UnprocessableEntity => "unprocessable_entity",
        StatusCodes.Status500InternalServerError => InternalError,
        StatusCodes.Status503ServiceUnavailable => "service_unavailable",
        _ => $"http_{status}",
    };
}
