namespace Huishoudplanner.Domain.Identity;

/// <summary>
/// The request names no active profile: the header is absent or malformed, or the user is unknown or inactive.
/// Reads still work without an actor; a write answers <c>400 profile_required</c>.
/// </summary>
public readonly record struct ProfileRequired;
