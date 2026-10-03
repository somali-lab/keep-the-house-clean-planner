namespace Huishoudplanner.Domain.Errors;

/// <summary>
/// The actor is known but may not do this particular thing (a rule on the target, beyond the endpoint's role policy).
/// <paramref name="Detail"/> is shown to the client. Maps to <c>403</c> with the problem code <paramref name="Code"/>, <c>permission_denied</c>
/// unless the rule has a code of its own (<c>redemption_locked</c>).
/// </summary>
public sealed record Forbidden(string Detail, string? Code = null);
