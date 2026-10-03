namespace Huishoudplanner.Domain.Errors;

/// <summary>The "it worked" variant of a port result that has nothing else to return.</summary>
public readonly record struct Success;

/// <summary>The addressed thing does not exist. Maps to <c>404 not_found</c>.</summary>
public readonly record struct NotFound;

/// <summary>
/// A rule that depends on current state refuses the request. <paramref name="Code"/> is the stable snake_case
/// code of requirements section 8 (for example <c>room_in_use</c>); it becomes part of the problem type.
/// Maps to <c>409</c>.
/// </summary>
public sealed record ConflictError(string Code, string Detail);

/// <summary>Field level problems, keyed by field name. Maps to <c>400 validation_error</c> with an <c>errors</c> extension.</summary>
public sealed record ValidationErrors(IReadOnlyDictionary<string, string[]> Errors)
{
    public static ValidationErrors For(string field, params string[] messages) =>
        new(new Dictionary<string, string[]> { [field] = messages });
}

/// <summary>
/// An infrastructure failure behind a port (database unreachable, provider down). The message is for logs only and
/// is never sent to a client. Maps to <c>500 internal_error</c>.
/// </summary>
public sealed record PortError(string Message);

/// <summary>The installation has no settings document yet (requirements section 8). Maps to <c>500 settings_missing</c>.</summary>
public readonly record struct SettingsMissing;
