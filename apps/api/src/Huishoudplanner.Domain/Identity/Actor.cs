namespace Huishoudplanner.Domain.Identity;

/// <summary>The person behind a request. <paramref name="ActorId"/> is the 24-character hex id of the user.</summary>
public sealed record Actor(string ActorId, Role Role, ActorSource Source);
