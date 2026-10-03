namespace Huishoudplanner.Domain.Identity;

/// <summary>The part of a user that identity needs. The users resource itself arrives with slice 1.1.</summary>
public sealed record UserIdentity(string Id, Role Role, bool Active);
