namespace Huishoudplanner.Domain.Errors;

/// <summary>
/// The actor is known but may not do this particular thing (a rule on the target, beyond the endpoint's role policy).
/// <paramref name="Detail"/> is shown to the client. Maps to <c>403 permission_denied</c>.
/// </summary>
public sealed record Forbidden(string Detail);
